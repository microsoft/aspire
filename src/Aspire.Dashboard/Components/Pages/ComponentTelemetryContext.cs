// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Telemetry;

namespace Aspire.Dashboard.Components.Pages;

public sealed class ComponentTelemetryContextProvider
{
    private readonly DashboardTelemetryService _telemetryService;
    private string? _browserUserAgent;

    public ComponentTelemetryContextProvider(DashboardTelemetryService telemetryService)
    {
        _telemetryService = telemetryService;
    }

    public void SetBrowserUserAgent(string? userAgent)
    {
        _browserUserAgent = userAgent;
    }

    public void Initialize(ComponentTelemetryContext context)
    {
        context.Initialize(_telemetryService, _browserUserAgent);
    }
}

public enum ComponentType
{
    Page,
    Control
}

public sealed class ComponentTelemetryContext : IDisposable
{
    private DashboardTelemetryService? _telemetryService;
    private readonly string _componentId;
    private readonly ComponentType _type;
    private bool _disposed;

    public ComponentTelemetryContext(ComponentType type, string componentId)
    {
        _componentId = componentId;
        _type = type;
    }

    // Internal for testing
    internal Dictionary<string, AspireTelemetryProperty> Properties { get; } = [];

    public void Initialize(DashboardTelemetryService telemetryService, string? browserUserAgent)
    {
        _telemetryService = telemetryService;

        Properties[TelemetryPropertyKeys.DashboardComponentId] = new AspireTelemetryProperty(_componentId);
        Properties[TelemetryPropertyKeys.DashboardComponentType] = new AspireTelemetryProperty(_type.ToString());
        if (browserUserAgent != null)
        {
            Properties[TelemetryPropertyKeys.UserAgent] = new AspireTelemetryProperty(browserUserAgent);
        }

        // Record usage now as a log rather than keeping a span open until component disposal.
        // Spans are only exported when completed, so terminating the dashboard without graceful
        // disposal could otherwise lose this usage data.
        telemetryService.RecordEvent(
            TelemetryEventKeys.ComponentInitialize,
            TelemetryResult.Success,
            properties: CreateInitializeAndDisposeProperties());
    }

    public bool UpdateTelemetryProperties(ReadOnlySpan<ComponentTelemetryProperty> modifiedProperties, ILogger logger)
    {
        // Only send updated properties if they are different from the existing ones.
        var anyChange = false;

        foreach (var (name, value) in modifiedProperties)
        {
            if (value.Value is string s && string.IsNullOrEmpty(s))
            {
                continue;
            }

            if (!Properties.TryGetValue(name, out var existingValue) || !existingValue.Value.Equals(value.Value))
            {
                Properties[name] = value;
                anyChange = true;
            }
        }

        if (anyChange)
        {
            RecordProperties(logger);
        }

        return anyChange;
    }

    private void RecordProperties(ILogger logger)
    {
        if (_telemetryService == null)
        {
            logger.LogWarning("Telemetry service for '{ComponentType}' is not initialized. Cannot post properties.", _componentId);
            return;
        }

        _telemetryService.RecordEvent(
            TelemetryEventKeys.ParametersSet,
            TelemetryResult.Success,
            properties: Properties);
    }

    private Dictionary<string, AspireTelemetryProperty> CreateInitializeAndDisposeProperties()
    {
        return new Dictionary<string, AspireTelemetryProperty>
        {
            // Component properties
            { TelemetryPropertyKeys.DashboardComponentId, new AspireTelemetryProperty(_componentId) },
            { TelemetryPropertyKeys.DashboardComponentType, new AspireTelemetryProperty(_type.ToString()) },
        };
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _telemetryService?.RecordEvent(
                TelemetryEventKeys.ComponentDispose,
                TelemetryResult.Success,
                properties: CreateInitializeAndDisposeProperties());

            _disposed = true;
        }
    }
}
