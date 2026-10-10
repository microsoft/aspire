// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Publishing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NativeHosting;

// stdout is exclusively RPC. Hosting and DCP diagnostics belong on stderr.
Console.SetOut(Console.Error);
var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
{
    Args = args,
    DisableDashboard = true,
    AllowUnsecuredTransport = true,
    TrustDeveloperCertificate = false,
    DeveloperCertificateDefaultHttpsTerminationEnabled = false,
    ContainerRegistryOverride = "docker.io"
});
builder.Services.AddLogging(logging => logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace));
var owner = Guid.NewGuid().ToString("N");
var resources = new Dictionary<string, IResourceBuilder<RedisResource>>(StringComparer.OrdinalIgnoreCase);
IResourceBuilder<IResourceWithEndpoints>? baseline = null;
var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
builder.Eventing.Subscribe<AfterPublishEvent>((@event, cancellationToken) =>
{
    published.TrySetResult();
    return Task.CompletedTask;
});
DistributedApplication? app = null;
ManagedEdges? edges = null;
RpcPeer? peer = null;
peer = new RpcPeer(InvokeAsync);
try
{
    using (peer)
    {
        await peer.RunAsync();
    }
}
finally
{
    if (app is not null)
    {
        using var stopCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await app.StopAsync(stopCancellation.Token);
        await app.DisposeAsync();
    }
}

async Task<JsonNode?> InvokeAsync(string method, JsonObject args, CancellationToken cancellationToken)
{
    if (method == "stats")
    {
        using var process = Process.GetCurrentProcess();
        return new JsonObject
        {
            ["workingSetBytes"] = process.WorkingSet64,
            ["modelResources"] = new JsonArray(builder.Resources.Select(resource => JsonValue.Create(resource.Name)).ToArray()),
            ["assemblies"] = new JsonArray(AppDomain.CurrentDomain.GetAssemblies().Select(assembly => JsonValue.Create(assembly.GetName().Name)).ToArray()),
            ["redisAssembly"] = typeof(RedisResource).Assembly.GetName().Name
        };
    }

    if (method == "addRedis")
    {
        if (app is not null)
        {
            throw new InvalidOperationException("Cannot add resources after the managed application is built.");
        }

        var name = RpcPeer.RequiredString(args, "name");
        // The shipped implementation, including its event handlers and health checks,
        // executes unchanged against real CLR objects in this process.
        resources.Add(name, builder.AddRedis(name));
        return new JsonObject { ["owner"] = owner, ["name"] = name };
    }

    if (method == "addNuxtBaseline")
    {
        if (app is not null || baseline is not null)
        {
            throw new InvalidOperationException("Baseline workload is already configured or the application is built.");
        }

        baseline = AddManagedBaseline(builder, RpcPeer.RequiredString(args, "directory"), resources["cache"]);
        return null;
    }

    if (method == "importNative")
    {
        if (app is not null || edges is not null)
        {
            throw new InvalidOperationException("Import must happen once, before building the application.");
        }

        edges = new ManagedEdges(builder, peer!, owner, RpcPeer.RequiredObject(args, "resource"), resources["cache"], args);
        return edges.Describe();
    }

    if (method.StartsWith("edge", StringComparison.Ordinal))
    {
        if (edges is null)
        {
            throw new InvalidOperationException("Import a native resource first.");
        }

        return await edges.InvokeAsync(method, args, app, cancellationToken);
    }

    if (method == "start")
    {
        if (app is not null)
        {
            throw new InvalidOperationException("Managed application has already started.");
        }

        app = builder.Build();
        await app.StartAsync(cancellationToken);
        if (builder.ExecutionContext.IsPublishMode)
        {
            // Publishing runs in a hosted service. StartAsync alone does not mean
            // the manifest has been flushed; publisher completion stops the host.
            await app.WaitForShutdownAsync(cancellationToken);
            if (!published.Task.IsCompletedSuccessfully)
            {
                throw new InvalidOperationException("Publishing did not complete successfully. See the pipeline error on stderr.");
            }

            return null;
        }

        await Task.WhenAll(resources.Keys.Select(name => app.ResourceNotifications.WaitForResourceHealthyAsync(name, cancellationToken)));
        if (baseline is not null)
        {
            await app.ResourceNotifications.WaitForResourceHealthyAsync(baseline.Resource.Name, cancellationToken);
            return JsonValue.Create(await baseline.GetEndpoint("http").GetValueAsync(cancellationToken));
        }

        return null;
    }

    if (method == "stop")
    {
        if (app is not null)
        {
            await app.StopAsync(cancellationToken);
        }

        return null;
    }

    if (method == "delay")
    {
        await Task.Delay(TimeSpan.FromMinutes(1), cancellationToken);
        return null;
    }

    var reference = RpcPeer.RequiredObject(args, "reference");
    if (method == "resolve" && edges is not null && edges.Owns(reference))
    {
        return await edges.ResolveAsync(args, cancellationToken);
    }

    if (RpcPeer.RequiredString(reference, "owner") != owner || !resources.TryGetValue(RpcPeer.RequiredString(reference, "name"), out var redis))
    {
        throw new InvalidOperationException("Managed resource owner or identity is invalid for this session.");
    }

    if (method == "describe")
    {
        return new JsonObject
        {
            ["name"] = redis.Resource.Name,
            ["properties"] = new JsonArray(((IResourceWithConnectionString)redis.Resource).GetConnectionProperties()
                .Select(property => JsonValue.Create(property.Key)).ToArray())
        };
    }

    if (method == "deploymentInputs")
    {
        if (app is null || !app.ResourceNotifications.TryGetCurrentState(redis.Resource.Name, out var state) ||
            state.Snapshot.Properties.Single(property => property.Name == "container.id").Value is not string containerId)
        {
            throw new InvalidOperationException("Redis has no running container for the deployment-context experiment.");
        }

        if (redis.Resource.TlsEnabled || redis.Resource.PasswordParameter is null)
        {
            throw new InvalidOperationException("This deployment-context fixture requires password-authenticated, non-TLS Redis.");
        }

        return new JsonObject
        {
            ["containerId"] = containerId,
            ["name"] = redis.Resource.Name,
            ["targetPort"] = redis.Resource.PrimaryEndpoint.TargetPort,
            ["passwordParameter"] = redis.Resource.PasswordParameter.Name
        };
    }

    if (method == "resolve")
    {
        if (RpcPeer.RequiredString(args, "property") != "Uri")
        {
            throw new ArgumentException("Prototype resolver supports only the Uri property.");
        }

        if (RpcPeer.RequiredString(args, "mode") == "publish")
        {
            return JsonValue.Create(redis.Resource.UriExpression.ValueExpression);
        }

        if (RpcPeer.RequiredString(args, "mode") != "run" || app is null)
        {
            throw new InvalidOperationException("Run resolution requires a started managed application.");
        }

        // Exercise reentrant RPC: native -> managed -> native describe while the
        // native request is awaiting this value. No CLR Nuxt resource is created.
        var consumer = await peer!.RequestAsync("describeConsumer", new JsonObject
        {
            ["resource"] = RpcPeer.RequiredObject(args, "consumer").DeepClone()
        }, cancellationToken);
        if (consumer?["executable"] is null)
        {
            throw new InvalidOperationException("Expected a native executable consumer.");
        }

        return JsonValue.Create(await redis.Resource.UriExpression.GetValueAsync(cancellationToken));
    }

    throw new InvalidOperationException($"Unknown managed operation '{method}'.");
}

// Do not eagerly load JavaScript integration types into the Redis-only path merely
// because the same executable provides the reduced all-managed comparison.
[MethodImpl(MethodImplOptions.NoInlining)]
static IResourceBuilder<IResourceWithEndpoints> AddManagedBaseline(
    IDistributedApplicationBuilder builder,
    string directory,
    IResourceBuilder<RedisResource> redis)
{
    return builder.AddJavaScriptApp("web", directory)
        .WithHttpEndpoint(env: "PORT")
        .WithEnvironment("HOST", "0.0.0.0")
        .WithEnvironment("NUXT_REDIS_URI", redis.Resource.UriExpression)
        .WithHttpHealthCheck("/api/health")
        .WaitFor(redis);
}
