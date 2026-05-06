// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting;

internal static class KnownConfigNames
{
    public const string AspNetCoreUrls = "ASPNETCORE_URLS";
    public const string AllowUnsecuredTransport = "ASPIRE_ALLOW_UNSECURED_TRANSPORT";
    public const string VersionCheckDisabled = "ASPIRE_VERSION_CHECK_DISABLED";
    public const string DashboardOtlpGrpcEndpointUrl = "ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL";
    public const string DashboardOtlpHttpEndpointUrl = "ASPIRE_DASHBOARD_OTLP_HTTP_ENDPOINT_URL";
    public const string DashboardFrontendBrowserToken = "ASPIRE_DASHBOARD_FRONTEND_BROWSERTOKEN";
    public const string DashboardResourceServiceClientApiKey = "ASPIRE_DASHBOARD_RESOURCESERVICE_APIKEY";
    public const string DashboardUnsecuredAllowAnonymous = "ASPIRE_DASHBOARD_UNSECURED_ALLOW_ANONYMOUS";
    public const string DashboardCorsAllowedOrigins = "ASPIRE_DASHBOARD_CORS_ALLOWED_ORIGINS";
    public const string DashboardConfigFilePath = "ASPIRE_DASHBOARD_CONFIG_FILE_PATH";
    public const string DashboardFileConfigDirectory = "ASPIRE_DASHBOARD_FILE_CONFIG_DIRECTORY";
    public const string DashboardAIDisabled = "ASPIRE_DASHBOARD_AI_DISABLED";
    public const string DashboardApiEnabled = "ASPIRE_DASHBOARD_API_ENABLED";
    public const string DashboardApiDisabled = "ASPIRE_DASHBOARD_API_DISABLED";
    public const string DashboardForwardedHeadersEnabled = "ASPIRE_DASHBOARD_FORWARDEDHEADERS_ENABLED";

    public const string ShowDashboardResources = "ASPIRE_SHOW_DASHBOARD_RESOURCES";
    public const string ResourceServiceEndpointUrl = "ASPIRE_RESOURCE_SERVICE_ENDPOINT_URL";

    public const string ContainerRuntime = "ASPIRE_CONTAINER_RUNTIME";
    public const string DependencyCheckTimeout = "ASPIRE_DEPENDENCY_CHECK_TIMEOUT";
    public const string ServiceStartupWatchTimeout = "ASPIRE_SERVICE_STARTUP_WATCH_TIMEOUT";
    public const string WaitForDebugger = "ASPIRE_WAIT_FOR_DEBUGGER";
    public const string WaitForDebuggerTimeout = "ASPIRE_DEBUGGER_TIMEOUT";
    public const string UnixSocketPath = "ASPIRE_BACKCHANNEL_PATH";
    public const string RemoteAppHostToken = "ASPIRE_REMOTE_APPHOST_TOKEN";
    public const string CliProcessId = "ASPIRE_CLI_PID";
    public const string CliProcessStarted = "ASPIRE_CLI_STARTED";
    public const string CliRunDetached = "ASPIRE_CLI_RUN_DETACHED";
    public const string ForceRichConsole = "ASPIRE_FORCE_RICH_CONSOLE";
    public const string TestingDisableHttpClient = "ASPIRE_TESTING_DISABLE_HTTP_CLIENT";
    public const string InteractivityEnabled = "ASPIRE_INTERACTIVITY_ENABLED";
    public const string EnableContainerTunnel = "ASPIRE_ENABLE_CONTAINER_TUNNEL";
    public const string AspireUserSecretsId = "ASPIRE_USER_SECRETS_ID";

    public const string LocaleOverride = "ASPIRE_LOCALE_OVERRIDE";
    public const string DotnetCliUiLanguage = "DOTNET_CLI_UI_LANGUAGE";
    public const string DotnetCliTelemetryOptOut = "DOTNET_CLI_TELEMETRY_OPTOUT";
    public const string DotnetCliWorkloadUpdateNotifyDisable = "DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE";
    public const string MsBuildTerminalLogger = "MSBUILDTERMINALLOGGER";

    public const string ExtensionEndpoint = "ASPIRE_EXTENSION_ENDPOINT";
    public const string ExtensionPromptEnabled = "ASPIRE_EXTENSION_PROMPT_ENABLED";
    public const string ExtensionToken = "ASPIRE_EXTENSION_TOKEN";
    public const string ExtensionCert = "ASPIRE_EXTENSION_CERT";
    public const string ExtensionDebugSessionId = "ASPIRE_EXTENSION_DEBUG_SESSION_ID";
    public const string ExtensionDebugRunMode = "ASPIRE_EXTENSION_DEBUG_RUN_MODE";
    public const string ExtensionCapabilities = "ASPIRE_EXTENSION_CAPABILITIES";

    public const string DeveloperCertificateDefaultTrust = "ASPIRE_DEVELOPER_CERTIFICATE_DEFAULT_TRUST";
    public const string DeveloperCertificateDefaultHttpsTermination = "ASPIRE_DEVELOPER_CERTIFICATE_DEFAULT_HTTPS_TERMINATION";
    public const string DcpDeveloperCertificate = "ASPIRE_DCP_USE_DEVELOPER_CERTIFICATE";

    public const string DebugSessionInfo = "DEBUG_SESSION_INFO";
    public const string DebugSessionRunMode = "DEBUG_SESSION_RUN_MODE";
    public const string DebugSessionPort = "DEBUG_SESSION_PORT";
    public const string DebugSessionToken = "DEBUG_SESSION_TOKEN";
    public const string DebugSessionServerCertificate = "DEBUG_SESSION_SERVER_CERTIFICATE";
    public const string DcpInstanceIdPrefix = "DCP_INSTANCE_ID_PREFIX";

    public static class Legacy
    {
        public const string DashboardOtlpGrpcEndpointUrl = "DOTNET_DASHBOARD_OTLP_ENDPOINT_URL";
        public const string DashboardOtlpHttpEndpointUrl = "DOTNET_DASHBOARD_OTLP_HTTP_ENDPOINT_URL";
        public const string DashboardFrontendBrowserToken = "DOTNET_DASHBOARD_FRONTEND_BROWSERTOKEN";
        public const string DashboardResourceServiceClientApiKey = "DOTNET_DASHBOARD_RESOURCESERVICE_APIKEY";
        public const string DashboardUnsecuredAllowAnonymous = "DOTNET_DASHBOARD_UNSECURED_ALLOW_ANONYMOUS";
        public const string DashboardCorsAllowedOrigins = "DOTNET_DASHBOARD_CORS_ALLOWED_ORIGINS";
        public const string DashboardConfigFilePath = "DOTNET_DASHBOARD_CONFIG_FILE_PATH";
        public const string DashboardFileConfigDirectory = "DOTNET_DASHBOARD_FILE_CONFIG_DIRECTORY";

        public const string ShowDashboardResources = "DOTNET_SHOW_DASHBOARD_RESOURCES";
        public const string ResourceServiceEndpointUrl = "DOTNET_RESOURCE_SERVICE_ENDPOINT_URL";

        public const string ContainerRuntime = "DOTNET_ASPIRE_CONTAINER_RUNTIME";
        public const string DependencyCheckTimeout = "DOTNET_ASPIRE_DEPENDENCY_CHECK_TIMEOUT";
        public const string ServiceStartupWatchTimeout = "DOTNET_ASPIRE_SERVICE_STARTUP_WATCH_TIMEOUT";
    }
}
