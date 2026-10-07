// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Aspire.TypeSystem;
using Aspire.Hosting.RemoteHost.Diagnostics;
using Aspire.Hosting.RemoteHost.Language;
using Microsoft.Extensions.Logging;
using StreamJsonRpc;

namespace Aspire.Hosting.RemoteHost.Ats;

/// <summary>
/// Singleton registry for external capabilities provided by integration hosts.
/// The engine calls getCapabilities on each integration host to populate this registry.
/// Scoped CapabilityDispatchers check this registry as a fallback.
/// </summary>
internal sealed class ExternalCapabilityRegistry : IDisposable
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private volatile FrozenDictionary<string, ExternalCapabilityRegistration> _capabilities = FrozenDictionary<string, ExternalCapabilityRegistration>.Empty;
    private readonly ConcurrentDictionary<JsonRpc, byte> _integrationHosts = new();
    private readonly ConcurrentDictionary<JsonRpc, JsonRpcCallbackInvoker> _integrationCallbackInvokers = new();
    private readonly ConcurrentDictionary<string, PendingRegistration> _pendingRegistrations = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<JsonRpc, string> _hostPackages = new();
    private readonly ConcurrentDictionary<JsonRpc, byte> _unavailableHosts = new();
    private readonly ConcurrentDictionary<string, CallbackOwner> _callbackOwners = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _hostRegisteredSignal = new(initialCount: 0);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _registrationGate = new();
    private readonly ILogger<ExternalCapabilityRegistry> _logger;
    private readonly TimeSpan _invocationTimeout;
    private readonly TimeSpan _callbackTimeout = IntegrationHostConfiguration.Default.CallbackTimeout;
    private readonly TimeProvider _timeProvider = TimeProvider.System;
    private readonly RemoteHostProfilingTelemetry _profilingTelemetry = RemoteHostProfilingTelemetry.Disabled;
    private InvalidOperationException? _initializationException;

    static ExternalCapabilityRegistry()
    {
        s_jsonOptions.Converters.Add(new JsonStringEnumConverter());
    }

    public ExternalCapabilityRegistry(ILogger<ExternalCapabilityRegistry> logger)
        : this(logger, IntegrationHostConfiguration.Default.InvocationTimeout)
    {
    }

    public ExternalCapabilityRegistry(ILogger<ExternalCapabilityRegistry> logger, IntegrationHostConfiguration configuration,
        TimeProvider timeProvider, RemoteHostProfilingTelemetry profilingTelemetry)
        : this(logger, configuration.InvocationTimeout, timeProvider)
    {
        _callbackTimeout = configuration.CallbackTimeout;
        _profilingTelemetry = profilingTelemetry;
    }

    internal ExternalCapabilityRegistry(ILogger<ExternalCapabilityRegistry> logger, TimeSpan invocationTimeout)
        : this(logger, invocationTimeout, TimeProvider.System)
    {
    }

    internal ExternalCapabilityRegistry(ILogger<ExternalCapabilityRegistry> logger, TimeSpan invocationTimeout, TimeProvider timeProvider)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(invocationTimeout, TimeSpan.Zero);
        _logger = logger;
        _invocationTimeout = invocationTimeout;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Tracks an integration host connection. The engine will call getCapabilities on it later.
    /// Releases the registration signal so any caller of <see cref="WaitForHostsAsync"/> can
    /// observe the new registration immediately rather than blind-waiting on a timer.
    /// </summary>
    public void AddIntegrationHost(JsonRpc clientRpc)
    {
        lock (_registrationGate)
        {
            _shutdown.Token.ThrowIfCancellationRequested();
            if (!_integrationHosts.TryAdd(clientRpc, 0))
            {
                throw new InvalidOperationException("This connection is already registered as an integration host.");
            }
            _logger.LogInformation("Integration host connected ({RpcHash}), total: {Count}",
                clientRpc.GetHashCode(), _integrationHosts.Count);
            _hostRegisteredSignal.Release();
        }
    }

    internal Task<JsonRpc> ExpectHostRegistration(string registrationId)
        => ExpectHostRegistration(registrationId, null);

    internal Task<JsonRpc> ExpectHostRegistration(string registrationId, string? packageName)
    {
        var registration = new PendingRegistration(
            new TaskCompletionSource<JsonRpc>(TaskCreationOptions.RunContinuationsAsynchronously), packageName);
        if (!_pendingRegistrations.TryAdd(registrationId, registration))
        {
            throw new InvalidOperationException($"Integration host registration '{registrationId}' is already pending.");
        }

        return registration.Completion.Task;
    }

    internal void ForgetHostRegistration(string registrationId)
        => _pendingRegistrations.TryRemove(registrationId, out _);

    public void AddIntegrationHost(string registrationId, JsonRpc clientRpc, JsonRpcCallbackInvoker callbackInvoker)
    {
        if (!_pendingRegistrations.TryRemove(registrationId, out var registration))
        {
            throw new InvalidOperationException("The integration host registration is unknown or has already been consumed.");
        }

        try
        {
            lock (_registrationGate)
            {
                if (registration.PackageName is { } packageName)
                {
                    _hostPackages[clientRpc] = packageName;
                }
                AddIntegrationHost(clientRpc);
                _integrationCallbackInvokers[clientRpc] = callbackInvoker;
                registration.Completion.SetResult(clientRpc);
            }
        }
        catch (Exception ex)
        {
            _hostPackages.TryRemove(clientRpc, out _);
            registration.Completion.TrySetException(ex);
            throw;
        }
    }

    internal bool MarkHostUnavailable(JsonRpc clientRpc)
    {
        lock (_registrationGate)
        {
            var hasCallbacks = (_integrationCallbackInvokers.TryGetValue(clientRpc, out var invoker) &&
                invoker.StopAcceptingCallbacks()) ||
                (_unavailableHosts.TryGetValue(clientRpc, out var state) && state != 0) ||
                _callbackOwners.Values.Any(owner => ReferenceEquals(owner.Host, clientRpc));

            // Keep projection metadata for already-generated SDKs, but never route a new
            // invocation to a disconnected host or replay calls that may have side effects.
            _unavailableHosts[clientRpc] = hasCallbacks ? (byte)1 : (byte)0;
            _integrationHosts.TryRemove(clientRpc, out _);
            RemoveCallbackOwners(owner => ReferenceEquals(owner.Host, clientRpc));
            clientRpc.Dispose();

            return hasCallbacks;
        }
    }

    /// <summary>
    /// Waits for the next <paramref name="expectedCount"/> integration host registrations
    /// (each via <see cref="AddIntegrationHost(JsonRpc)"/>), with a per-host timeout. Returns the
    /// number of registrations actually consumed before the timeout fired.
    /// Cancellation propagates to the caller.
    ///
    /// The signal is a counting semaphore released once per registration, so registrations
    /// that happened *before* this call are still consumable — the caller does not have to
    /// race the host startup. If a host has already registered when this is called, the
    /// matching wait returns immediately.
    ///
    /// Used by <c>IntegrationHostLauncher</c> at server startup to synchronise codegen
    /// against the integration hosts it just spawned, replacing the older blind-wait.
    /// </summary>
    public async Task<int> WaitForHostsAsync(int expectedCount, TimeSpan timeoutPerHost, CancellationToken cancellationToken)
    {
        if (expectedCount <= 0)
        {
            return 0;
        }

        var registered = 0;
        while (registered < expectedCount)
        {
            using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            try
            {
                await _hostRegisteredSignal.WaitAsync(waitCancellation.Token)
                    .WaitAsync(timeoutPerHost, _timeProvider, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _logger.LogWarning(
                    "Timed out waiting for integration host registration after {Registered}/{Expected} (per-host timeout {Timeout}).",
                    registered, expectedCount, timeoutPerHost);
                break;
            }
            finally
            {
                waitCancellation.Cancel();
            }

            registered++;
        }

        return registered;
    }

    /// <summary>
    /// Calls getCapabilities on all connected integration hosts with a per-host timeout,
    /// then publishes the validated registrations before codegen.
    /// </summary>
    public async Task InitializeAllHostsAsync(int expectedCount, TimeSpan timeoutPerHost, CancellationToken cancellationToken)
    {
        var hosts = _integrationHosts.Keys.ToArray();
        // A consumed registration signal does not guarantee that its connection
        // survived until discovery. Never publish a partial SDK after early exit.
        if (hosts.Length != expectedCount)
        {
            _initializationException = new InvalidOperationException(
                $"Only {hosts.Length} of {expectedCount} integration hosts remain connected for capability discovery.");
            _logger.LogError(_initializationException, "Integration host capability discovery failed.");
            throw _initializationException;
        }
        _logger.LogInformation("Initializing {Count} integration host(s)...", hosts.Length);
        var capabilities = new Dictionary<string, ExternalCapabilityRegistration>(StringComparer.Ordinal);

        foreach (var host in hosts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var discoveryCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            List<ExternalCapabilityRegistration> registrations;
            _hostPackages.TryGetValue(host, out var packageName);
            using var activity = _profilingTelemetry.StartIntegrationHostPhase("discovery", packageName, timeout: timeoutPerHost);
            var started = _timeProvider.GetTimestamp();
            _logger.LogInformation("Integration capability discovery started: package {Name}, connection {Host}, timeout {Timeout}.",
                packageName, host.GetHashCode(), timeoutPerHost);
            try
            {
                var capsPayload = await host.InvokeWithCancellationAsync<JsonElement>(
                    "getCapabilities",
                    Array.Empty<object>(),
                    discoveryCancellation.Token)
                    .WaitAsync(timeoutPerHost, _timeProvider, cancellationToken).ConfigureAwait(false);

                registrations = ReadCapabilities(capsPayload).Select(cap => new ExternalCapabilityRegistration
                {
                    CapabilityId = cap.Id,
                    ClientRpc = host,
                    SupportsInvocationIds = SupportsInvocationIds(capsPayload),
                    ProjectedCapability = CreateProjectedCapability(cap),
                    Signature = CreateSignature(cap)
                }).ToList();
            }
            catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
            {
                activity.SetError(ex);
                throw;
            }
            catch (TimeoutException ex)
            {
                // Bound the local wait even if the integration host ignores RPC cancellation.
                discoveryCancellation.Cancel();
                _initializationException = new InvalidOperationException(
                    $"Timed out getting capabilities from integration host '{packageName}' (connection {host.GetHashCode()}) after {timeoutPerHost}. " +
                    "SDK generation cannot continue without all configured integrations.", ex);
                _logger.LogError(_initializationException, "Integration host capability discovery failed.");
                activity.SetError(_initializationException);
                throw _initializationException;
            }
            catch (Exception ex)
            {
                _initializationException = new InvalidOperationException(
                    $"Failed to get capabilities from integration host '{packageName}' (connection {host.GetHashCode()}).", ex);
                _logger.LogError(_initializationException, "Integration host capability discovery failed.");
                activity.SetError(_initializationException);
                throw _initializationException;
            }
            finally
            {
                _logger.LogInformation("Integration capability discovery finished: package {Name}, connection {Host}, elapsed {Elapsed}, timeout {Timeout}.",
                    packageName, host.GetHashCode(), _timeProvider.GetElapsedTime(started), timeoutPerHost);
            }

            foreach (var registration in registrations)
            {
                if (!capabilities.TryAdd(registration.CapabilityId, registration))
                {
                    var previous = capabilities[registration.CapabilityId];
                    _initializationException = new InvalidOperationException(
                        $"Capability ID '{registration.CapabilityId}' is provided by multiple external registrations " +
                        $"(integration hosts {previous.ClientRpc.GetHashCode()} and {host.GetHashCode()}). Capability IDs must be unique.");
                    _logger.LogError(_initializationException, "Conflicting external capability registrations.");
                    activity.SetError(_initializationException);
                    throw _initializationException;
                }
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        // Publish only after validating the entire set, so collisions cannot leave a
        // partially registered SDK or choose a host based on connection enumeration order.
        lock (_registrationGate)
        {
            _capabilities = capabilities.ToFrozenDictionary(StringComparer.Ordinal);
        }
        _initializationException = null;
        foreach (var capabilityId in _capabilities.Keys.Order(StringComparer.Ordinal))
        {
            _logger.LogInformation("Registered external capability: {CapabilityId}", capabilityId);
        }

        _logger.LogInformation("Integration hosts initialized. External capabilities: {Count}", _capabilities.Count);
    }

    internal async Task ReplaceHostAsync(JsonRpc previous, JsonRpc replacement, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if ((_integrationCallbackInvokers.TryGetValue(previous, out var invoker) && invoker.StopAcceptingCallbacks()) ||
            (_unavailableHosts.TryGetValue(previous, out var state) && state != 0))
        {
            throw new InvalidOperationException(
                "The integration host contributed callbacks to the resource model. " +
                "Restart the AppHost session to rebuild those callbacks before using the integration.");
        }

        using var discoveryCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        List<ExternalCapabilityRegistration> discovered;
        try
        {
            var payload = await replacement.InvokeWithCancellationAsync<JsonElement>(
                "getCapabilities", Array.Empty<object>(), discoveryCancellation.Token)
                .WaitAsync(timeout, _timeProvider, discoveryCancellation.Token).ConfigureAwait(false);
            discovered = ReadCapabilities(payload).Select(cap => new ExternalCapabilityRegistration
            {
                CapabilityId = cap.Id,
                ClientRpc = replacement,
                SupportsInvocationIds = SupportsInvocationIds(payload),
                ProjectedCapability = CreateProjectedCapability(cap),
                Signature = CreateSignature(cap)
            }).ToList();
        }
        finally
        {
            discoveryCancellation.Cancel();
        }

        cancellationToken.ThrowIfCancellationRequested();
        lock (_registrationGate)
        {
            _shutdown.Token.ThrowIfCancellationRequested();
            cancellationToken.ThrowIfCancellationRequested();
            var current = _capabilities;
            var owned = current.Values.Where(cap => ReferenceEquals(cap.ClientRpc, previous))
                .ToDictionary(cap => cap.CapabilityId, StringComparer.Ordinal);
            if (discovered.Count != owned.Count ||
                discovered.Select(cap => cap.CapabilityId).Distinct(StringComparer.Ordinal).Count() != discovered.Count ||
                discovered.Any(cap => !owned.TryGetValue(cap.CapabilityId, out var original) ||
                    !JsonElement.DeepEquals(original.Signature, cap.Signature)))
            {
                throw new InvalidOperationException(
                    "The restarted integration host changed its capability signatures. " +
                    "Restart the AppHost session and regenerate its SDK before using the changed integration.");
            }

            // Publish the complete replacement atomically. Other hosts can recover concurrently,
            // and callers must never observe a mixture of old and new connections for one host.
            var updated = current.ToDictionary();
            foreach (var registration in discovered)
            {
                updated[registration.CapabilityId] = registration;
            }
            _capabilities = updated.ToFrozenDictionary(StringComparer.Ordinal);
            _unavailableHosts.TryRemove(previous, out _);
            _integrationCallbackInvokers.TryRemove(previous, out _);
            _hostPackages.TryRemove(previous, out _);
        }
    }

    public AtsContext AugmentContext(AtsContext context)
    {
        // Preserve discovery failures for every caller instead of returning a partial SDK.
        if (_initializationException is not null)
        {
            throw new InvalidOperationException("Integration host capability discovery failed.", _initializationException);
        }

        var registrations = _capabilities;
        var duplicateId = context.Capabilities
            .Select(c => c.CapabilityId)
            .Where(registrations.ContainsKey)
            .Order(StringComparer.Ordinal)
            .FirstOrDefault();
        if (duplicateId is not null)
        {
            // Dispatch prefers managed capabilities, so external metadata with the same
            // ID would describe a different implementation than the one actually invoked.
            throw new InvalidOperationException(
                $"Capability ID '{duplicateId}' is provided by both a managed capability and an external integration host. Capability IDs must be unique.");
        }

        var projectedCapabilities = registrations.Values
            .Select(c => c.ProjectedCapability)
            .OrderBy(c => c.CapabilityId, StringComparer.Ordinal)
            .ToList();

        if (projectedCapabilities.Count == 0)
        {
            return context;
        }

        var mergedContext = new AtsContext
        {
            Capabilities = context.Capabilities
                .Concat(projectedCapabilities)
                .ToList(),
            HandleTypes = context.HandleTypes,
            DtoTypes = context.DtoTypes,
            EnumTypes = context.EnumTypes,
            ExportedValues = context.ExportedValues,
            Diagnostics = context.Diagnostics
        };

        foreach (var (id, method) in context.Methods)
        {
            mergedContext.Methods[id] = method;
        }

        foreach (var (id, property) in context.Properties)
        {
            mergedContext.Properties[id] = property;
        }

        return mergedContext;
    }

    /// <summary>
    /// Tries to invoke an external capability by forwarding to the integration host.
    /// The <paramref name="ownerInvoker"/> identifies the guest-side JSON-RPC connection that
    /// issued this call, so any callback arguments can be routed back to the originating guest
    /// via <c>invokeGuestCallback</c>.
    /// </summary>
    public async Task<(bool Found, JsonNode? Result)> TryInvokeAsync(
        string capabilityId, JsonObject? args, JsonRpcCallbackInvoker? ownerInvoker = null, CancellationToken cancellationToken = default)
    {
        if (!_capabilities.TryGetValue(capabilityId, out var registration))
        {
            return (false, null);
        }

        if (_unavailableHosts.TryGetValue(registration.ClientRpc, out var state))
        {
            if (state != 0)
            {
                throw new InvalidOperationException(
                    $"The integration host providing '{capabilityId}' contributed callbacks to the resource model. " +
                    "Restart the AppHost session to rebuild those callbacks before using the integration.");
            }

            throw new InvalidOperationException(
                $"The integration host providing '{capabilityId}' is restarting. Retry after it has registered again.");
        }

        var invocationId = Guid.NewGuid().ToString("N");
        var started = _timeProvider.GetTimestamp();
        var forwardedArgs = args?.DeepClone().AsObject();
        using var invocationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _shutdown.Token, ownerInvoker?.LifetimeToken ?? CancellationToken.None);
        invocationCancellation.Token.ThrowIfCancellationRequested();
        RegisterCallbackOwners(registration, forwardedArgs, ownerInvoker, invocationId);
        _logger.LogInformation(
            "Integration invocation {InvocationId} started: capability {CapabilityId}, host {Host}, timeout {Timeout}.",
            invocationId, capabilityId, registration.ClientRpc.GetHashCode(), _invocationTimeout);

        try
        {
            var rawResult = await registration.ClientRpc.InvokeWithCancellationAsync<object?>(
                "handleExternalCapability",
                registration.SupportsInvocationIds
                    ? new object?[] { capabilityId, forwardedArgs, invocationId }
                    : new object?[] { capabilityId, forwardedArgs },
                invocationCancellation.Token).WaitAsync(_invocationTimeout, _timeProvider, invocationCancellation.Token).ConfigureAwait(false);

            // SystemTextJsonFormatter returns JsonElement for object?
            JsonNode? result;
            if (rawResult is JsonElement je)
            {
                result = JsonNode.Parse(je.GetRawText());
            }
            else if (rawResult is JsonNode jn)
            {
                result = jn;
            }
            else if (rawResult is not null)
            {
                result = JsonSerializer.SerializeToNode(rawResult);
            }
            else
            {
                result = null;
            }

            return (true, result);
        }
        catch (TimeoutException ex)
        {
            invocationCancellation.Cancel();
            MarkHostUnavailable(registration.ClientRpc);
            var error = new TimeoutException(
                $"Integration capability '{capabilityId}' (invocation {invocationId}, host {registration.ClientRpc.GetHashCode()}) " +
                $"timed out after {_invocationTimeout}. The host connection was retired to stop outstanding work. " +
                "The call will not be replayed. Check the integration host logs; restart the AppHost session if it owns callbacks.", ex);
            _logger.LogError(error,
                "Integration invocation {InvocationId} stalled: capability {CapabilityId}, host {Host}, elapsed {Elapsed}, timeout {Timeout}. " +
                "Retired the connection; supervised process cleanup must complete before recovery. In-flight work is not replayed.",
                invocationId, capabilityId, registration.ClientRpc.GetHashCode(), _timeProvider.GetElapsedTime(started), _invocationTimeout);
            throw error;
        }
        catch (OperationCanceledException) when (invocationCancellation.IsCancellationRequested)
        {
            // RPC cancellation alone cannot prove user code stopped mutating the model.
            // Retiring the connection makes the supervisor reap the complete old scope.
            MarkHostUnavailable(registration.ClientRpc);
            _logger.LogInformation(
                "Integration invocation {InvocationId} canceled: capability {CapabilityId}, host {Host}, elapsed {Elapsed}. " +
                "The connection was retired; in-flight work is not replayed.",
                invocationId, capabilityId, registration.ClientRpc.GetHashCode(), _timeProvider.GetElapsedTime(started));
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Integration invocation {InvocationId} failed: capability {CapabilityId}, host {Host}, elapsed {Elapsed}.",
                invocationId, capabilityId, registration.ClientRpc.GetHashCode(), _timeProvider.GetElapsedTime(started));
            throw;
        }
        finally
        {
            _logger.LogInformation("Integration invocation {InvocationId} finished: capability {CapabilityId}, host {Host}, elapsed {Elapsed}.",
                invocationId, capabilityId, registration.ClientRpc.GetHashCode(), _timeProvider.GetElapsedTime(started));
        }
    }

    /// <summary>
    /// Returns the guest connection and original callback ID for a relay ID,
    /// or <c>null</c> if the relay ID is not currently registered.
    /// Integration hosts use this to route <c>invokeGuestCallback</c> back to the originating guest.
    /// </summary>
    public (JsonRpcCallbackInvoker Invoker, string CallbackId)? ResolveCallbackOwner(string callbackId)
        => _callbackOwners.TryGetValue(callbackId, out var owner) ? (owner.Invoker, owner.CallbackId) : null;

    internal async Task<JsonNode?> InvokeGuestCallbackAsync(JsonRpc host, string callbackId, JsonObject? args, CancellationToken cancellationToken)
    {
        if (!_callbackOwners.TryGetValue(callbackId, out var owner) || !ReferenceEquals(owner.Host, host))
        {
            throw new InvalidOperationException(
                $"No guest callback relay '{callbackId}' belongs to integration host {host.GetHashCode()}. " +
                "Its owning connection may have disconnected; restart the AppHost session to rebuild deferred callbacks.");
        }

        var started = _timeProvider.GetTimestamp();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token, owner.Invoker.LifetimeToken);
        _logger.LogInformation(
            "Guest callback relay {CallbackId} started: integration invocation {InvocationId}, capability {CapabilityId}, host {Host}, timeout {Timeout}.",
            callbackId, owner.InvocationId, owner.CapabilityId, host.GetHashCode(), _callbackTimeout);
        try
        {
            // The guest may await integration calls, so its outer callback budget must remain independent.
            return await owner.Invoker.InvokeAsync<JsonNode?>(owner.CallbackId, args, cancellation.Token, _callbackTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            cancellation.Cancel();
            MarkHostUnavailable(host);
            _logger.LogError(ex,
                "Guest callback relay {CallbackId} stalled: integration invocation {InvocationId}, capability {CapabilityId}, host {Host}, elapsed {Elapsed}, timeout {Timeout}. " +
                "The integration connection was retired; restart the AppHost session to rebuild callbacks.",
                callbackId, owner.InvocationId, owner.CapabilityId, host.GetHashCode(), _timeProvider.GetElapsedTime(started), _callbackTimeout);
            throw new TimeoutException(
                $"Guest callback relay '{callbackId}' for integration capability '{owner.CapabilityId}' " +
                $"(invocation {owner.InvocationId}) timed out after {_callbackTimeout}. Restart the AppHost session to rebuild callbacks.", ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Guest callback relay {CallbackId} failed: integration invocation {InvocationId}, capability {CapabilityId}, host {Host}, elapsed {Elapsed}.",
                callbackId, owner.InvocationId, owner.CapabilityId, host.GetHashCode(), _timeProvider.GetElapsedTime(started));
            throw;
        }
        finally
        {
            _logger.LogInformation(
                "Guest callback relay {CallbackId} finished: integration invocation {InvocationId}, capability {CapabilityId}, host {Host}, elapsed {Elapsed}.",
                callbackId, owner.InvocationId, owner.CapabilityId, host.GetHashCode(), _timeProvider.GetElapsedTime(started));
        }
    }

    internal CancellationToken[] GetCancellationTokens(string capabilityId, JsonObject? args, AtsMarshaller marshaller)
    {
        if (args is null || !_capabilities.TryGetValue(capabilityId, out var registration))
        {
            return [];
        }

        return registration.ProjectedCapability.Parameters
            .Where(parameter => parameter.Type?.TypeId == AtsConstants.CancellationToken)
            .Select(parameter => (CancellationToken)marshaller.UnmarshalFromJson(
                args[parameter.Name], typeof(CancellationToken),
                new AtsMarshaller.UnmarshalContext { CapabilityId = capabilityId, ParameterName = parameter.Name })!)
            .ToArray();
    }

    internal void RemoveGuestCallbacks(JsonRpcCallbackInvoker invoker)
    {
        lock (_registrationGate)
        {
            invoker.StopAcceptingCallbacks();
            RemoveCallbackOwners(owner => ReferenceEquals(owner.Invoker, invoker));
        }
    }

    private void RemoveCallbackOwners(Func<CallbackOwner, bool> predicate)
    {
        foreach (var (id, owner) in _callbackOwners)
        {
            if (predicate(owner) && _callbackOwners.TryRemove(id, out _))
            {
                _logger.LogDebug("Retired guest callback relay {CallbackId} from integration invocation {InvocationId}, capability {CapabilityId}, host {Host}.",
                    id, owner.InvocationId, owner.CapabilityId, owner.Host.GetHashCode());
            }
        }
    }

    private void RegisterCallbackOwners(ExternalCapabilityRegistration registration, JsonObject? args, JsonRpcCallbackInvoker? ownerInvoker, string invocationId)
    {
        if (args is null || ownerInvoker is null)
        {
            return;
        }

        lock (_registrationGate)
        {
            _shutdown.Token.ThrowIfCancellationRequested();
            if (_unavailableHosts.ContainsKey(registration.ClientRpc))
            {
                throw new InvalidOperationException($"The integration host providing '{registration.CapabilityId}' was retired before callback admission.");
            }

            foreach (var parameter in registration.ProjectedCapability.Parameters.Where(parameter => parameter.IsCallback))
            {
                if (args[parameter.Name] is JsonValue value && value.TryGetValue<string>(out var callbackId) && !string.IsNullOrEmpty(callbackId))
                {
                    // Both ends own this relay. Integration code can retain it in a deferred
                    // model callback, even if the export later fails after mutating the model.
                    ownerInvoker.RegisterCallback();
                    if (_integrationCallbackInvokers.TryGetValue(registration.ClientRpc, out var hostInvoker))
                    {
                        hostInvoker.RegisterCallback();
                    }
                    var relayId = $"external_callback_{Guid.NewGuid():N}";
                    _callbackOwners[relayId] = new CallbackOwner(registration.ClientRpc, ownerInvoker, callbackId, invocationId, registration.CapabilityId);
                    args[parameter.Name] = relayId;
                    _logger.LogDebug("Registered guest callback relay {CallbackId} for integration invocation {InvocationId}, capability {CapabilityId}, host {Host}.",
                        relayId, invocationId, registration.CapabilityId, registration.ClientRpc.GetHashCode());
                }
            }
        }
    }

    /// <summary>
    /// Checks if a capability is registered externally.
    /// </summary>
    public bool IsRegistered(string capabilityId) => _capabilities.ContainsKey(capabilityId);

    internal void Stop()
    {
        lock (_registrationGate)
        {
            _shutdown.Cancel();
            foreach (var host in _integrationHosts.Keys)
            {
                MarkHostUnavailable(host);
            }
            foreach (var registration in _pendingRegistrations.Values)
            {
                registration.Completion.TrySetCanceled(_shutdown.Token);
            }
            _pendingRegistrations.Clear();
            _callbackOwners.Clear();
        }
    }

    public void Dispose()
    {
        Stop();
        _integrationCallbackInvokers.Clear();
        _hostPackages.Clear();
        _shutdown.Dispose();
        _hostRegisteredSignal.Dispose();
    }

    private static JsonElement CreateSignature(ExternalCapabilityProjection capability)
        => JsonSerializer.SerializeToElement(new
        {
            capability.Id,
            capability.Method,
            capability.CapabilityKind,
            capability.Parameters,
            capability.ReturnType,
            capability.TargetTypeId,
            capability.TargetType,
            capability.TargetParameterName,
            capability.ReturnsBuilder,
            capability.OwningTypeName,
            capability.ExpandedTargetTypes
        }, s_jsonOptions);

    private static IReadOnlyList<ExternalCapabilityProjection> ReadCapabilities(JsonElement payload)
    {
        if (payload.ValueKind == JsonValueKind.Object &&
            payload.TryGetProperty("capabilities", out var capabilitiesElement))
        {
            payload = capabilitiesElement;
        }
        if (payload.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("Integration host capabilities must be an array or an object containing a 'capabilities' array.");
        }

        return JsonSerializer.Deserialize<List<ExternalCapabilityProjection>>(payload.GetRawText(), s_jsonOptions)
            ?? throw new JsonException("Integration host capabilities must not be null.");
    }

    private static bool SupportsInvocationIds(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty("protocolVersion", out var version))
        {
            return false;
        }
        if (version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var value) || value != 2)
        {
            throw new JsonException("Integration host protocolVersion must be 2 when supplied.");
        }
        return true;
    }

    private static AtsCapabilityInfo CreateProjectedCapability(ExternalCapabilityProjection capability)
    {
        if (capability.ReturnType is null)
        {
            throw new JsonException(
                $"Integration capability '{capability.Id}' must declare returnType; use '{AtsConstants.Void}' for a void method.");
        }

        return new AtsCapabilityInfo
        {
            CapabilityId = capability.Id,
            MethodName = capability.Method,
            OwningTypeName = capability.OwningTypeName,
            Description = capability.Description,
            Parameters = capability.Parameters.Select(CreateParameterInfo).ToList(),
            ReturnType = CreateTypeRef(capability.ReturnType),
            TargetTypeId = capability.TargetTypeId ?? capability.TargetType?.TypeId,
            TargetType = capability.TargetType is not null ? CreateTypeRef(capability.TargetType) : null,
            TargetParameterName = capability.TargetParameterName,
            ExpandedTargetTypes = capability.ExpandedTargetTypes?.Select(CreateTypeRef).ToList() ?? [],
            ReturnsBuilder = capability.ReturnsBuilder,
            CapabilityKind = capability.CapabilityKind,
            SourceLocation = $"external:{capability.Id}",
            RunSyncOnBackgroundThread = false
        };
    }

    private static AtsParameterInfo CreateParameterInfo(ExternalCapabilityParameter parameter)
        => new()
        {
            Name = parameter.Name,
            Type = parameter.Type is not null ? CreateTypeRef(parameter.Type) : null,
            IsOptional = parameter.IsOptional,
            IsNullable = parameter.IsNullable,
            IsCallback = parameter.IsCallback,
            CallbackParameters = parameter.CallbackParameters?
                .Select(p => new AtsCallbackParameterInfo
                {
                    Name = p.Name,
                    Type = p.Type is not null
                        ? CreateTypeRef(p.Type)
                        : new AtsTypeRef { TypeId = "unknown", Category = AtsTypeCategory.Unknown }
                })
                .ToList(),
            CallbackReturnType = parameter.CallbackReturnType is not null
                ? CreateTypeRef(parameter.CallbackReturnType)
                : null,
            DefaultValue = null
        };

    private static AtsTypeRef CreateTypeRef(ExternalTypeRef typeRef)
        => new()
        {
            TypeId = typeRef.TypeId,
            Category = typeRef.Category,
            IsInterface = typeRef.IsInterface,
            IsNullable = typeRef.IsNullable,
            IsReadOnly = typeRef.IsReadOnly,
            ElementType = typeRef.ElementType is not null ? CreateTypeRef(typeRef.ElementType) : null,
            KeyType = typeRef.KeyType is not null ? CreateTypeRef(typeRef.KeyType) : null,
            ValueType = typeRef.ValueType is not null ? CreateTypeRef(typeRef.ValueType) : null,
            UnionTypes = typeRef.UnionTypes?.Select(CreateTypeRef).ToList()
        };

    private sealed class ExternalCapabilityRegistration
    {
        public required string CapabilityId { get; init; }
        public required JsonRpc ClientRpc { get; init; }
        public required JsonElement Signature { get; init; }
        public bool SupportsInvocationIds { get; init; }
        public required AtsCapabilityInfo ProjectedCapability { get; init; }
    }

    private sealed record PendingRegistration(TaskCompletionSource<JsonRpc> Completion, string? PackageName);

    private sealed record CallbackOwner(
        JsonRpc Host, JsonRpcCallbackInvoker Invoker, string CallbackId, string InvocationId, string CapabilityId);

    private sealed class ExternalCapabilityProjection
    {
        public string Id { get; set; } = "";
        public string Method { get; set; } = "";
        public string Description { get; set; } = "";
        public AtsCapabilityKind CapabilityKind { get; set; } = AtsCapabilityKind.Method;
        public List<ExternalCapabilityParameter> Parameters { get; set; } = [];
        public ExternalTypeRef? ReturnType { get; set; }
        public string? TargetTypeId { get; set; }
        public ExternalTypeRef? TargetType { get; set; }
        public string? TargetParameterName { get; set; }
        public bool ReturnsBuilder { get; set; }
        public string? OwningTypeName { get; set; }
        public List<ExternalTypeRef>? ExpandedTargetTypes { get; set; }
    }

    private sealed class ExternalCapabilityParameter
    {
        public string Name { get; set; } = "";
        public ExternalTypeRef? Type { get; set; }
        public bool IsOptional { get; set; }
        public bool IsNullable { get; set; }
        public bool IsCallback { get; set; }
        public List<ExternalCallbackParameter>? CallbackParameters { get; set; }
        public ExternalTypeRef? CallbackReturnType { get; set; }
    }

    private sealed class ExternalCallbackParameter
    {
        public string Name { get; set; } = "";
        public ExternalTypeRef? Type { get; set; }
    }

    private sealed class ExternalTypeRef
    {
        public string TypeId { get; set; } = "";
        public AtsTypeCategory Category { get; set; }
        public bool IsInterface { get; set; }
        public bool? IsNullable { get; set; }
        public bool IsReadOnly { get; set; }
        public ExternalTypeRef? ElementType { get; set; }
        public ExternalTypeRef? KeyType { get; set; }
        public ExternalTypeRef? ValueType { get; set; }
        public List<ExternalTypeRef>? UnionTypes { get; set; }
    }
}
