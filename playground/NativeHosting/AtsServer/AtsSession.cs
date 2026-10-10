// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace NativeHosting;

internal sealed class AtsFault(string code, string message) : InvalidOperationException(message)
{
    public string Code { get; } = code;
}

internal sealed class AtsRegistry : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, (object Value, string Type)> _handles = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<object, JsonObject> _references = new(ReferenceEqualityComparer.Instance);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _tokens = new(StringComparer.Ordinal);
    private readonly object _tokenGate = new();
    private readonly ConcurrentDictionary<string, RpcPeer> _external = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<RpcPeer, AtsSession> _hosts = new();
    private readonly object _hostGate = new();
    private readonly ConcurrentDictionary<NativeResource, AtsSession> _controllers = new();
    private NativeBuilder? _builder;
    private readonly SemaphoreSlim _graphGate = new(1);
    private readonly SemaphoreSlim _dispatchGate = new(1);
    private readonly List<Task> _active = [];
    private CancellationTokenSource _generation = new();
    private bool _resetting;
    public bool Idle => _builder is null && !_resetting;
    public int HandleCount => _handles.Count;

    public void ClaimController(NativeResource resource, AtsSession session)
    {
        if (!_controllers.TryAdd(resource, session))
        {
            throw new InvalidOperationException("A controller already owns this custom resource.");
        }
    }

    public void EnsureController(NativeResource resource, AtsSession session)
    {
        if (!_controllers.TryGetValue(resource, out var controller) || !ReferenceEquals(controller, session))
        {
            throw new UnauthorizedAccessException("Only the registered controller can report custom resource state.");
        }
    }

    public JsonObject Marshal(object value, string type)
    {
        return (JsonObject)_references.GetOrAdd(value, instance =>
        {
            var id = RandomNumberGenerator.GetHexString(32, lowercase: true);
            if (instance is NativeBuilder builder)
            {
                if (Interlocked.CompareExchange(ref _builder, builder, null) is not null)
                {
                    throw new InvalidOperationException("Only one guest graph can be active.");
                }
            }

            _handles[id] = (instance, type);
            return new JsonObject { ["$handle"] = id, ["$type"] = type };
        }).DeepClone();
    }

    public T Get<T>(JsonNode? node) where T : class
    {
        var reference = node as JsonObject ?? throw new ArgumentException("Expected an ATS handle.");
        var id = RpcPeer.RequiredString(reference, "$handle");
        if (!_handles.TryGetValue(id, out var entry))
        {
            throw new AtsFault("HANDLE_NOT_FOUND", "ATS handle is stale or unknown.");
        }

        if (RpcPeer.RequiredString(reference, "$type") != entry.Type || entry.Value is not T typed)
        {
            throw new AtsFault("TYPE_MISMATCH", "ATS handle type does not satisfy this capability.");
        }

        return typed;
    }

    public CancellationToken Token(JsonNode? node, CancellationToken fallback)
    {
        if (node is null)
        {
            return fallback;
        }

        var id = node.GetValue<string>();
        lock (_tokenGate)
        {
            return _tokens.GetOrAdd(id, _ => CancellationTokenSource.CreateLinkedTokenSource(fallback)).Token;
        }
    }

    public bool CancelToken(string id)
    {
        lock (_tokenGate)
        {
            return _tokens.TryGetValue(id, out var source) && Cancel(source);
        }
    }

    private static bool Cancel(CancellationTokenSource source)
    {
        source.Cancel();
        return true;
    }

    public async Task RegisterHostAsync(AtsSession session, CancellationToken token)
    {
        var peer = session.Peer;
        var capabilities = await peer.RequestAsync("getCapabilities", new JsonArray(), token);
        if (capabilities?["protocolVersion"]?.GetValue<int>() != 2 || capabilities["capabilities"] is not JsonArray exports)
        {
            throw new ArgumentException("Expected external ATS integration protocol version 2.");
        }

        var ids = exports.Select(export => RpcPeer.RequiredString(export!.AsObject(), "id")).ToArray();
        if (ids.Distinct(StringComparer.Ordinal).Count() != ids.Length || ids.Any(id => !ExternalDispatch.Contains(id)))
        {
            throw new ArgumentException("External ATS capabilities must be known and unique.");
        }

        // Validate the entire registration before publishing it. A rejected host
        // must not leave capabilities routed to a disconnected, partial owner.
        lock (_hostGate)
        {
            if (_hosts.ContainsKey(peer) || ids.Any(_external.ContainsKey))
            {
                throw new ArgumentException("External ATS integration host or capability is already registered.");
            }

            _hosts[peer] = session;
            foreach (var id in ids)
            {
                _external[id] = peer;
            }
        }
    }

    public void UnregisterHost(RpcPeer peer)
    {
        lock (_hostGate)
        {
            _hosts.TryRemove(peer, out _);
            foreach (var entry in _external.Where(entry => ReferenceEquals(entry.Value, peer)))
            {
                _external.TryRemove(entry.Key, out _);
            }
        }
    }
    public async Task<JsonNode?> InvokeAsync(AtsSession session, string capability, JsonObject args, CancellationToken token)
    {
        Task<JsonNode?> operation;
        await _dispatchGate.WaitAsync(token);
        try
        {
            if (_resetting)
            {
                throw new InvalidOperationException("The previous ATS graph is still being disposed.");
            }

            _active.RemoveAll(task => task.IsCompleted);
            operation = InvokeCoreAsync(session, capability, args, token, _generation.Token);
            _active.Add(operation);
        }
        finally
        {
            _dispatchGate.Release();
        }

        return await operation;
    }

    private async Task<JsonNode?> InvokeCoreAsync(AtsSession session, string capability, JsonObject args, CancellationToken token, CancellationToken generation)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, generation, Token(args["cancellationToken"], token));
        if (capability == "NativeHosting.Ats/createNativeBuilder" && _builder is not null)
        {
            // Check under the dispatch gate before constructing an owner. A
            // rejected guest must not allocate an undisposed DCP workspace.
            throw new InvalidOperationException("Only one guest graph can be active.");
        }

        if (ExternalDispatch.Contains(capability))
        {
            if (!_external.TryGetValue(capability, out var host))
            {
                throw new InvalidOperationException("The ATS integration host is not registered.");
            }

            return await host.RequestAsync("handleExternalCapability",
                new JsonArray(JsonValue.Create(capability), args.DeepClone(), JsonValue.Create(Guid.NewGuid().ToString("N"))), linked.Token);
        }

        return await NativeDispatch.InvokeAsync(session, capability, args, linked.Token);
    }

    public async Task ResetAsync()
    {
        await _graphGate.WaitAsync();
        try
        {
            if (_builder is { } current && _references.TryGetValue(current, out var reference) &&
                _external.TryGetValue("NativeHosting.Ats/releaseGraph", out var integration) &&
                _hosts.TryGetValue(integration, out var integrationSession))
            {
                // Controllers must stop while their graph handles are still valid.
                // After this bounded disposal hook, revoke every old handle before
                // canceling requests and deleting the native workloads.
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                try
                {
                    await integration.RequestAsync("handleExternalCapability",
                        new JsonArray(JsonValue.Create("NativeHosting.Ats/releaseGraph"),
                            new JsonObject
                            {
                                ["builder"] = reference.DeepClone(),
                                ["callbackIds"] = new JsonArray(integrationSession.CallbackIds.Select(id => (JsonNode)JsonValue.Create(id)!).ToArray())
                            }, JsonValue.Create(Guid.NewGuid().ToString("N"))),
                        cleanup.Token);
                    integrationSession.ClearCallbacks();
                }
                catch (Exception error)
                {
                    Console.Error.WriteLine($"Integration graph disposal failed: {error.Message}");
                }
            }

            await _dispatchGate.WaitAsync();
            Task[] operations;
            try
            {
                _resetting = true;
                _generation.Cancel();
                // Revoke capabilities before waiting for inflight callbacks.
                _handles.Clear();
                _references.Clear();
                _controllers.Clear();
                operations = _active.ToArray();
            }
            finally
            {
                _dispatchGate.Release();
            }

            foreach (var operation in operations)
            {
                try
                {
                    await operation;
                }
                catch (Exception error)
                {
                    Console.Error.WriteLine($"Graph reset observed {error.GetType().Name} from an inflight ATS operation.");
                }
            }

            if (_builder is { } builder)
            {
                await builder.DisposeAsync();
                _builder = null;
            }

            lock (_tokenGate)
            {
                foreach (var token in _tokens.Values)
                {
                    token.Cancel();
                    token.Dispose();
                }

                _tokens.Clear();
            }
            _generation.Dispose();
            _generation = new();
            await _dispatchGate.WaitAsync();
            try
            {
                _active.Clear();
                _resetting = false;
            }
            finally
            {
                _dispatchGate.Release();
            }
        }
        finally
        {
            _graphGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await ResetAsync();
        _generation.Dispose();
        _dispatchGate.Dispose();
        _graphGate.Dispose();
    }

}

internal sealed class AtsSession(AtsRegistry registry, string authToken)
{
    private readonly ConcurrentDictionary<string, bool> _callbacks = new(StringComparer.Ordinal);
    private bool _authenticated;
    public bool IntegrationHost { get; private set; }
    public bool Guest { get; private set; }
    public RpcPeer Peer { get; set; } = null!;
    public string[] CallbackIds => _callbacks.Keys.ToArray();
    public void ClearCallbacks() => _callbacks.Clear();
    public void TrackCallback(string id) => _callbacks.TryAdd(id, true);
    public void ClaimController(NativeResource resource) => registry.ClaimController(resource, this);
    public void EnsureController(NativeResource resource) => registry.EnsureController(resource, this);

    public JsonObject Marshal(object value, string type) => registry.Marshal(value, type);
    public T Get<T>(JsonNode? node) where T : class => registry.Get<T>(node);
    public object Union(JsonNode? node) => node is JsonObject ? Get<NativeValue>(node) : String(node, "value");
    public CancellationToken Token(JsonNode? node, CancellationToken fallback) => registry.Token(node, fallback);
    public static string String(JsonNode? node, string name) => node?.GetValue<string>()
        ?? throw new ArgumentException($"Missing argument '{name}'.");

    public async Task<bool> CallbackAsync(string id, JsonObject args, CancellationToken token)
    {
        // The production ATS callback shape is positional {p0,p1,...}. Include
        // the server token ID so nested generated calls retain cancellation.
        var tokenId = Guid.NewGuid().ToString("N");
        registry.Token(JsonValue.Create(tokenId), token);
        args[$"p{args.Count}"] = tokenId;
        var result = await Peer.RequestAsync("invokeCallback",
            new JsonArray(JsonValue.Create(id), args), token);
        return result!.GetValue<bool>();
    }

    public async Task<JsonNode?> InvokeAsync(string method, JsonNode? parameters, CancellationToken token)
    {
        var args = parameters is null ? new JsonArray() :
            parameters as JsonArray ?? throw new ArgumentException("ATS methods require positional RPC arguments.");
        if (method == "authenticate")
        {
            var provided = String(args[0], "token");
            _authenticated = CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(provided), System.Text.Encoding.UTF8.GetBytes(authToken));
            return JsonValue.Create(_authenticated);
        }

        if (method == "ping")
        {
            return JsonValue.Create("pong");
        }

        if (!_authenticated)
        {
            throw new UnauthorizedAccessException("Authenticate before invoking ATS capabilities.");
        }

        if (method == "getRuntimeState")
        {
            return new JsonObject { ["idle"] = registry.Idle, ["handles"] = registry.HandleCount };
        }

        if (method == "registerAsIntegrationHost")
        {
            await registry.RegisterHostAsync(this, token);
            IntegrationHost = true;
            return JsonValue.Create(true);
        }

        if (method == "cancelToken")
        {
            return JsonValue.Create(registry.CancelToken(String(args[0], "tokenId")));
        }

        if (method != "invokeCapability")
        {
            throw new ArgumentException($"Unsupported native AppHost server method '{method}'.");
        }

        var capability = String(args[0], "capabilityId");
        if (capability == "NativeHosting.Ats/createNativeBuilder")
        {
            if (IntegrationHost)
            {
                throw new InvalidOperationException("Integration hosts cannot own the guest graph.");
            }

        }

        try
        {
            var result = await registry.InvokeAsync(this, capability, args[1] as JsonObject ?? new JsonObject(), token);
            if (capability == "NativeHosting.Ats/createNativeBuilder")
            {
                Guest = true;
            }

            return result;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // ATS clients recognize the structured $error envelope independently
            // of the RPC library's transport error codes.
            Console.Error.WriteLine($"ATS {capability} failed: {error.GetType().Name}");
            var code = error switch
            {
                AtsFault fault => fault.Code,
                UnauthorizedAccessException => "FORBIDDEN",
                ArgumentException or System.Text.Json.JsonException => "INVALID_ARGUMENT",
                _ => "INTERNAL_ERROR"
            };
            return new JsonObject
            {
                ["$error"] = new JsonObject { ["code"] = code, ["message"] = error.Message, ["capability"] = capability }
            };
        }
    }
}
