// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Dashboard.Telemetry;

public static class TelemetryPropertyKeys
{
    // Follow OpenTelemetry's namespace and snake_case naming conventions.
    // https://opentelemetry.io/docs/specs/semconv/general/naming/
    private const string AspireDashboardPropertyPrefix = "aspire.dashboard.";

    // Default properties
    public const string DashboardVersion = AspireDashboardPropertyPrefix + "version";
    public const string DashboardBuildId = AspireDashboardPropertyPrefix + "build_id";
    public const string DeploymentEnvironmentName = "deployment.environment.name";
    public const string OsName = "os.name";
    public const string OsType = "os.type";
    public const string OsVersion = "os.version";
    public const string MacAddressHash = "machine.mac_address_hash";
    public const string DeviceId = "machine.device_id";
    public const string InternalMicrosoft = AspireDashboardPropertyPrefix + "is_microsoft_internal";
    public const string InternalMicrosoftSource = AspireDashboardPropertyPrefix + "microsoft_internal_source";
    public const string InternalMicrosoftAlias = AspireDashboardPropertyPrefix + "microsoft_internal_alias";
    public const string InternalMicrosoftDomain = AspireDashboardPropertyPrefix + "microsoft_internal_domain";

    // Startup properties
    public const string StartupSuccess = AspireDashboardPropertyPrefix + "startup.success";
    public const string StartupLaunchContext = AspireDashboardPropertyPrefix + "startup.launch_context";
    public const string StartupPersistenceMode = AspireDashboardPropertyPrefix + "startup.persistence_mode";
    public const string StartupDurationMilliseconds = AspireDashboardPropertyPrefix + "startup.duration_ms";
    public const string ErrorType = "error.type";

    // IComponentWithTelemetry properties
    public const string DashboardComponentId = AspireDashboardPropertyPrefix + "component.id";
    public const string DashboardComponentType = AspireDashboardPropertyPrefix + "component.type";
    public const string UserAgent = "user_agent.original";

    // ConsoleLogs properties
    public const string ConsoleLogsShowTimestamp = AspireDashboardPropertyPrefix + "console_logs.show_timestamp";
    public const string ConsoleLogsResourceName = AspireDashboardPropertyPrefix + "console_logs.resource.name";

    // Metrics properties
    public const string MetricsResourceIsReplica = AspireDashboardPropertyPrefix + "metrics.resource.is_replica";
    public const string MetricsInstrumentsCount = AspireDashboardPropertyPrefix + "metrics.instruments.count";
    public const string MetricsSelectedDuration = AspireDashboardPropertyPrefix + "metrics.selected_duration";
    public const string MetricsSelectedView = AspireDashboardPropertyPrefix + "metrics.selected_view";

    // Exception properties
    public const string ExceptionType = "exception.type";
    public const string ExceptionMessage = "exception.message";
    public const string ExceptionStackTrace = "exception.stacktrace";
    public const string ExceptionRuntimeVersion = "process.runtime.version";

    // Resources properties
    public const string ResourceType = AspireDashboardPropertyPrefix + "resource.type";
    public const string ResourceView = AspireDashboardPropertyPrefix + "resource.view";

    // Error properties
    public const string ErrorRequestId = AspireDashboardPropertyPrefix + "request.id";

    // Structured logs properties
    public const string StructuredLogsSelectedLogLevel = AspireDashboardPropertyPrefix + "structured_logs.selected_log_level";
    public const string StructuredLogsFilterCount = AspireDashboardPropertyPrefix + "structured_logs.filter_count";

    // Command properties
    public const string CommandName = AspireDashboardPropertyPrefix + "command.name";

    // Terminal dock properties
    public const string TerminalDockTrigger = AspireDashboardPropertyPrefix + "terminal_dock.trigger";
}
