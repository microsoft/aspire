// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Net;
using Aspire.Hosting.Dcp.Model;
using k8s;
using k8s.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.InternalTesting;
using V2 = Aspire.Hosting.Dcp.Model.V2;

namespace Aspire.Hosting.Tests.Dcp;

public partial class KubernetesServiceTests
{
    [Theory]
    [InlineData("Namespace")]
    [InlineData("LogicalService")]
    [InlineData("LogicalContainerNetwork")]
    [InlineData("LogicalContainer")]
    [InlineData("LogicalProcess")]
    [InlineData("LogicalContainerExec")]
    [InlineData("ServiceEndpoint")]
    [InlineData("PhysicalContainerVolume")]
    [InlineData("V1Cluster")]
    [InlineData("V1Namespaced")]
    public Task ResourceOperationsUseTypeMetadata(string kind)
    {
        const string v2 = "/apis/usvc-dev.developer.microsoft.com/v2";
        const string namespaced = v2 + "/namespaces/app";
        return kind switch
        {
            "Namespace" => AssertResourceRoutesAsync(V2.Namespace.Create("resource"), v2 + "/namespaces", logs: false),
            "LogicalService" => AssertResourceRoutesAsync(V2.LogicalService.Create("resource", "app"), namespaced + "/logicalservices", logs: false),
            "LogicalContainerNetwork" => AssertResourceRoutesAsync(V2.LogicalContainerNetwork.Create("resource", "app"), namespaced + "/logicalcontainernetworks", logs: false),
            "LogicalContainer" => AssertResourceRoutesAsync(V2.LogicalContainer.Create("resource", "app"), namespaced + "/logicalcontainers", logs: true),
            "LogicalProcess" => AssertResourceRoutesAsync(V2.LogicalProcess.Create("resource", "app"), namespaced + "/logicalprocesses", logs: true),
            "LogicalContainerExec" => AssertResourceRoutesAsync(V2.LogicalContainerExec.Create("resource", "app", "container"), namespaced + "/logicalcontainerexecs", logs: true),
            "ServiceEndpoint" => AssertResourceRoutesAsync(V2.ServiceEndpoint.Create("resource", "app", "service"), namespaced + "/serviceendpoints", logs: false),
            "PhysicalContainerVolume" => AssertResourceRoutesAsync(V2.PhysicalContainerVolume.Create("resource", "app", "data"), namespaced + "/physicalcontainervolumes", logs: false),
            "V1Cluster" => AssertResourceRoutesAsync(Container.Create("resource", "image"), "/apis/usvc-dev.developer.microsoft.com/v1/containers", logs: true),
            "V1Namespaced" => AssertResourceRoutesAsync(CreateNamespacedV1Container(), "/apis/usvc-dev.developer.microsoft.com/v1/namespaces/app/containers", logs: true),
            _ => throw new InvalidOperationException(kind)
        };
    }

    private static Container CreateNamespacedV1Container()
    {
        var container = Container.Create("resource", "image");
        container.Metadata.NamespaceProperty = "app";

        return container;
    }

    private async Task AssertResourceRoutesAsync<T>(T resource, string collectionPath, bool logs)
        where T : CustomResource, IKubernetesStaticMetadata
    {
        using var cts = AsyncTestHelpers.CreateDefaultTimeoutTokenSource(TestConstants.LongTimeoutDuration);
        var (service, kubeconfigPath, fileSystem) = CreateService();
        using var disposableFileSystem = fileSystem;
        using var disposableService = service;
        var requests = new ConcurrentQueue<string>();
        var json = KubernetesJson.Serialize(resource);
        await using var server = await StartServerAsync(cts.Token, requestHandler: async context =>
        {
            requests.Enqueue($"{context.Request.Method} {context.Request.Path}{context.Request.QueryString}");
            context.Response.ContentType = "application/json";
            if (HttpMethods.IsPost(context.Request.Method))
            {
                var created = KubernetesJson.Deserialize<T>(await new StreamReader(context.Request.Body).ReadToEndAsync(context.RequestAborted));
                Assert.Equal(resource.ApiVersion, created.ApiVersion);
                Assert.Equal(resource.Kind, created.Kind);
                Assert.Equal(resource.Namespace(), created.Namespace());
            }

            if (context.Request.Query["watch"] == "true")
            {
                await context.Response.WriteAsync($$"""{"type":"ADDED","object":{{json}}}""" + "\n", context.RequestAborted);
                await context.Response.Body.FlushAsync(context.RequestAborted);
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted);
                }
                catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
                {
                    // Closing the enumerator closes its HTTP/2 stream.
                }
            }
            else if (context.Request.Path.Value!.EndsWith("/log", StringComparison.Ordinal))
            {
                await context.Response.WriteAsync("resource output\n", context.RequestAborted);
            }
            else if (context.Request.Path == collectionPath && HttpMethods.IsGet(context.Request.Method))
            {
                await context.Response.WriteAsync($$"""{"items":[{{json}}]}""", context.RequestAborted);
            }
            else
            {
                await context.Response.WriteAsync(json, context.RequestAborted);
            }
        });
        WriteKubeconfig(kubeconfigPath, server.Port);

        var created = await service.CreateAsync(resource, cts.Token);
        Assert.Equal(resource.ApiVersion, created.ApiVersion);
        Assert.Equal(resource.Kind, created.Kind);
        Assert.Equal(resource.Metadata.Name, (await service.GetAsync<T>("resource", resource.Namespace(), cts.Token)).Metadata.Name);
        await service.PatchAsync(resource, new V1Patch("[]", V1Patch.PatchType.JsonPatch), cts.Token);
        Assert.Single(await service.ListAsync<T>(resource.Namespace(), cts.Token));
        await using (var watch = service.WatchAsync<T>(resource.Namespace(), cts.Token).GetAsyncEnumerator())
        {
            Assert.True(await watch.MoveNextAsync());
            Assert.Equal(WatchEventType.Added, watch.Current.Item1);
            Assert.Equal(resource.ApiVersion, watch.Current.Item2.ApiVersion);
        }

        if (logs)
        {
            await using var stream = await service.GetLogStreamAsync(resource, "stdout", cts.Token, follow: false, timestamps: true, lineNumbers: true, limit: 3, skip: 1);
            using var reader = new StreamReader(stream);
            Assert.Equal("resource output\n", await reader.ReadToEndAsync(cts.Token));
        }
        await service.DeleteAsync<T>("resource", resource.Namespace(), cts.Token);

        string[] expected =
        [
            $"POST {collectionPath}", $"GET {collectionPath}/resource", $"PATCH {collectionPath}/resource",
            $"GET {collectionPath}", $"GET {collectionPath}?watch=true",
            .. logs ? new[] { $"GET {collectionPath}/resource/log?follow=false&timestamps=true&source=stdout&line_numbers=true&limit=3&skip=1" } : [],
            $"DELETE {collectionPath}/resource"
        ];
        Assert.Equal(expected, requests.ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task V2CreateRecoveryUsesSameVersionAndScope(bool clusterScoped)
    {
        if (clusterScoped)
        {
            await AssertCreateRecoveryRoutesAsync(V2.Namespace.Create("resource"), "/apis/usvc-dev.developer.microsoft.com/v2/namespaces");
        }
        else
        {
            await AssertCreateRecoveryRoutesAsync(V2.LogicalProcess.Create("resource", "app"), "/apis/usvc-dev.developer.microsoft.com/v2/namespaces/app/logicalprocesses");
        }
    }

    private async Task AssertCreateRecoveryRoutesAsync<T>(T resource, string collectionPath)
        where T : CustomResource, IKubernetesStaticMetadata
    {
        using var cts = AsyncTestHelpers.CreateDefaultTimeoutTokenSource(TestConstants.LongTimeoutDuration);
        var (service, kubeconfigPath, fileSystem) = CreateService();
        using var disposableFileSystem = fileSystem;
        using var disposableService = service;
        var requests = new ConcurrentQueue<string>();
        await using var server = await StartServerAsync(cts.Token, requestHandler: async context =>
        {
            requests.Enqueue($"{context.Request.Method} {context.Request.Path}");
            if (HttpMethods.IsPost(context.Request.Method))
            {
                await WriteStatusAsync(context, StatusCodes.Status504GatewayTimeout, "Timeout");
            }
            else
            {
                await context.Response.WriteAsync(KubernetesJson.Serialize(resource), context.RequestAborted);
            }
        });
        WriteKubeconfig(kubeconfigPath, server.Port);

        var recovered = await service.CreateAsync(resource, cts.Token);

        Assert.Equal(resource.Kind, recovered.Kind);
        Assert.Equal(resource.ApiVersion, recovered.ApiVersion);
        Assert.Equal([$"POST {collectionPath}", $"GET {collectionPath}/resource"], requests.ToArray());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DeleteWithUidPreconditionPreservesReplacements(bool conflict)
    {
        using var cts = AsyncTestHelpers.CreateDefaultTimeoutTokenSource();
        var (service, kubeconfigPath, fileSystem) = CreateService();
        using var disposableFileSystem = fileSystem;
        using var disposableService = service;
        var resource = V2.Namespace.Create("app");
        resource.Metadata.Uid = "owned-namespace";
        var requests = new ConcurrentQueue<string>();
        await using var server = await StartServerAsync(cts.Token, requestHandler: async context =>
        {
            requests.Enqueue($"{context.Request.Method} {context.Request.Path}");
            var options = KubernetesJson.Deserialize<V1DeleteOptions>(await new StreamReader(context.Request.Body).ReadToEndAsync(context.RequestAborted));
            Assert.Equal("owned-namespace", options.Preconditions.Uid);
            if (conflict)
            {
                await WriteStatusAsync(context, StatusCodes.Status409Conflict, "Conflict");
            }
            else
            {
                await context.Response.WriteAsync(KubernetesJson.Serialize(resource), context.RequestAborted);
            }
        });
        WriteKubeconfig(kubeconfigPath, server.Port);
        if (conflict)
        {
            await Assert.ThrowsAsync<k8s.Autorest.HttpOperationException>(() => service.DeleteAsync(resource, cts.Token));
        }
        else
        {
            Assert.Equal(resource.Metadata.Uid, (await service.DeleteAsync(resource, cts.Token)).Metadata.Uid);
        }
        Assert.Equal(["DELETE /apis/usvc-dev.developer.microsoft.com/v2/namespaces/app"], requests.ToArray());
    }

    [Fact]
    public async Task NamespaceRejectsNamespacedOperations()
    {
        var (service, _, fileSystem) = CreateService();
        using var disposableFileSystem = fileSystem;
        using var disposableService = service;
        var resource = V2.Namespace.Create("app");
        resource.Metadata.NamespaceProperty = "wrong";

        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(resource));
        await Assert.ThrowsAsync<ArgumentException>(() => service.GetAsync<V2.Namespace>("app", "wrong"));
        await Assert.ThrowsAsync<ArgumentException>(() => service.ListAsync<V2.Namespace>("wrong"));
        await Assert.ThrowsAsync<ArgumentException>(() => service.DeleteAsync<V2.Namespace>("app", "wrong"));
        await Assert.ThrowsAsync<ArgumentException>(() => service.PatchAsync(resource, new V1Patch("[]", V1Patch.PatchType.JsonPatch)));
        await Assert.ThrowsAsync<ArgumentException>(() => service.GetLogStreamAsync(resource, "stdout"));
        await using var watch = service.WatchAsync<V2.Namespace>("wrong").GetAsyncEnumerator();
        await Assert.ThrowsAsync<ArgumentException>(() => watch.MoveNextAsync().AsTask());
    }

    [Fact]
    public async Task TestServiceSeparatesKindsVersionsAndNamespaces()
    {
        var service = new TestKubernetesService();
        var v1 = Container.Create("same", "image");
        var first = V2.LogicalContainer.Create("same", "first");
        var second = V2.LogicalContainer.Create("same", "second");
        var process = V2.LogicalProcess.Create("same", "first");
        await service.CreateAsync(v1);
        await service.CreateAsync(first);
        await service.CreateAsync(second);
        await service.CreateAsync(process);
        await service.DeleteAsync<V2.LogicalContainer>("same", "first");

        var missing = await Assert.ThrowsAsync<k8s.Autorest.HttpOperationException>(() => service.GetAsync<V2.LogicalContainer>("same", "first"));
        Assert.Equal(HttpStatusCode.NotFound, missing.Response.StatusCode);
        Assert.Equal("v1", (await service.GetAsync<Container>("same")).ApiVersion.Split('/')[1]);
        Assert.Equal("second", (await service.GetAsync<V2.LogicalContainer>("same", "second")).Namespace());
        Assert.Equal("first", (await service.GetAsync<V2.LogicalProcess>("same", "first")).Namespace());
        Assert.Single(await service.ListAsync<V2.LogicalContainer>());
        Assert.Empty(await service.ListAsync<V2.LogicalContainer>("first"));

        var recreated = await service.CreateAsync(first);
        var patched = await service.PatchAsync(recreated, new V1Patch(
            System.Text.Json.Nodes.JsonNode.Parse("""[{"op":"add","path":"/spec/stop","value":true}]""")!.AsArray(), V1Patch.PatchType.JsonPatch));
        Assert.True(patched.Spec.Stop);
        Assert.NotEqual(recreated.Metadata.ResourceVersion, patched.Metadata.ResourceVersion);
        Assert.True((await service.GetAsync<V2.LogicalContainer>("same", "first")).Spec.Stop);

        using var cts = AsyncTestHelpers.CreateDefaultTimeoutTokenSource();
        await using var watch = service.WatchAsync<V2.LogicalContainer>("second", cts.Token).GetAsyncEnumerator();
        Assert.True(await watch.MoveNextAsync());
        Assert.Equal("second", watch.Current.Item2.Namespace());
    }
}
