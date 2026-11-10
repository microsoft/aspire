// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREEXTENSION001 // Verify authoritative Functions launch metadata after a real coordinated build.
#pragma warning disable ASPIREPIPELINES003 // Exercise SDK container publication against the local runtime.
#pragma warning disable ASPIRECONTAINERRUNTIME001 // Inspect and clean up the test-owned image.

using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Publishing;
using Aspire.Hosting.Testing;
using Aspire.Hosting.Tests.Publishing;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Aspire.TestUtilities;
using Azure.Storage.Queues;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Azure.Tests;

public class AzureFunctionsV2RuntimeTests(ITestOutputHelper outputHelper)
{
    [Fact]
    [RequiresFeature(TestFeature.ContainerRuntime)]
    [RequiresTools(["func"])]
    public async Task CSharpFunctionsHostUsesCoordinatedOutputAndQueueBinding()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var projectPath = AzureFunctionsTestHelpers.CreateRuntimeProject(workspace.Path);
        using var builder = TestDistributedApplicationBuilder.Create(
            options => options.ProjectDirectory = workspace.Path,
            outputHelper);
        var storage = builder.AddAzureStorage("storage").RunAsEmulator();
        var queues = storage.AddQueues("queue");
        var functions = builder.AddDotnetAzureFunction("functions", projectPath)
            .WithBuildEnvironment("BUILD_FLAVOR", "functions-v2")
            .WithHostStorage(storage)
            .WithReference(queues)
            .WaitFor(storage)
            .WithHttpHealthCheck("/api/health");
        using var app = builder.Build();

        try
        {
            using (var startup = new CancellationTokenSource(TimeSpan.FromMinutes(5)))
            {
                await app.StartAsync(startup.Token);
            }

            using (var ready = new CancellationTokenSource(TimeSpan.FromMinutes(3)))
            {
                await app.Services.GetRequiredService<ResourceNotificationService>()
                    .WaitForResourceHealthyAsync(functions.Resource.Name, ready.Token);
            }

            await VerifyHttpAndQueueBindingAsync(app, functions.Resource.Name, queues.Resource, "functions-v2");

            var metadata = functions.Resource.GetProjectMetadata();
            Assert.True(metadata.SuppressBuild);
            Assert.Equal("functions-v2", metadata.BuildEnvironment["BUILD_FLAVOR"]);
            Assert.Equal(Path.GetDirectoryName(projectPath), metadata.BuildWorkingDirectory);
            Assert.Contains("functions-v2", functions.Resource.WorkingDirectory, StringComparison.Ordinal);
            Assert.Equal(["built"], File.ReadAllLines(Path.Combine(Path.GetDirectoryName(projectPath)!, "build-count.txt")));

            var arguments = await ArgumentEvaluator.GetArgumentListAsync(functions.Resource, app.Services);
            if (OperatingSystem.IsWindows())
            {
                Assert.Equal("cmd", Path.GetFileNameWithoutExtension(functions.Resource.Command), ignoreCase: true);
                Assert.Equal(["/C", "func", "start"], arguments.Take(3));
            }
            else
            {
                Assert.Equal("func", Path.GetFileNameWithoutExtension(functions.Resource.Command));
                Assert.Equal("start", arguments[0]);
            }

            var launch = await functions.Resource.CreateLaunchConfigurationAsync("NoDebug", TestContext.Current.CancellationToken);
            var json = JsonSerializer.SerializeToElement(launch);
            Assert.Equal("azure-functions-with-external-build.v1", json.GetProperty("type").GetString());
            Assert.Equal(projectPath, json.GetProperty("project_path").GetString());
            Assert.True(json.GetProperty("suppress_build").GetBoolean());
            Assert.Equal(metadata.BuildWorkingDirectory, json.GetProperty("build_working_directory").GetString());
            Assert.Equal("functions-v2", json.GetProperty("build_environment").GetProperty("BUILD_FLAVOR").GetString());
            var configuration = builder.AppHostAssembly?.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration;
            if (configuration is not null)
            {
                Assert.Equal(configuration, json.GetProperty("build_configuration").GetString());
            }

            await Verify(json.GetRawText(), "json")
                .AddScrubber(text => text
                    .Replace(JsonEncodedText.Encode(projectPath).ToString(), "Functions.csproj")
                    .Replace(JsonEncodedText.Encode(metadata.BuildWorkingDirectory!).ToString(), "Functions")
                    .Replace($"\"build_configuration\":\"{configuration}\"", "\"build_configuration\":\"Configuration\""));
        }
        catch
        {
            workspace.Preserve();
            throw;
        }
        finally
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromMinutes(1));
            await app.StopAsync(stop.Token);
        }
    }

    [Fact]
    [RequiresFeature(TestFeature.ContainerRuntime)]
    [RequiresTools(["func"])]
    public async Task CSharpFunctionsReplicasUseDistinctPortsAndSharedPublicEndpoint()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var projectPath = AzureFunctionsTestHelpers.CreateRuntimeProject(workspace.Path);
        AzureFunctionsTestHelpers.WriteLaunchSettings(projectPath, new LaunchSettings
        {
            Profiles = new Dictionary<string, LaunchProfile>
            {
                ["Functions"] = new() { CommandName = "Project", CommandLineArgs = "--port 7071" }
            }
        });
        // Keep the testing host's port randomization enabled. The fixed profile port
        // still exercises reconciliation, without binding a shared development port.
        using var builder = TestDistributedApplicationBuilder.Create(
            options => options.ProjectDirectory = workspace.Path, outputHelper);
        var storage = builder.AddAzureStorage("storage").RunAsEmulator();
        var functions = builder.AddDotnetAzureFunction("functions", projectPath)
            .WithBuildEnvironment("BUILD_FLAVOR", "replicas")
            .WithHostStorage(storage)
            .WithReplicas(2)
            .WaitFor(storage)
            .WithHttpHealthCheck("/api/health");
        using var app = builder.Build();

        try
        {
            using (var startup = new CancellationTokenSource(TimeSpan.FromMinutes(5)))
            {
                await app.StartAsync(startup.Token);
            }

            var notifications = app.ResourceNotifications;
            using var ready = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            await notifications.WaitForResourceHealthyAsync(functions.Resource.Name, ready.Token);
            var first = await notifications.WaitForResourceAsync(functions.Resource.Name,
                resourceEvent => resourceEvent.Snapshot.State?.Text == KnownResourceStates.Running
                    && resourceEvent.Snapshot.Properties.Any(property => property.Name == "executable.pid" && property.Value is int),
                ready.Token);
            var second = await notifications.WaitForResourceAsync(functions.Resource.Name,
                resourceEvent => resourceEvent.ResourceId != first.ResourceId
                    && resourceEvent.Snapshot.State?.Text == KnownResourceStates.Running
                    && resourceEvent.Snapshot.Properties.Any(property => property.Name == "executable.pid" && property.Value is int),
                ready.Token);
            var instances = new[] { first, second };
            Assert.Equal(2, instances.Select(instance =>
                Assert.Single(instance.Snapshot.Properties, property => property.Name == "executable.pid").Value).Distinct().Count());
            var ports = instances.Select(instance =>
            {
                // Core Tools receives ["start", "--port", "<allocated port>", ...].
                // Inspect resolved DCP argv rather than the shared endpoint template.
                var arguments = Assert.IsAssignableFrom<IEnumerable<string>>(
                    Assert.Single(instance.Snapshot.Properties, property => property.Name == "executable.args").Value).ToArray();
                Assert.Equal(1, arguments.Count(argument => argument == "--port"));
                return int.Parse(arguments[Array.IndexOf(arguments, "--port") + 1], CultureInfo.InvariantCulture);
            }).ToArray();
            Assert.Equal(2, ports.Distinct().Count());
            using var requests = new CancellationTokenSource(TimeSpan.FromMinutes(1));
            using var proxyClient = app.CreateHttpClient(functions.Resource.Name);
            Assert.Equal("replicas|", await proxyClient.GetStringAsync("/api/health", requests.Token));
            foreach (var port in ports)
            {
                Assert.NotEqual(proxyClient.BaseAddress!.Port, port);
                using var directClient = new HttpClient { BaseAddress = new Uri($"http://localhost:{port}") };
                Assert.Equal("replicas|", await directClient.GetStringAsync("/api/health", requests.Token));
            }
            Assert.Equal(["built"], File.ReadAllLines(Path.Combine(Path.GetDirectoryName(projectPath)!, "build-count.txt")));
        }
        catch
        {
            workspace.Preserve();
            throw;
        }
        finally
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromMinutes(1));
            await app.StopAsync(stop.Token);
        }
    }

    [Fact]
    [RequiresFeature(TestFeature.ContainerRuntime | TestFeature.ContainerImageBuild)]
    public async Task CSharpFunctionsSdkImageStartsFunctionsHostAndQueueBinding()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var projectPath = AzureFunctionsTestHelpers.CreateRuntimeProject(workspace.Path);
        var archivePath = Path.Combine(workspace.Path, "functions.tar.gz");
        var imageName = $"localhost/functions-v2-{Guid.NewGuid():N}";
        var imageReference = $"{imageName}:sdk-test";
        using var publisher = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputHelper);
        var functions = publisher.AddDotnetAzureFunction("functions", projectPath)
            .WithBuildEnvironment("BUILD_FLAVOR", "functions-v2-image")
            .WithContainerBuildOptions(context =>
            {
                context.Destination = ContainerImageDestination.Archive;
                context.ImageFormat = ContainerImageFormat.Docker;
                context.LocalImageName = imageName;
                context.LocalImageTag = "sdk-test";
                context.OutputPath = archivePath;
                context.TargetPlatform = ContainerTargetPlatform.LinuxAmd64;
            });
        using var publishApp = publisher.Build();
        using var build = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var runtime = await publishApp.Services.GetRequiredService<IContainerRuntimeResolver>().ResolveAsync(build.Token);

        try
        {
            await publishApp.Services.GetRequiredService<IResourceContainerImageManager>()
                .BuildImageAsync(functions.Resource, build.Token);
            Assert.Equal([imageReference], TestContainerImageArchive.ReadDockerImageReferences(archivePath));
            await AzureFunctionsTestHelpers.RunContainerRuntimeAsync(runtime, ["image", "load", "--input", archivePath], build.Token);
            var inspection = await runtime.InspectImageConfigAsync(imageReference, build.Token);
            Assert.True(inspection.TryGetConfig(out var imageConfig), inspection.ErrorMessage);
            Assert.True(
                imageConfig.Entrypoint.Concat(imageConfig.Command).Any(argument =>
                    argument.Contains("azure-functions-host", StringComparison.Ordinal) ||
                    argument.Contains("start_nonappservice.sh", StringComparison.Ordinal)),
                "The published image must start the Functions host, not the isolated worker directly.");

            foreach (var targetPort in new[] { 8080, 9090 })
            {
                if (targetPort == 9090)
                {
                    functions.WithEndpointsInEnvironment(_ => false).WithEnvironment("ASPNETCORE_URLS", "http://+:9090");
                }
                var publishedEnvironment = await AzureFunctionsTestHelpers.GetEnvironmentAsync(publisher, functions.Resource);
                var listeningAddress = publishedEnvironment["ASPNETCORE_URLS"] switch
                {
                    string value => value,
                    ReferenceExpression expression => await expression.GetValueAsync(build.Token)
                        ?? throw new InvalidOperationException("The published Functions listening address could not be resolved."),
                    _ => throw new InvalidOperationException("The published Functions listening address has an unexpected value type.")
                };
                Assert.Equal($"http://+:{targetPort}", listeningAddress);

                using var builder = TestDistributedApplicationBuilder.Create(outputHelper);
                var storage = builder.AddAzureStorage("storage").RunAsEmulator();
                var queues = storage.AddQueues("queue");
                builder.AddContainer("functions", imageName, "sdk-test")
                    .WithHttpEndpoint(targetPort: targetPort)
                    .WithEnvironment("FUNCTIONS_WORKER_RUNTIME", "dotnet-isolated")
                    .WithEnvironment("ASPNETCORE_URLS", listeningAddress)
                    .WithEnvironment(context =>
                    {
                        ((IResourceWithAzureFunctionsConfig)storage.Resource).ApplyAzureFunctionsConfiguration(context.EnvironmentVariables, "AzureWebJobsStorage");
                        ((IResourceWithAzureFunctionsConfig)queues.Resource).ApplyAzureFunctionsConfiguration(context.EnvironmentVariables, "queue");
                    })
                    .WaitFor(storage)
                    .WithHttpHealthCheck("/api/health");
                using var app = builder.Build();
                try
                {
                    using (var startup = new CancellationTokenSource(TimeSpan.FromMinutes(3)))
                    {
                        await app.StartAsync(startup.Token);
                    }

                    using (var ready = new CancellationTokenSource(TimeSpan.FromMinutes(3)))
                    {
                        await app.Services.GetRequiredService<ResourceNotificationService>()
                            .WaitForResourceHealthyAsync("functions", ready.Token);
                    }

                    await VerifyHttpAndQueueBindingAsync(app, "functions", queues.Resource, "functions-v2-image");
                }
                finally
                {
                    using var stop = new CancellationTokenSource(TimeSpan.FromMinutes(1));
                    await app.StopAsync(stop.Token);
                }
            }
            await Verify(new { imageConfig.Entrypoint, imageConfig.Command, imageConfig.WorkingDirectory });
        }
        catch
        {
            workspace.Preserve();
            throw;
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromMinutes(1));
            try
            {
                await runtime.RemoveImageAsync(imageReference, cleanup.Token);
            }
            catch (Exception exception)
            {
                outputHelper.WriteLine($"Could not remove test-owned image {imageReference}: {exception.Message}");
            }
        }
    }

    private static async Task VerifyHttpAndQueueBindingAsync(
        DistributedApplication app,
        string resourceName,
        AzureQueueStorageResource queues,
        string buildFlavor)
    {
        using var operation = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        using var client = app.CreateHttpClient(resourceName);
        Assert.Equal($"{buildFlavor}|", await client.GetStringAsync("/api/health", operation.Token));
        using var response = await client.PostAsync("/api/enqueue", new StringContent("functions-v2-roundtrip"), operation.Token);
        response.EnsureSuccessStatusCode();

        var queueClient = new QueueClient(
            await queues.ConnectionStringExpression.GetValueAsync(operation.Token),
            "roundtrip",
            new QueueClientOptions { MessageEncoding = QueueMessageEncoding.Base64 });
        var message = (await queueClient.ReceiveMessageAsync(cancellationToken: operation.Token)).Value;
        Assert.NotNull(message);
        Assert.Equal("functions-v2-roundtrip", message.MessageText);
        await queueClient.DeleteMessageAsync(message.MessageId, message.PopReceipt, operation.Token);
    }
}
