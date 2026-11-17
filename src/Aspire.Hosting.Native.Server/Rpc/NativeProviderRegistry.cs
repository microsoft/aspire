// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Aspire.Hosting.Native.Api;
using Aspire.Hosting.Native.Server;
using Microsoft.Extensions.Logging;
using Aspire.Hosting.Native.Runtime;

namespace Aspire.Hosting.Native.Rpc;

/// <summary>Owns explicit provider activation separately from message framing and static AOT dispatch.</summary>
internal interface INativeProviderRegistry : IProviderLifecycleDispatcher
{
    string Expect(NativeRpcConnection owner, string packageName);
    Task RegisterAsync(NativeRpcConnection connection, string authority, INativeRpcPeer peer);
    Task WaitRegistrationAsync(NativeRpcConnection owner, string authority);
    bool AbandonRegistration(NativeRpcConnection owner, string authority);
    bool IsProvider(NativeRpcConnection connection);
    JsonObject InspectCapabilities();
    Task<JsonNode?> InvokeAsync(NativeRpcConnection caller, string capability, JsonObject arguments);
    Task<JsonNode?> InvokeCoreAsync(NativeRpcConnection provider, string capability, JsonObject arguments);
    Task<JsonNode?> InvokeCallbackAsync(NativeRpcConnection provider, string callback, JsonObject arguments);
    void Disconnect(NativeRpcConnection connection);
}

internal sealed partial class NativeProviderRegistry(JsonObject coreContract, NativeServerOptions options, ILogger logger) : INativeProviderRegistry
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Expectation> _expected = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Expectation> _activationWaits = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Registration> _capabilities = new(StringComparer.Ordinal);
    private readonly Dictionary<NativeRpcConnection, Registration> _providers = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Guid, Registration> _providersById = new();
    private readonly HashSet<string> _providerTypes = new(StringComparer.Ordinal);
    private readonly NativeProviderContractProjection _contract = new(NativeProviderMetadata.ReadCoreContract(coreContract));

    public string Expect(NativeRpcConnection owner, string packageName)
    {
        NativeProviderContractProjection.ValidatePackageName(packageName);
        lock (_gate)
        {
            foreach (var (authority, expectation) in _activationWaits.ToArray())
            {
                if (options.TimeProvider.GetElapsedTime(expectation.Created) >= options.ProviderRegistrationTimeout)
                {
                    _expected.Remove(authority);
                    _activationWaits.Remove(authority);
                    expectation.Completion.TrySetCanceled();
                }
            }
            if (_activationWaits.Count >= 64 || _providers.Count >= 32)
            {
                throw RpcFault.ProviderLimit("Provider registration capacity was reached.");
            }
            var token = RandomNumberGenerator.GetHexString(64);
            var pending = new Expectation(owner, packageName, options.TimeProvider.GetTimestamp());
            _expected.Add(token, pending);
            _activationWaits.Add(token, pending);

            return token;
        }
    }

    public async Task RegisterAsync(NativeRpcConnection connection, string authority, INativeRpcPeer peer)
    {
        Expectation expected;
        lock (_gate)
        {
            // An expected authority is one-use even when discovery subsequently
            // fails. The registration process never guesses a provider by package.
            if (!_expected.Remove(authority, out expected!) || expected.Owner.IsClosed ||
                ReferenceEquals(expected.Owner, connection) || _providers.ContainsKey(connection) ||
                options.TimeProvider.GetElapsedTime(expected.Created) >= options.ProviderRegistrationTimeout)
            {
                throw RpcFault.ProviderRegistrationRejected("The expected provider registration is unavailable.");
            }
        }
        try
        {
            using var deadline = new CancellationTokenSource(options.ProviderRegistrationTimeout, options.TimeProvider);
            using var discovery = CancellationTokenSource.CreateLinkedTokenSource(connection.Closed, deadline.Token);
            var metadata = NativeProviderMetadata.Read(await peer.RequestAsync("getCapabilities", [], discovery.Token).ConfigureAwait(false));
            var manifest = _contract.Project(expected.PackageName, metadata);
            var projected = manifest.Capabilities;
            var registration = new Registration(expected.Owner, connection, peer, projected, manifest.OwnedTypes);
            lock (_gate)
            {
                if (!_activationWaits.TryGetValue(authority, out var pending) || !ReferenceEquals(pending, expected))
                {
                    throw RpcFault.ProviderRegistrationRejected("The expected activation authority is unavailable.");
                }
                if (expected.Owner.IsClosed || connection.IsClosed || peer.Closed.IsCancellationRequested ||
                    _capabilities.Count + projected.Count > 4096 ||
                    projected.Keys.Any(id => _contract.CoreCapabilities.Contains(id) || _capabilities.ContainsKey(id)) ||
                    manifest.OwnedTypes.Overlaps(_providerTypes) ||
                    _providers.ContainsKey(connection))
                {
                    throw RpcFault.ProviderConflict("The provider registration conflicts with an existing capability.");
                }
                foreach (var id in projected.Keys)
                {
                    _capabilities.Add(id, registration);
                }
                _providers.Add(connection, registration);
                _providersById.Add(registration.Id, registration);
                _providerTypes.UnionWith(manifest.OwnedTypes);
            }
            logger.LogInformation("Native provider activated with {CapabilityCount} capabilities.", projected.Count);
            expected.Completion.TrySetResult();
        }
        catch
        {
            expected.Completion.TrySetException(RpcFault.ProviderRegistrationRejected("The provider activation failed."));
            _ = expected.Completion.Task.Exception;
            throw;
        }
    }

    public async Task WaitRegistrationAsync(NativeRpcConnection owner, string authority)
    {
        Expectation expected;
        lock (_gate)
        {
            if (!_activationWaits.TryGetValue(authority, out expected!) || !ReferenceEquals(owner, expected.Owner))
            {
                throw RpcFault.ProviderRegistrationRejected("The expected activation authority is unavailable.");
            }
        }
        try
        {
            await expected.Completion.Task.WaitAsync(options.ProviderRegistrationTimeout, options.TimeProvider, owner.Closed).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw RpcFault.ProviderTimeout("The provider activation deadline was reached.");
        }
        finally
        {
            lock (_gate)
            {
                _activationWaits.Remove(authority);
                _expected.Remove(authority);
            }
        }
    }

    public bool AbandonRegistration(NativeRpcConnection owner, string authority)
    {
        lock (_gate)
        {
            if (!_activationWaits.TryGetValue(authority, out var expected))
            {
                return false;
            }
            if (!ReferenceEquals(owner, expected.Owner))
            {
                throw RpcFault.ProviderRegistrationRejected("The activation authority belongs to another connection.");
            }
            _expected.Remove(authority);
            _activationWaits.Remove(authority);
            expected.Completion.TrySetException(RpcFault.ProviderRegistrationRejected("The provider activation was abandoned."));
            _ = expected.Completion.Task.Exception;

            return true;
        }
    }

    public bool IsProvider(NativeRpcConnection connection)
    {
        lock (_gate)
        {
            return _providers.ContainsKey(connection);
        }
    }

    public JsonObject InspectCapabilities()
    {
        lock (_gate)
        {
            return new JsonObject
            {
                ["protocolVersion"] = 2,
                ["capabilities"] = new JsonArray(_capabilities.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .Select(pair => (JsonNode?)pair.Value.Capabilities[pair.Key].Metadata.DeepClone()).ToArray())
            };
        }
    }

    public async Task<JsonNode?> InvokeAsync(NativeRpcConnection caller, string capability, JsonObject arguments)
    {
        // Connection dispatch starts under its handle gate. Yield before taking
        // another connection's gate to avoid cross-connection lock inversion.
        await Task.Yield();
        Registration registration;
        lock (_gate)
        {
            if (!_capabilities.TryGetValue(capability, out registration!))
            {
                throw RpcFault.UnknownCapability("The capability is not implemented.");
            }
        }
        EnsureAvailable(registration);
        var metadata = registration.Capabilities[capability];
        if (metadata.LifecyclePhase is not null)
        {
            throw RpcFault.LifecycleAuthorityRequired("Lifecycle capabilities require native phase authority.");
        }
        var parameters = metadata.Parameters;
        RpcArguments.Validate(arguments, parameters.Select(parameter => parameter.Name).ToArray());
        var mapped = new JsonObject();
        var callbacks = new List<ProviderCallbackLease>();
        PublishedResource? callbackReceiver = null;
        try
        {
            foreach (var parameter in parameters)
            {
                var name = parameter.Name;
                if (parameter.Callback is { } signature)
                {
                    var callbackId = RpcArguments.String(arguments[name]);
                    var lease = registration.Connection.AddCallback(new ProviderCallback(caller, callbackId, signature));
                    callbacks.Add(lease);
                    mapped[name] = lease.Id;
                }
                else
                {
                    mapped[name] = ToProvider(registration, caller, arguments[name], parameter.Type, out var resource);
                    if (metadata.TargetParameterName == name)
                    {
                        callbackReceiver = resource;
                    }
                }
            }
            var result = await registration.Peer.RequestAsync("handleExternalCapability",
                new JsonArray(JsonValue.Create(capability), mapped, JsonValue.Create(Guid.NewGuid().ToString())), caller.Closed).ConfigureAwait(false);
            EnsureAvailable(registration);

            var translated = ToCaller(registration, caller, result, metadata.ReturnType, out var callbackOwner);
            // A returned provider object owns deferred callbacks. Mutators may
            // instead attach them to their existing provider-owned receiver.
            callbackOwner ??= callbackReceiver;
            if (callbacks.Count > 0 && callbackOwner is not null)
            {
                lock (_gate)
                {
                    if (ReferenceEquals(callbackOwner.Owner, caller) && !callbackOwner.IsRetired && !callbackOwner.Declaration.IsRevoked)
                    {
                        foreach (var callback in callbacks)
                        {
                            callback.Callback.Resource = callbackOwner;
                            callbackOwner.Callbacks.Add(callback);
                        }
                        callbacks.Clear();
                    }
                }
            }

            return translated;
        }
        catch (RpcFault fault) when (fault.FaultCode == RpcFault.ProviderTimeoutCode)
        {
            Disconnect(registration.Connection);
            throw;
        }
        catch (OperationCanceledException)
        {
            Disconnect(registration.Connection);
            throw RpcFault.ProviderDisconnected("The provider invocation did not complete; it will not be replayed.");
        }
        finally
        {
            foreach (var callback in callbacks)
            {
                callback.Dispose();
            }
        }
    }

    public async Task<JsonNode?> InvokeCoreAsync(NativeRpcConnection provider, string capability, JsonObject arguments)
    {
        await Task.Yield();
        Registration registration;
        lock (_gate)
        {
            if (!_providers.TryGetValue(provider, out registration!))
            {
                throw RpcFault.ProviderDisconnected("The provider authority is no longer available.");
            }
        }
        EnsureAvailable(registration);
        NativeRpcConnection? origin = null;
        JsonNode? Translate(JsonNode? value)
        {
            if (value is JsonObject handle && handle.ContainsKey("$handle"))
            {
                var reference = provider.ReadReference(handle);
                if (reference is ResourceLifecycleScope)
                {
                    if (origin is not null && !ReferenceEquals(origin, provider))
                    {
                        throw RpcFault.InvalidHandle("A lifecycle scope cannot combine general declaration authority.");
                    }
                    origin = provider;

                    return handle.DeepClone();
                }
                if (reference is not DelegatedCoreReference delegated)
                {
                    throw RpcFault.TypeMismatch("Core calls require delegated core authority.");
                }
                if (origin is not null && !ReferenceEquals(origin, delegated.Owner))
                {
                    throw RpcFault.InvalidHandle("A core call cannot combine different connection authorities.");
                }
                origin = delegated.Owner;
                delegated.Owner.ReadReference(delegated.Handle);

                return delegated.Handle.DeepClone();
            }
            return value switch
            {
                JsonObject obj => new JsonObject(obj.Select(pair => KeyValuePair.Create(pair.Key, Translate(pair.Value)))),
                JsonArray array => new JsonArray(array.Select(Translate).ToArray()),
                _ => value?.DeepClone()
            };
        }
        var translated = (JsonObject)Translate(arguments)!;
        if (origin is null)
        {
            throw RpcFault.InvalidHandle("Provider core calls require delegated connection authority.");
        }
        var result = await origin.InvokeCoreAsync(capability, translated, provider).ConfigureAwait(false);
        return DelegateResult(result);

        JsonNode? DelegateResult(JsonNode? value)
        {
            if (value is JsonObject handle && handle.ContainsKey("$handle"))
            {
                origin.ReadReference(handle);

                return provider.AddReference(new DelegatedCoreReference(origin, handle.DeepClone().AsObject()),
                    RpcArguments.String(handle["$type"]));
            }
            return value switch
            {
                JsonObject obj => new JsonObject(obj.Select(pair => KeyValuePair.Create(pair.Key, DelegateResult(pair.Value)))),
                JsonArray array => new JsonArray(array.Select(DelegateResult).ToArray()),
                _ => value?.DeepClone()
            };
        }
    }

    public async Task<JsonNode?> InvokeCallbackAsync(NativeRpcConnection provider, string callbackId, JsonObject arguments)
    {
        await Task.Yield();
        Registration registration;
        lock (_gate)
        {
            if (!_providers.TryGetValue(provider, out registration!))
            {
                throw RpcFault.InvalidHandle("Callbacks require a registered provider.");
            }
        }
        EnsureAvailable(registration);
        var callback = provider.ReadCallback(callbackId);
        var parameters = callback.Signature.Parameters;
        RpcArguments.Validate(arguments, Enumerable.Range(0, parameters.Length).Select(index => $"p{index}").ToArray());
        var mapped = new JsonObject();
        for (var index = 0; index < parameters.Length; index++)
        {
            mapped[$"p{index}"] = ToCaller(registration, callback.Owner, arguments[$"p{index}"],
                parameters[index], out _);
        }
        var result = await callback.Owner.RequestAsync("invokeCallback",
            new JsonArray(JsonValue.Create(callback.Id), mapped)).ConfigureAwait(false);
        return ToProvider(registration, callback.Owner, result, callback.Signature.ReturnType, out _);
    }

    private JsonNode? ToProvider(Registration registration, NativeRpcConnection caller, JsonNode? value, NativeProviderType type,
        out PublishedResource? resource)
    {
        resource = null;
        if (type.Kind != NativeProviderValueKind.Handle)
        {
            NativeProviderValueProtocol.ValidatePrimitive(value, type);
            return value?.DeepClone();
        }
        var handle = value as JsonObject ?? throw RpcFault.InvalidHandle("A typed handle is required.");
        var expectedType = type.TypeId;
        var reference = caller.ReadReference(handle, expectedType);
        if (reference is ProviderResourceReference external)
        {
            if (!ReferenceEquals(external.Provider, registration) || !ReferenceEquals(external.Owner, caller))
            {
                throw RpcFault.InvalidHandle("The provider resource belongs to another authority.");
            }
            resource = external.Resource;
            return external.Handle.DeepClone();
        }
        if (!_contract.CoreHandles.Contains(expectedType))
        {
            throw RpcFault.TypeMismatch("The value is not an integration-owned resource.");
        }
        return registration.Connection.AddReference(new DelegatedCoreReference(caller, handle.DeepClone().AsObject()), expectedType);
    }

    private JsonNode? ToCaller(Registration registration, NativeRpcConnection caller, JsonNode? value, NativeProviderType type,
        out PublishedResource? resource)
    {
        resource = null;
        if (type.Kind != NativeProviderValueKind.Handle)
        {
            NativeProviderValueProtocol.ValidatePrimitive(value, type);
            return value?.DeepClone();
        }
        var expectedType = type.TypeId;
        var handle = NativeProviderValueProtocol.ParseReturnedHandle(value, type, _contract.CoreHandles);
        if (_contract.CoreHandles.Contains(expectedType))
        {
            if (registration.Connection.ReadReference(handle.Wire, expectedType) is not DelegatedCoreReference delegated ||
                !ReferenceEquals(delegated.Owner, caller))
            {
                throw RpcFault.InvalidHandle("The returned core handle is not delegated by this caller.");
            }
            caller.ReadReference(delegated.Handle, expectedType);
            return delegated.Handle.DeepClone();
        }
        if (handle.Lifetime is not { } lifetime ||
            registration.Connection.ReadReference(lifetime) is not DelegatedCoreReference backing ||
            !ReferenceEquals(backing.Owner, caller))
        {
            throw RpcFault.InvalidHandle("Integration resource lifetime must be delegated by its caller.");
        }
        if (caller.ReadReference(backing.Handle) is not DeclaredResource declaration)
        {
            throw RpcFault.InvalidHandle("Integration resource lifetime must be a native resource declaration.");
        }
        resource = TrackProviderObject(registration, caller, handle.Id, declaration, expectedType);
        return caller.AddReference(new ProviderResourceReference(registration, caller, handle.Wire.DeepClone().AsObject(),
            backing.Handle.DeepClone().AsObject(), resource), expectedType);
    }

    private static void EnsureAvailable(Registration registration)
    {
        if (registration.Disconnected || registration.Owner.IsClosed || registration.Connection.IsClosed || registration.Peer.Closed.IsCancellationRequested)
        {
            throw RpcFault.ProviderDisconnected("The provider is no longer available.");
        }
    }

    public void Disconnect(NativeRpcConnection connection)
    {
        var peers = new List<INativeRpcPeer>();
        lock (_gate)
        {
            foreach (var (id, expected) in _activationWaits.ToArray())
            {
                if (ReferenceEquals(expected.Owner, connection))
                {
                    _expected.Remove(id);
                    _activationWaits.Remove(id);
                    expected.Completion.TrySetCanceled();
                }
            }
            foreach (var (provider, registration) in _providers.ToArray())
            {
                if (registration.Matches(connection))
                {
                    registration.Disconnected = true;
                    peers.Add(registration.Peer);
                    _providers.Remove(provider);
                    _providersById.Remove(registration.Id);
                    foreach (var capability in registration.Capabilities.Keys)
                    {
                        _capabilities.Remove(capability);
                    }
                    _providerTypes.ExceptWith(registration.OwnedTypes);
                    registration.Release();
                }
            }
        }
        // Closing a peer invokes its connection-disposal callback. Do not hold
        // the registry gate across callbacks that acquire another connection gate.
        foreach (var peer in peers)
        {
            peer.Dispose();
        }
    }

    internal sealed class Registration(NativeRpcConnection owner, NativeRpcConnection connection, INativeRpcPeer peer,
        Dictionary<string, NativeProviderCapability> capabilities, HashSet<string> ownedTypes)
    {
        private NativeRpcConnection? _owner = owner;
        private NativeRpcConnection? _connection = connection;
        private INativeRpcPeer? _peer = peer;
        public NativeRpcConnection Owner => _owner ?? throw Unavailable();
        public NativeRpcConnection Connection => _connection ?? throw Unavailable();
        public INativeRpcPeer Peer => _peer ?? throw Unavailable();
        public Dictionary<string, NativeProviderCapability> Capabilities { get; } = capabilities;
        public IReadOnlySet<string> OwnedTypes { get; } = ownedTypes;
        public Dictionary<string, NativeProviderCapability[]> LifecycleCapabilities { get; } = capabilities.Values
            .Where(capability => capability.LifecyclePhase is not null)
            .GroupBy(capability => capability.TargetTypeId!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        public Guid Id { get; } = Guid.NewGuid();
        public Dictionary<string, PublishedResource> Resources { get; } = new(StringComparer.Ordinal);
        public bool Disconnected { get; set; }
        public bool Matches(NativeRpcConnection candidate) =>
            ReferenceEquals(_owner, candidate) || ReferenceEquals(_connection, candidate);
        public void Release()
        {
            // Existing handles retain this disconnected tombstone, not registry
            // routes. A replacement process must never revive their authority.
            _owner = null;
            _connection = null;
            _peer = null;
            foreach (var resource in Resources.Values)
            {
                resource.IsRetired = true;
                resource.Callbacks.Clear();
            }
            Resources.Clear();
        }
        private static RpcFault Unavailable() => RpcFault.ProviderDisconnected("The integration provider is unavailable.");
    }

    private sealed record Expectation(NativeRpcConnection Owner, string PackageName, long Created)
    {
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

}

/// <summary>Delegates invocation access, never ownership, to the caller's original core handle table.</summary>
internal sealed record DelegatedCoreReference(NativeRpcConnection Owner, JsonObject Handle) : ICapabilityLifetime
{
    public bool IsRevoked => Owner.IsClosed || !Owner.IsReferenceAlive(Handle);
}

internal sealed record ProviderResourceReference(NativeProviderRegistry.Registration Provider, NativeRpcConnection Owner,
    JsonObject Handle, JsonObject Lifetime, NativeProviderRegistry.PublishedResource Resource) : ICapabilityLifetime
{
    public bool IsRevoked => Owner.IsClosed || Provider.Disconnected || Provider.Connection.IsClosed || !Owner.IsReferenceAlive(Lifetime);
}

internal sealed record ProviderCallback(NativeRpcConnection Owner, string Id, NativeProviderCallback Signature)
{
    internal NativeProviderRegistry.PublishedResource? Resource { get; set; }
    public bool IsRevoked => Owner.IsClosed || Resource is { } resource &&
        (resource.IsRetired || resource.Registration.Disconnected || resource.Declaration.IsRevoked);
}

internal sealed class ProviderCallbackLease(NativeRpcConnection connection, string id, ProviderCallback callback) : IDisposable
{
    public string Id { get; } = id;
    public ProviderCallback Callback { get; } = callback;
    public void Dispose() => connection.RemoveCallback(Id);
}
