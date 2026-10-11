// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.JavaScript;
using Microsoft.Extensions.DependencyInjection;

namespace NativeHosting;

internal sealed class ManagedEdges
{
    private readonly IDistributedApplicationBuilder _builder;
    private readonly RpcPeer _peer;
    private readonly JsonObject _native;
    private readonly JsonObject _callbackHandle;
    private readonly IResourceBuilder<NativeFacade> _web;
    private readonly IResourceBuilder<ExecutableResource> _consumer;
    private readonly IResourceBuilder<RedisResource> _cache;
    private readonly EndpointReference _heldEndpoint;
    private readonly ConcurrentDictionary<string, object> _values = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _stateGate = new(1);
    private int _generation;
    private int _callbackCalls;
    private bool _graphCallbackRan;
    private bool _endpointsAllocated;
    private volatile bool _invalidated;

    public ManagedEdges(IDistributedApplicationBuilder builder, RpcPeer peer, string owner,
        JsonObject native, IResourceBuilder<RedisResource> cache, JsonObject args)
    {
        _builder = builder;
        _peer = peer;
        _cache = cache;
        _native = (JsonObject)native.DeepClone();
        _callbackHandle = new JsonObject { ["owner"] = owner, ["name"] = "web-environment" };
        _web = builder.AddResource(new NativeFacade(RpcPeer.RequiredString(native, "name")))
            .WithInitialState(new CustomResourceSnapshot
            {
                ResourceType = "native-executable", Properties = [], State = KnownResourceStates.Starting
            })
            .WithHttpEndpoint(targetPort: 3000, isProxied: false)
            .WithReference(cache)
            .WithReference(cache, connectionName: "my-db")
            .WithEnvironment("NUXT_REDIS_URI", cache.Resource.UriExpression)
            .WithEnvironment("MANAGED_LITERAL", "configured-through-CSharp")
            .WithEnvironment(async context =>
            {
                Interlocked.Increment(ref _callbackCalls);
                var description = await _peer.RequestAsync("describeNative",
                    new JsonObject { ["resource"] = _native.DeepClone() }, context.CancellationToken);
                context.EnvironmentVariables["MANAGED_CALLBACK"] = RpcPeer.RequiredString((JsonObject)description!, "name");
            })
            .WithHttpHealthCheck("/api/health")
            .WaitFor(cache)
            .OnBeforeResourceStarted((resource, @event, cancellationToken) =>
            {
                builder.CreateResourceBuilder(resource).WithEnvironment("MANAGED_BEFORE_RESOURCE", "before-native-launch");
                return Task.CompletedTask;
            })
            .WithCommand("probe-owner", "Probe native owner", async context =>
            {
                await _peer.RequestAsync("describeNative",
                    new JsonObject { ["resource"] = _native.DeepClone() }, context.CancellationToken);
                return new ExecuteCommandResult { Success = true, Message = "Native owner responded." };
            });
        _heldEndpoint = _web.GetEndpoint("http");
        _consumer = builder.AddExecutable("managed-consumer", RpcPeer.RequiredString(args, "node"),
                RpcPeer.RequiredString(args, "directory"), RpcPeer.RequiredString(args, "consumerScript"))
            .WithReference(_web)
            .WithEnvironment("NATIVE_URL", _heldEndpoint)
            .WithEnvironment("PROBE_RESULT", RpcPeer.RequiredString(args, "resultPath"))
            .WaitFor(_web);

        builder.OnBeforeStart((@event, cancellationToken) =>
        {
            // An unchanged graph-wide callback sees a CLR facade, not an executable.
            // The annotation stays local until the explicit environment-export phase.
            var resource = @event.Model.Resources.Single(resource => resource.Name == _web.Resource.Name);
            if (resource is not IResourceWithEnvironment withEnvironment)
            {
                throw new InvalidOperationException("Imported facade does not support environment annotations.");
            }

            builder.CreateResourceBuilder(withEnvironment).WithEnvironment("MANAGED_GRAPH", "visible-in-before-start");
            _graphCallbackRan = true;
            return Task.CompletedTask;
        });

        if (args["bridgePublisher"]?.GetValue<bool>() == true)
        {
            _web.WithManifestPublishingCallback(async context =>
            {
                var description = (JsonObject)(await _peer.RequestAsync("describeNative",
                    new JsonObject { ["resource"] = _native.DeepClone() }, context.CancellationToken))!;
                // This is an experimental manifest extension, not a supported deployment
                // target. Standard writers preserve the real C# environment and bindings.
                context.Writer.WriteString("type", "native-executable.v0");
                context.Writer.WriteString("command", RpcPeer.RequiredString(description, "executable"));
                context.Writer.WritePropertyName("build");
                description["publish"]!.WriteTo(context.Writer);
                await context.WriteEnvironmentVariablesAsync(_web.Resource);
                context.WriteBindings(_web.Resource);
            });
        }
    }

    public bool Owns(JsonObject reference) =>
        RpcPeer.RequiredString(reference, "owner") == RpcPeer.RequiredString(_callbackHandle, "owner") &&
        RpcPeer.RequiredString(reference, "name") == RpcPeer.RequiredString(_callbackHandle, "name");

    public JsonObject Describe() => new()
    {
        ["clrType"] = _web.Resource.GetType().Name,
        ["isExecutable"] = (object)_web.Resource is ExecutableResource,
        ["isJavaScript"] = IsJavaScriptResource(_web.Resource),
        ["relationships"] = new JsonArray(_web.Resource.Annotations.OfType<ResourceRelationshipAnnotation>()
            .Select(annotation => JsonValue.Create($"{annotation.Type}:{annotation.Resource.Name}")).ToArray()),
        ["waits"] = new JsonArray(_web.Resource.Annotations.OfType<WaitAnnotation>()
            .Select(annotation => JsonValue.Create(annotation.Resource.Name)).ToArray()),
        ["consumerWaits"] = new JsonArray(_consumer.Resource.Annotations.OfType<WaitAnnotation>()
            .Select(annotation => JsonValue.Create(annotation.Resource.Name)).ToArray()),
        ["graphCallbackRan"] = _graphCallbackRan,
        ["callbackCalls"] = _callbackCalls
    };

    public async Task<JsonNode?> InvokeAsync(string method, JsonObject args, DistributedApplication? app, CancellationToken cancellationToken)
    {
        if (method == "edgeExport")
        {
            Dictionary<string, object> values;
            if (app is null)
            {
                // A pre-build inspection cannot use the production resolver, whose
                // service provider and run endpoints do not exist yet. Runtime export
                // below uses Hosting's normal gatherer and its evaluate-once cache.
                var context = new EnvironmentCallbackContext(_builder.ExecutionContext, _web.Resource, cancellationToken: cancellationToken);
                foreach (var annotation in _web.Resource.Annotations.OfType<EnvironmentCallbackAnnotation>())
                {
                    await annotation.Callback(context);
                }

                values = context.EnvironmentVariables;
            }
            else
            {
                var configuration = await ExecutionConfigurationBuilder.Create(_web.Resource)
                    .WithEnvironmentVariablesConfig().BuildAsync(_builder.ExecutionContext, cancellationToken: cancellationToken);
                if (configuration.Exception is not null)
                {
                    throw configuration.Exception;
                }

                values = configuration.EnvironmentVariablesWithUnprocessed.ToDictionary(entry => entry.Key, entry => entry.Value.Unprocessed);
            }

            var environment = new JsonObject();
            foreach (var (key, value) in values)
            {
                if (value is not string && value is not (IValueProvider and IManifestExpressionProvider))
                {
                    throw new InvalidOperationException($"Cannot bridge environment value '{value.GetType().Name}' for '{key}'.");
                }

                // Keep the actual CLR value provider alive in its owner. The core holds
                // { reference: { owner, name }, property: "<environment-key>" }, not a
                // resolved credential or a lossy serialization of an arbitrary provider.
                _values[key] = value;
                environment[key] = value is string literal ? JsonValue.Create(literal) : new JsonObject
                {
                    ["reference"] = _callbackHandle.DeepClone(), ["property"] = key
                };
            }

            return environment;
        }

        if (method == "edgeDescribe")
        {
            return Describe();
        }

        if (app is null || _builder.ExecutionContext.IsPublishMode)
        {
            throw new InvalidOperationException("This edge operation requires a running managed application.");
        }

        if (method == "edgeWaitDependencies")
        {
            await app.ResourceNotifications.WaitForDependenciesAsync(_web.Resource, cancellationToken);
            return null;
        }

        if (method == "edgeBeforeStart")
        {
            await _builder.Eventing.PublishAsync(new BeforeResourceStartedEvent(_web.Resource, app.Services), cancellationToken);
            return null;
        }

        if (method == "edgeConflict")
        {
            var conflicting = _builder.CreateResourceBuilder(new NativeFacade("conflict"))
                .WithReference(_cache, connectionName: "my-db")
                .WithReference(_cache, connectionName: "my_db");
            await ExecutionConfigurationBuilder.Create(conflicting.Resource).WithEnvironmentVariablesConfig()
                .BuildAsync(_builder.ExecutionContext, cancellationToken: cancellationToken);
            throw new InvalidOperationException("Expected Hosting to reject physical connection-name collisions.");
        }

        if (method == "edgeNetwork")
        {
            var network = RpcPeer.RequiredString(args, "network") switch
            {
                "localhost" => KnownNetworkIdentifiers.LocalhostNetwork,
                "container" => KnownNetworkIdentifiers.DefaultAspireContainerNetwork,
                _ => throw new ArgumentException("Unsupported network.")
            };
            var uri = new Uri((await _cache.Resource.UriExpression.GetValueAsync(new ValueProviderContext
            {
                Caller = _web.Resource, ExecutionContext = _builder.ExecutionContext, Network = network
            }, cancellationToken))!);
            return new JsonObject { ["host"] = uri.Host, ["port"] = uri.Port };
        }

        if (method == "edgeState")
        {
            // RPC requests run concurrently. Serialize allocation, lifecycle events,
            // and status publication so an older update cannot overtake a newer one.
            await _stateGate.WaitAsync(cancellationToken);
            try
            {
                var generation = args["generation"]!.GetValue<int>();
                if (_invalidated || generation <= _generation ||
                    RpcPeer.RequiredString(RpcPeer.RequiredObject(args, "resource"), "owner") != RpcPeer.RequiredString(_native, "owner") ||
                    RpcPeer.RequiredString(RpcPeer.RequiredObject(args, "resource"), "name") != _web.Resource.Name)
                {
                    throw new InvalidOperationException("Stale generation or foreign native resource.");
                }

                var state = RpcPeer.RequiredString(args, "state");
                if (state is not ("Running" or "Exited" or "FailedToStart"))
                {
                    throw new ArgumentException("Unsupported owner state.");
                }

                if (state == KnownResourceStates.Running)
                {
                    var port = args["port"]!.GetValue<int>();
                    var endpoint = _web.Resource.Annotations.OfType<EndpointAnnotation>().Single();
                    endpoint.AllocatedEndpoint = new AllocatedEndpoint(endpoint, "127.0.0.1", port, EndpointBindingMode.SingleAddress,
                        targetPortExpression: null, networkId: null);
                    if (!_endpointsAllocated)
                    {
                        await _builder.Eventing.PublishAsync(new ResourceEndpointsAllocatedEvent(_web.Resource, app.Services), cancellationToken);
                        _endpointsAllocated = true;
                    }
                }

                _generation = generation;
                await app.ResourceNotifications.PublishUpdateAsync(_web.Resource, snapshot => snapshot with
                {
                    ResourceType = "native-executable",
                    State = state
                });
            }
            finally
            {
                _stateGate.Release();
            }

            return null;
        }

        if (method == "edgeWaitHealthy")
        {
            await app.ResourceNotifications.WaitForResourceHealthyAsync(_web.Resource.Name, cancellationToken);
            return null;
        }

        if (method == "edgeWaitConsumer")
        {
            await app.ResourceNotifications.WaitForResourceAsync(_consumer.Resource.Name, KnownResourceStates.Finished, cancellationToken);
            return null;
        }

        if (method == "edgeCommand")
        {
            var result = await app.Services.GetRequiredService<ResourceCommandService>()
                .ExecuteCommandAsync(_web.Resource, "probe-owner", cancellationToken);
            return new JsonObject { ["success"] = result.Success, ["message"] = result.Message };
        }

        if (method == "edgeEndpoints")
        {
            return new JsonObject
            {
                ["held"] = await _heldEndpoint.GetValueAsync(cancellationToken),
                ["fresh"] = await _web.GetEndpoint("http").GetValueAsync(cancellationToken),
                ["state"] = app.ResourceNotifications.TryGetCurrentState(_web.Resource.Name, out var current) ? current.Snapshot.State?.Text : null
            };
        }

        if (method == "edgeInvalidate")
        {
            await _stateGate.WaitAsync(cancellationToken);
            try
            {
                _invalidated = true;
                await app.ResourceNotifications.PublishUpdateAsync(_web.Resource, snapshot => snapshot with { State = KnownResourceStates.FailedToStart });
            }
            finally
            {
                _stateGate.Release();
            }

            return null;
        }

        throw new InvalidOperationException($"Unknown edge operation '{method}'.");
    }

    public async Task<JsonNode?> ResolveAsync(JsonObject args, CancellationToken cancellationToken)
    {
        if (_invalidated)
        {
            throw new InvalidOperationException("Native owner has been invalidated.");
        }

        // Fail even if a CLR provider still has a cached endpoint when the native
        // owner is gone. Built-in EndpointReference consumers do not have this guard.
        await _peer.RequestAsync("describeNative", new JsonObject { ["resource"] = _native.DeepClone() }, cancellationToken);
        if (_invalidated)
        {
            throw new InvalidOperationException("Native owner was invalidated during resolution.");
        }

        var key = RpcPeer.RequiredString(args, "property");
        if (!_values.TryGetValue(key, out var value))
        {
            throw new InvalidOperationException($"Environment provider '{key}' was not exported.");
        }

        var mode = RpcPeer.RequiredString(args, "mode");
        if (mode == "publish" && value is IManifestExpressionProvider expression)
        {
            return JsonValue.Create(expression.ValueExpression);
        }

        if (mode == "run" && value is IValueProvider provider)
        {
            return JsonValue.Create(await provider.GetValueAsync(new ValueProviderContext
            {
                ExecutionContext = _builder.ExecutionContext,
                Caller = _web.Resource
            }, cancellationToken));
        }

        throw new InvalidOperationException($"Unsupported environment value '{value.GetType().Name}' in '{mode}' mode.");
    }

    // Keep the JS assembly out of the original split path until this explicit probe.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static bool IsJavaScriptResource(IResource resource) => resource is JavaScriptAppResource;

    private sealed class NativeFacade(string name) : Resource(name),
        IResourceWithEnvironment, IResourceWithServiceDiscovery, IResourceWithWaitSupport;
}
