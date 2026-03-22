// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace Aspire.Dashboard.Telemetry;

public sealed class DashboardTelemetryService
{
    private readonly SemaphoreSlim _lock = new SemaphoreSlim(1);
    private readonly ILogger<DashboardTelemetryService> _logger;
    private readonly IDashboardTelemetrySender _telemetrySender;

    // Internal for testing.
    internal readonly Dictionary<string, AspireTelemetryProperty> _defaultProperties;

    public DashboardTelemetryService(
        ILogger<DashboardTelemetryService> logger,
        IDashboardTelemetrySender telemetrySender)
    {
        _logger = logger;
        _telemetrySender = telemetrySender;

        _defaultProperties = new Dictionary<string, AspireTelemetryProperty>
        {
            // This is consistent with CLI version data.
            { TelemetryPropertyKeys.DashboardVersion, new AspireTelemetryProperty(typeof(DashboardWebApplication).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? string.Empty) },
            { TelemetryPropertyKeys.DashboardBuildId, new AspireTelemetryProperty(typeof(DashboardWebApplication).Assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version ?? string.Empty) },
        };
    }

    /// <summary>
    /// Whether the telemetry service has been initialized. This will be true if <see cref="InitializeAsync"/> has completed.
    /// </summary>
    public bool IsTelemetryInitialized => _telemetrySender.State != TelemetrySessionState.Uninitialized;

    /// <summary>
    /// Whether telemetry is enabled in the current environment. This will be false if:
    /// <list type="bullet">
    /// <item>The user is not running the Aspire dashboard through a supported IDE version</item>
    /// <item>The dashboard resource contains a telemetry opt-out config entry</item>
    /// <item>The IDE instance has opted out of telemetry</item>
    /// </list>
    /// </summary>
    public bool IsTelemetryEnabled => _telemetrySender.State == TelemetrySessionState.Enabled;

    /// <summary>
    /// Call before using any telemetry methods. This will initialize the telemetry service and ensure that <see cref="DashboardTelemetryService.IsTelemetryEnabled"/> is set
    /// by making a request to the debug session, if one exists.
    /// </summary>
    public async Task InitializeAsync()
    {
        if (IsTelemetryInitialized)
        {
            return;
        }

        // Async lock to ensure that telemetry is only initialized once.
        await _lock.WaitAsync().ConfigureAwait(false);

        try
        {
            if (IsTelemetryInitialized)
            {
                return;
            }

            _logger.LogDebug("Initializing telemetry service.");
            await _telemetrySender.TryStartTelemetrySessionAsync().ConfigureAwait(false);
            _logger.LogDebug("Initialized telemetry service. Telemetry sender state: {TelemetrySenderState}", _telemetrySender.State);

            // Post session property values after initialization, if telemetry has been enabled.
            if (IsTelemetryEnabled)
            {
                foreach (var (key, value) in _defaultProperties)
                {
                    PostProperty(key, value);
                }
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    private bool SkipQueuingRequests()
    {
        // Don't queue requests if we know the sender isn't enabled. This is a performance optimization.
        // Queue requests if enabled or not yet initialized.
        return !IsTelemetryEnabled;
    }

    /// <summary>
    /// Begin a long-running user operation. Prefer this over <see cref="PostOperation"/>. If an explicit user task caused this operation to start,
    /// use <see cref="StartUserTask"/> instead. Duration will be automatically calculated and the end event posted after <see cref="DashboardTelemetryService.EndOperation"/> is called.
    /// </summary>
    public OperationContext StartOperation(string eventName, Dictionary<string, AspireTelemetryProperty> startEventProperties, TelemetrySeverity severity = TelemetrySeverity.Normal, bool isOptOutFriendly = false, bool postStartEvent = true, IEnumerable<OperationContextProperty>? correlations = null)
    {
        if (SkipQueuingRequests())
        {
            return OperationContext.Empty;
        }

        var context = OperationContext.Create(propertyCount: 2, name: GetCompositeEventName(eventName, TelemetryEndpoints.TelemetryStartOperation));
        _telemetrySender.QueueRequest(context, async (client, propertyGetter) =>
        {
            var scopeSettings = new AspireTelemetryScopeSettings(
                IncludeDefaultProperties(startEventProperties),
                severity,
                isOptOutFriendly,
                correlations?.Select(propertyGetter).Cast<TelemetryEventCorrelation>().ToArray(),
                postStartEvent);

            var response = await PostRequestAsync<StartOperationRequest, StartOperationResponse>(client, TelemetryEndpoints.TelemetryStartOperation, new StartOperationRequest(eventName, scopeSettings)).ConfigureAwait(false);
            context.Properties[0].SetValue(response.OperationId);
            context.Properties[1].SetValue(response.Correlation);
        });

        return context;
    }

    /// <summary>
    /// Ends a long-running operation. This will post the end event and calculate the duration.
    /// </summary>
    public void EndOperation(OperationContextProperty operationId, TelemetryResult result, string? errorMessage = null)
    {
        if (SkipQueuingRequests())
        {
            return;
        }

        var context = OperationContext.Create(propertyCount: 0, name: TelemetryEndpoints.TelemetryEndOperation);
        _telemetrySender.QueueRequest(context, async (client, propertyGetter) =>
        {
            await client.PostAsJsonAsync(TelemetryEndpoints.TelemetryEndOperation, new EndOperationRequest(Id: (string)propertyGetter(operationId), Result: result, ErrorMessage: errorMessage)).ConfigureAwait(false);
        });
    }

    /// <summary>
    /// Begin a long-running user task. This will post the start event and calculate the duration.
    /// Duration will be automatically calculated and the end event posted after <see cref="EndUserTask"/> is called.
    /// </summary>
    public OperationContext StartUserTask(string eventName, Dictionary<string, AspireTelemetryProperty> startEventProperties, TelemetrySeverity severity = TelemetrySeverity.Normal, bool isOptOutFriendly = false, bool postStartEvent = true, IEnumerable<OperationContextProperty>? correlations = null)
    {
        if (SkipQueuingRequests())
        {
            return OperationContext.Empty;
        }

        var context = OperationContext.Create(propertyCount: 2, name: GetCompositeEventName(eventName, TelemetryEndpoints.TelemetryStartUserTask));
        _telemetrySender.QueueRequest(context, async (client, propertyGetter) =>
        {
            var scopeSettings = new AspireTelemetryScopeSettings(
                IncludeDefaultProperties(startEventProperties),
                severity,
                isOptOutFriendly,
                correlations?.Select(propertyGetter).Cast<TelemetryEventCorrelation>().ToArray(),
                postStartEvent);

            var response = await PostRequestAsync<StartOperationRequest, StartOperationResponse>(client, TelemetryEndpoints.TelemetryStartUserTask, new StartOperationRequest(eventName, scopeSettings)).ConfigureAwait(false);
            context.Properties[0].SetValue(response.OperationId);
            context.Properties[1].SetValue(response.Correlation);
        });

        return context;
    }

    /// <summary>
    /// Ends a long-running user task. This will post the end event and calculate the duration.
    /// </summary>
    public void EndUserTask(OperationContextProperty operationId, TelemetryResult result, string? errorMessage = null)
    {
        if (SkipQueuingRequests())
        {
            return;
        }

        var context = OperationContext.Create(propertyCount: 0, name: TelemetryEndpoints.TelemetryEndUserTask);
        _telemetrySender.QueueRequest(context, async (client, propertyGetter) =>
        {
            await client.PostAsJsonAsync(TelemetryEndpoints.TelemetryEndUserTask, new EndOperationRequest(Id: (string)propertyGetter(operationId), Result: result, ErrorMessage: errorMessage)).ConfigureAwait(false);
        });
    }

    /// <summary>
    /// Posts a short-lived operation. If duration needs to be calculated, use <see cref="DashboardTelemetryService.StartOperation"/> and <see cref="DashboardTelemetryService.EndOperation"/> instead.
    /// If an explicit user task caused this operation to start, use <see cref="DashboardTelemetryService.PostUserTask"/> instead.
    /// <returns>Guid corresponding to the (as-of-yet-uncompleted) correlation returned from this request.</returns>
    /// </summary>
    public OperationContext PostOperation(string eventName, TelemetryResult result, string? resultSummary = null, Dictionary<string, AspireTelemetryProperty>? properties = null, IEnumerable<OperationContextProperty>? correlatedWith = null)
    {
        if (SkipQueuingRequests())
        {
            return OperationContext.Empty;
        }

        var context = OperationContext.Create(propertyCount: 1, name: GetCompositeEventName(eventName, TelemetryEndpoints.TelemetryPostOperation));
        _telemetrySender.QueueRequest(context, async (client, propertyGetter) =>
        {
            var request = new PostOperationRequest(
                eventName,
                result,
                resultSummary,
                IncludeDefaultProperties(properties),
                correlatedWith?.Select(propertyGetter).Cast<TelemetryEventCorrelation>().ToArray());

            var response = await PostRequestAsync<PostOperationRequest, TelemetryEventCorrelation>(client, TelemetryEndpoints.TelemetryPostOperation, request).ConfigureAwait(false);
            context.Properties[0].SetValue(response);
        });

        return context;
    }

    /// <summary>
    /// Posts a short-lived user task. If duration needs to be calculated, use <see cref="DashboardTelemetryService.StartUserTask"/> and <see cref="DashboardTelemetryService.EndUserTask"/> instead.
    /// <returns>Guid corresponding to the (as-of-yet-uncompleted) correlation returned from this request.</returns>
    /// </summary>
    public OperationContext PostUserTask(string eventName, TelemetryResult result, string? resultSummary = null, Dictionary<string, AspireTelemetryProperty>? properties = null, IEnumerable<OperationContextProperty>? correlatedWith = null)
    {
        if (SkipQueuingRequests())
        {
            return OperationContext.Empty;
        }

        var context = OperationContext.Create(propertyCount: 1, name: GetCompositeEventName(eventName, TelemetryEndpoints.TelemetryPostUserTask));
        _telemetrySender.QueueRequest(context, async (client, propertyGetter) =>
        {
            var request = new PostOperationRequest(
                eventName,
                result,
                resultSummary,
                IncludeDefaultProperties(properties),
                correlatedWith?.Select(propertyGetter).Cast<TelemetryEventCorrelation>().ToArray());

            var response = await PostRequestAsync<PostOperationRequest, TelemetryEventCorrelation>(client, TelemetryEndpoints.TelemetryPostUserTask, request).ConfigureAwait(false);
            context.Properties[0].SetValue(response);
        });

        return context;
    }

    /// <summary>
    /// Posts a fault event.
    /// <returns>Guid corresponding to the (as-of-yet-uncompleted) correlation returned from this request.</returns>
    /// </summary>
    public OperationContext PostFault(string eventName, string description, FaultSeverity severity, Dictionary<string, AspireTelemetryProperty>? properties = null, IEnumerable<OperationContextProperty>? correlatedWith = null)
    {
        if (SkipQueuingRequests())
        {
            return OperationContext.Empty;
        }

        var context = OperationContext.Create(propertyCount: 1, name: GetCompositeEventName(eventName, TelemetryEndpoints.TelemetryPostFault));
        _telemetrySender.QueueRequest(context, async (client, propertyGetter) =>
        {
            var request = new PostFaultRequest(
                eventName,
                description,
                severity,
                IncludeDefaultProperties(properties),
                correlatedWith?.Select(propertyGetter).Cast<TelemetryEventCorrelation>().ToArray());

            var response = await PostRequestAsync<PostFaultRequest, TelemetryEventCorrelation>(client, TelemetryEndpoints.TelemetryPostFault, request).ConfigureAwait(false);
            context.Properties[0].SetValue(response);
        });

        return context;
    }

    /// <summary>
    /// Posts an asset event. This is used to track events that are related to a specific asset, whose correlations can be sent along with other events.
    /// Currently not used.
    /// <returns>Guid corresponding to the (as-of-yet-uncompleted) correlation returned from this request.</returns>
    /// </summary>
    public OperationContext PostAsset(string eventName, string assetId, int assetEventVersion, Dictionary<string, AspireTelemetryProperty>? additionalProperties = null, IEnumerable<OperationContextProperty>? correlatedWith = null)
    {
        if (SkipQueuingRequests())
        {
            return OperationContext.Empty;
        }

        var context = OperationContext.Create(propertyCount: 1, name: GetCompositeEventName(eventName, TelemetryEndpoints.TelemetryPostAsset));
        _telemetrySender.QueueRequest(context, async (client, propertyGetter) =>
        {
            var request = new PostAssetRequest(
                eventName,
                assetId,
                assetEventVersion,
                IncludeDefaultProperties(additionalProperties),
                correlatedWith?.Select(propertyGetter).Cast<TelemetryEventCorrelation>().ToArray());

            var response = await PostRequestAsync<PostAssetRequest, TelemetryEventCorrelation>(client, TelemetryEndpoints.TelemetryPostAsset, request).ConfigureAwait(false);
            context.Properties[0].SetValue(response);
        });

        return context;
    }

    /// <summary>
    /// Post a session property.
    /// </summary>
    public void PostProperty(string propertyName, AspireTelemetryProperty propertyValue)
    {
        if (SkipQueuingRequests())
        {
            return;
        }

        var context = OperationContext.Create(propertyCount: 0, name: TelemetryEndpoints.TelemetryPostProperty);
        _telemetrySender.QueueRequest(context, async (client, _) =>
        {
            var request = new PostPropertyRequest(propertyName, propertyValue);
            await client.PostAsJsonAsync(TelemetryEndpoints.TelemetryPostProperty, request).ConfigureAwait(false);
        });
    }

    /// <summary>
    /// Post a session recurring property.
    /// </summary>
    public void PostRecurringProperty(string propertyName, AspireTelemetryProperty propertyValue)
    {
        if (SkipQueuingRequests())
        {
            return;
        }

        var context = OperationContext.Create(propertyCount: 0, name: TelemetryEndpoints.TelemetryPostRecurringProperty);
        _telemetrySender.QueueRequest(context, async (client, _) =>
        {
            var request = new PostPropertyRequest(propertyName, propertyValue);
            await client.PostAsJsonAsync(TelemetryEndpoints.TelemetryPostRecurringProperty, request).ConfigureAwait(false);
        });
    }

    /// <summary>
    /// Currently not used.
    /// </summary>
    public void PostCommandLineFlags(List<string> flagPrefixes, Dictionary<string, AspireTelemetryProperty> additionalProperties)
    {
        if (SkipQueuingRequests())
        {
            return;
        }

        var context = OperationContext.Create(propertyCount: 0, name: TelemetryEndpoints.TelemetryPostCommandLineFlags);
        _telemetrySender.QueueRequest(context, async (client, _) =>
        {
            var request = new PostCommandLineFlagsRequest(flagPrefixes, additionalProperties);
            await client.PostAsJsonAsync(TelemetryEndpoints.TelemetryPostCommandLineFlags, request).ConfigureAwait(false);
        });
    }

    private static async Task<TResponse> PostRequestAsync<TRequest, TResponse>(HttpClient client, string endpoint, TRequest request)
    {
        var httpResponseMessage = await client.PostAsJsonAsync(endpoint, request).ConfigureAwait(false);
        httpResponseMessage.EnsureSuccessStatusCode();
        var response = await httpResponseMessage.Content.ReadFromJsonAsync<TResponse>().ConfigureAwait(false);
        if (response is null)
        {
            throw new InvalidOperationException("Response was null.");
        }
        return response;
    }

    private static string GetCompositeEventName(string eventName, string endpoint)
    {
        return $"{endpoint} - ${eventName}";
    }

    [return: NotNullIfNotNull(nameof(properties))]
    private Dictionary<string, AspireTelemetryProperty>? IncludeDefaultProperties(Dictionary<string, AspireTelemetryProperty>? properties)
    {
        if (properties != null)
        {
            foreach (var (key, value) in _defaultProperties)
            {
                properties[key] = value;
            }
        }

        return properties;
    }
}

public static class TelemetryEndpoints
{
    public const string TelemetryEnabled = "/telemetry/enabled";
    public const string TelemetryStart = "/telemetry/start";
    public const string TelemetryStartOperation = "/telemetry/startOperation";
    public const string TelemetryEndOperation = "/telemetry/endOperation";
    public const string TelemetryStartUserTask = "/telemetry/startUserTask";
    public const string TelemetryEndUserTask = "/telemetry/endUserTask";
    public const string TelemetryPostOperation = "/telemetry/operation";
    public const string TelemetryPostUserTask = "/telemetry/userTask";
    public const string TelemetryPostFault = "/telemetry/fault";
    public const string TelemetryPostAsset = "/telemetry/asset";
    public const string TelemetryPostProperty = "/telemetry/property";
    public const string TelemetryPostRecurringProperty = "/telemetry/recurringProperty";
    public const string TelemetryPostCommandLineFlags = "/telemetry/commandLineFlags";
}
