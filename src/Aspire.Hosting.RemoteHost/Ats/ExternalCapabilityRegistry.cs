// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Aspire.TypeSystem;
using Microsoft.Extensions.Logging;
using StreamJsonRpc;

namespace Aspire.Hosting.RemoteHost.Ats;

/// <summary>
/// Singleton registry for external capabilities provided by integration hosts.
/// The engine calls getCapabilities on each integration host to populate this registry.
/// Scoped CapabilityDispatchers check this registry as a fallback.
/// </summary>
internal sealed class ExternalCapabilityRegistry
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly ConcurrentDictionary<string, ExternalCapabilityRegistration> _capabilities = new();
    private readonly List<JsonRpc> _integrationHosts = new();
    private readonly ConcurrentDictionary<string, JsonRpcCallbackInvoker> _callbackOwners = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _hostRegisteredSignal = new(initialCount: 0);
    private readonly ILogger<ExternalCapabilityRegistry> _logger;

    static ExternalCapabilityRegistry()
    {
        s_jsonOptions.Converters.Add(new JsonStringEnumConverter());
    }

    public ExternalCapabilityRegistry(ILogger<ExternalCapabilityRegistry> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Tracks an integration host connection. The engine will call getCapabilities on it later.
    /// Releases the registration signal so any caller of <see cref="WaitForHostsAsync"/> can
    /// observe the new registration immediately rather than blind-waiting on a timer.
    /// </summary>
    public void AddIntegrationHost(JsonRpc clientRpc)
    {
        _integrationHosts.Add(clientRpc);
        _logger.LogInformation("Integration host connected ({RpcHash}), total: {Count}",
            clientRpc.GetHashCode(), _integrationHosts.Count);
        _hostRegisteredSignal.Release();
    }

    /// <summary>
    /// Waits for the next <paramref name="expectedCount"/> integration host registrations
    /// (each via <see cref="AddIntegrationHost"/>), with a per-host timeout. Returns the
    /// number of registrations actually consumed before the timeout fired or
    /// <paramref name="cancellationToken"/> was triggered.
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
            bool got;
            try
            {
                got = await _hostRegisteredSignal.WaitAsync(timeoutPerHost, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }

            if (!got)
            {
                _logger.LogWarning(
                    "Timed out waiting for integration host registration after {Registered}/{Expected} (per-host timeout {Timeout}).",
                    registered, expectedCount, timeoutPerHost);
                break;
            }

            registered++;
        }

        return registered;
    }

    /// <summary>
    /// Calls getCapabilities on all connected integration hosts and registers the results.
    /// Called by the CLI (via RPC) before codegen.
    /// </summary>
    public async Task InitializeAllHostsAsync()
    {
        _logger.LogInformation("Initializing {Count} integration host(s)...", _integrationHosts.Count);

        foreach (var host in _integrationHosts)
        {
            try
            {
                var capsPayload = await host.InvokeWithCancellationAsync<JsonElement>(
                    "getCapabilities",
                    Array.Empty<object>(),
                    CancellationToken.None).ConfigureAwait(false);

                foreach (var cap in ReadCapabilities(capsPayload))
                {
                    var projectedCapability = TryCreateProjectedCapability(cap);
                    _capabilities[cap.Id] = new ExternalCapabilityRegistration
                    {
                        CapabilityId = cap.Id,
                        ClientRpc = host,
                        ProjectedCapability = projectedCapability
                    };

                    _logger.LogInformation("Registered external capability: {CapabilityId}", cap.Id);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get capabilities from integration host {RpcHash}", host.GetHashCode());
            }
        }

        _logger.LogInformation("Integration hosts initialized. External capabilities: {Count}", _capabilities.Count);
    }

    public AtsContext AugmentContext(AtsContext context)
    {
        var projectedCapabilities = _capabilities.Values
            .Select(c => c.ProjectedCapability)
            .OfType<AtsCapabilityInfo>()
            .ToList();

        if (projectedCapabilities.Count == 0)
        {
            return context;
        }

        var mergedContext = new AtsContext
        {
            Capabilities = context.Capabilities
                .Concat(projectedCapabilities)
                .GroupBy(c => c.CapabilityId, StringComparer.Ordinal)
                .Select(g => g.Last())
                .ToList(),
            HandleTypes = context.HandleTypes,
            DtoTypes = context.DtoTypes,
            EnumTypes = context.EnumTypes,
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
    public async Task<(bool Found, JsonNode? Result)> TryInvokeAsync(string capabilityId, JsonObject? args, JsonRpcCallbackInvoker? ownerInvoker = null)
    {
        if (!_capabilities.TryGetValue(capabilityId, out var registration))
        {
            return (false, null);
        }

        _logger.LogDebug("Forwarding capability {CapabilityId} to integration host", capabilityId);

        // Register callback ownership for any IsCallback parameters in the args so the
        // integration host can later relay invokeCallback back to the originating guest.
        var registeredCallbackIds = RegisterCallbackOwners(registration.ProjectedCapability, args, ownerInvoker);

        try
        {
            var rawResult = await registration.ClientRpc.InvokeWithCancellationAsync<object?>(
                "handleExternalCapability",
                new object?[] { capabilityId, args },
                CancellationToken.None).ConfigureAwait(false);

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
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error forwarding capability {CapabilityId}", capabilityId);
            throw;
        }
        finally
        {
            foreach (var id in registeredCallbackIds)
            {
                _callbackOwners.TryRemove(id, out _);
            }
        }
    }

    /// <summary>
    /// Returns the <see cref="JsonRpcCallbackInvoker"/> for the guest-side connection that owns
    /// the given callback id, or <c>null</c> if the id is not currently registered.
    /// Integration hosts use this to route <c>invokeGuestCallback</c> back to the originating guest.
    /// </summary>
    public JsonRpcCallbackInvoker? ResolveCallbackOwner(string callbackId)
        => _callbackOwners.TryGetValue(callbackId, out var invoker) ? invoker : null;

    private List<string> RegisterCallbackOwners(AtsCapabilityInfo? projected, JsonObject? args, JsonRpcCallbackInvoker? ownerInvoker)
    {
        var registered = new List<string>();
        if (projected is null || args is null || ownerInvoker is null)
        {
            return registered;
        }

        foreach (var parameter in projected.Parameters)
        {
            if (!parameter.IsCallback)
            {
                continue;
            }

            if (!args.TryGetPropertyValue(parameter.Name, out var node) || node is null)
            {
                continue;
            }

            if (node is JsonValue value && value.TryGetValue<string>(out var callbackId) && !string.IsNullOrEmpty(callbackId))
            {
                _callbackOwners[callbackId] = ownerInvoker;
                registered.Add(callbackId);
            }
        }

        return registered;
    }

    /// <summary>
    /// Checks if a capability is registered externally.
    /// </summary>
    public bool IsRegistered(string capabilityId) => _capabilities.ContainsKey(capabilityId);

    private static IReadOnlyList<ExternalCapabilityProjection> ReadCapabilities(JsonElement payload)
    {
        if (payload.ValueKind == JsonValueKind.Array)
        {
            return JsonSerializer.Deserialize<List<ExternalCapabilityProjection>>(payload.GetRawText(), s_jsonOptions) ?? [];
        }

        if (payload.ValueKind == JsonValueKind.Object &&
            payload.TryGetProperty("capabilities", out var capabilitiesElement))
        {
            return JsonSerializer.Deserialize<List<ExternalCapabilityProjection>>(capabilitiesElement.GetRawText(), s_jsonOptions) ?? [];
        }

        return [];
    }

    private static AtsCapabilityInfo? TryCreateProjectedCapability(ExternalCapabilityProjection capability)
    {
        if (capability.ReturnType is null)
        {
            return null;
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
        public AtsCapabilityInfo? ProjectedCapability { get; init; }
    }

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
        public bool IsReadOnly { get; set; }
        public ExternalTypeRef? ElementType { get; set; }
        public ExternalTypeRef? KeyType { get; set; }
        public ExternalTypeRef? ValueType { get; set; }
        public List<ExternalTypeRef>? UnionTypes { get; set; }
    }
}
