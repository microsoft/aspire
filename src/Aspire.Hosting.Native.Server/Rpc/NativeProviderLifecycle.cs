// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Aspire.Hosting.Native.Api;
using Aspire.Hosting.Native.Runtime;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Native.Rpc;

internal sealed partial class NativeProviderRegistry
{
    private PublishedResource TrackProviderObject(Registration registration, NativeRpcConnection owner, string id,
        DeclaredResource declaration, string typeId)
    {
        lock (_gate)
        {
            if (registration.Resources.TryGetValue(id, out var existing))
            {
                if (!ReferenceEquals(existing.Owner, owner) || !ReferenceEquals(existing.Declaration.Composition, declaration.Composition) ||
                    existing.Declaration.Handle != declaration.Handle || existing.TypeId != typeId || existing.IsRetired)
                {
                    throw RpcFault.InvalidHandle("An integration object cannot change its original declaration authority.");
                }
                return existing;
            }
            if (registration.Resources.Count >= 4096)
            {
                throw RpcFault.ProviderLimit("Provider object capacity was reached.");
            }
            var bindings = registration.LifecycleCapabilities.TryGetValue(typeId, out var hooks)
                ? hooks.Select(metadata => new ProviderLifecycleBinding(registration.Id, id, typeId,
                    metadata.LifecyclePhase!, metadata.Id)).ToImmutableArray()
                : ImmutableArray<ProviderLifecycleBinding>.Empty;
            declaration.Composition.Model.SetProviderLifecycle(declaration.Handle, bindings);
            var resource = new PublishedResource(this, registration, owner, declaration, id, typeId);
            declaration.Composition.TrackProviderResource(resource);
            registration.Resources.Add(id, resource);

            return resource;
        }
    }

    public async Task InvokeAsync(ProviderLifecycleBinding binding, ResourceLifecycleAuthority authority, CancellationToken cancellationToken)
    {
        await Task.Yield();
        Registration registration;
        PublishedResource resource;
        lock (_gate)
        {
            if (!_providersById.TryGetValue(binding.ProviderId, out registration!))
            {
                throw RpcFault.ProviderDisconnected("The lifecycle provider is unavailable.");
            }
            if (!registration.Resources.TryGetValue(binding.ObjectId, out resource!) || resource.IsRetired ||
                resource.TypeId != binding.TypeId || !registration.Capabilities.TryGetValue(binding.CapabilityId, out var metadata) ||
                metadata.LifecyclePhase != authority.Phase)
            {
                throw RpcFault.InvalidHandle("The lifecycle object or phase authority is unavailable.");
            }
        }
        EnsureAvailable(registration);
        var connection = registration.Connection;
        var peer = registration.Peer;
        var scope = connection.AddLifecycleScope(new ResourceLifecycleScope(authority));
        var invocation = Guid.NewGuid().ToString();
        var metadataProjection = registration.Capabilities[binding.CapabilityId];
        var receiver = metadataProjection.TargetParameterName!;
        var arguments = new JsonObject
        {
            [receiver] = new JsonObject { ["$handle"] = binding.ObjectId, ["$type"] = binding.TypeId },
            ["scope"] = scope
        };
        using var deadline = new CancellationTokenSource(options.ProviderInvocationTimeout, options.TimeProvider);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token, peer.Closed);
        try
        {
            var result = await peer.RequestAsync("handleExternalCapability",
                new JsonArray(JsonValue.Create(binding.CapabilityId), arguments, JsonValue.Create(invocation)), cancellation.Token).ConfigureAwait(false);
            NativeProviderValueProtocol.ValidatePrimitive(result, metadataProjection.ReturnType);
            logger.LogDebug("Native provider lifecycle {Phase} completed.", authority.Phase);
        }
        catch (Exception)
        {
            // Revocation precedes cancellation acknowledgement: an in-flight
            // callback cannot keep executing native work after its phase ends.
            authority.Revoke();
            if (!peer.Closed.IsCancellationRequested)
            {
                using var cancelDeadline = new CancellationTokenSource(options.ProviderInvocationTimeout, options.TimeProvider);
                try
                {
                    await peer.RequestAsync("cancelProviderInvocation", new JsonArray(JsonValue.Create(invocation)),
                        cancelDeadline.Token).ConfigureAwait(false);
                }
                catch
                {
                    Disconnect(connection);
                }
            }
            const string message = "The provider lifecycle phase did not complete; it will not be replayed.";
            throw cancellationToken.IsCancellationRequested ? RpcFault.ProviderLifecycleCancelled(message) :
                deadline.IsCancellationRequested ? RpcFault.ProviderLifecycleTimeout(message) : RpcFault.ProviderLifecycleFailed(message);
        }
        finally
        {
            authority.Revoke();
            try
            {
                await authority.DrainAsync().ConfigureAwait(false);
            }
            finally
            {
                connection.RemoveLifecycleScopes();
            }
        }
    }

    private async Task ReleaseObjectAsync(PublishedResource resource)
    {
        await Task.Yield();
        INativeRpcPeer? peer;
        NativeRpcConnection? connection;
        ProviderCallbackLease[] callbacks;
        lock (_gate)
        {
            resource.IsRetired = true;
            resource.Registration.Resources.Remove(resource.ObjectId);
            callbacks = resource.Callbacks.ToArray();
            resource.Callbacks.Clear();
            peer = resource.Registration.Disconnected ? null : resource.Registration.Peer;
            connection = resource.Registration.Disconnected ? null : resource.Registration.Connection;
        }
        foreach (var callback in callbacks)
        {
            callback.Dispose();
        }
        if (peer is null || peer.Closed.IsCancellationRequested)
        {
            return;
        }
        using var deadline = new CancellationTokenSource(options.ProviderInvocationTimeout, options.TimeProvider);
        try
        {
            var result = await peer.RequestAsync("releaseIntegrationResource",
                new JsonArray(JsonValue.Create(resource.ObjectId)), deadline.Token).ConfigureAwait(false);
            if (result is not JsonValue success || !success.TryGetValue<bool>(out var released) || !released)
            {
                throw RpcFault.ProviderDisposalFailed("The provider did not release its retired object.");
            }
        }
        catch
        {
            // A provider that cannot honor release loses its connection, so no
            // callable objects or phase authorities remain registered locally.
            if (connection is not null)
            {
                Disconnect(connection);
            }
            throw RpcFault.ProviderDisposalFailed("The provider object release did not complete.");
        }
    }

    internal sealed class PublishedResource(NativeProviderRegistry registry, Registration registration,
        NativeRpcConnection owner, DeclaredResource declaration, string objectId, string typeId) : IProviderResourceRetirement
    {
        private readonly Lock _retirementGate = new();
        private Task? _retirement;
        internal Registration Registration { get; } = registration;
        internal NativeRpcConnection Owner { get; } = owner;
        internal DeclaredResource Declaration { get; } = declaration;
        internal string ObjectId { get; } = objectId;
        internal string TypeId { get; } = typeId;
        internal bool IsRetired { get; set; }
        internal List<ProviderCallbackLease> Callbacks { get; } = [];
        public Task RetireAsync()
        {
            lock (_retirementGate)
            {
                return _retirement ??= registry.ReleaseObjectAsync(this);
            }
        }
    }
}
