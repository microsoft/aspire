// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting;

internal static class DashboardConfigNames
{
    public static readonly ConfigName DashboardFrontendUrlName = new(KnownConfigNames.AspNetCoreUrls);

    public static readonly ConfigName DashboardOtlpGrpcUrlName = new(KnownConfigNames.DashboardOtlpGrpcEndpointUrl);
    public static readonly ConfigName DashboardOtlpHttpUrlName = new(KnownConfigNames.DashboardOtlpHttpEndpointUrl);
    public static readonly ConfigName DashboardMcpUrlName = new(KnownConfigNames.DashboardMcpEndpointUrl);
    public static readonly ConfigName DashboardUnsecuredAllowAnonymousName = new(KnownConfigNames.DashboardUnsecuredAllowAnonymous);
    public static readonly ConfigName DashboardConfigFilePathName = new(KnownConfigNames.DashboardConfigFilePath);
    public static readonly ConfigName DashboardFileConfigDirectoryName = new(KnownConfigNames.DashboardFileConfigDirectory);
    public static readonly ConfigName DashboardAIDisabledName = new(KnownConfigNames.DashboardAIDisabled);
    public static readonly ConfigName ResourceServiceUrlName = new(KnownConfigNames.ResourceServiceEndpointUrl);
    public static readonly ConfigName ForwardedHeaders = new(KnownConfigNames.DashboardForwardedHeadersEnabled);

    public static readonly ConfigName DashboardOtlpAuthModeName = new("Dashboard:Otlp:AuthMode", "DASHBOARD__OTLP__AUTHMODE");
    public static readonly ConfigName DashboardOtlpPrimaryApiKeyName = new("Dashboard:Otlp:PrimaryApiKey", "DASHBOARD__OTLP__PRIMARYAPIKEY");
    public static readonly ConfigName DashboardOtlpSecondaryApiKeyName = new("Dashboard:Otlp:SecondaryApiKey", "DASHBOARD__OTLP__SECONDARYAPIKEY");
    public static readonly ConfigName DashboardMcpPublicUrlName = new("Dashboard:Mcp:PublicUrl", "DASHBOARD__MCP__PUBLICURL");
    public static readonly ConfigName DashboardMcpAuthModeName = new("Dashboard:Mcp:AuthMode", "DASHBOARD__MCP__AUTHMODE");
    public static readonly ConfigName DashboardMcpPrimaryApiKeyName = new("Dashboard:Mcp:PrimaryApiKey", "DASHBOARD__MCP__PRIMARYAPIKEY");
    public static readonly ConfigName DashboardMcpDisableName = new("Dashboard:Mcp:Disabled", "DASHBOARD__MCP__DISABLED");
    public static readonly ConfigName DashboardMcpUseCliMcpName = new("Dashboard:Mcp:UseCliMcp", "DASHBOARD__MCP__USECLIMCP");
    public static readonly ConfigName DashboardOtlpSuppressUnsecuredTelemetryMessageName = new("Dashboard:Otlp:SuppressUnsecuredTelemetryMessage", "DASHBOARD__OTLP__SUPPRESSUNSECUREDTELEMETRYMESSAGE");
    public static readonly ConfigName DashboardOtlpCorsAllowedOriginsKeyName = new("Dashboard:Otlp:Cors:AllowedOrigins", "DASHBOARD__OTLP__CORS__ALLOWEDORIGINS");
    public static readonly ConfigName DashboardOtlpCorsAllowedHeadersKeyName = new("Dashboard:Otlp:Cors:AllowedHeaders", "DASHBOARD__OTLP__CORS__ALLOWEDHEADERS");
    public static readonly ConfigName DashboardOtlpAllowedCertificatesName = new("Dashboard:Otlp:AllowedCertificates", "DASHBOARD__OTLP__ALLOWEDCERTIFICATES");
    public static readonly ConfigName DashboardFrontendAuthModeName = new("Dashboard:Frontend:AuthMode", "DASHBOARD__FRONTEND__AUTHMODE");
    public static readonly ConfigName DashboardFrontendBrowserTokenName = new("Dashboard:Frontend:BrowserToken", "DASHBOARD__FRONTEND__BROWSERTOKEN");
    public static readonly ConfigName DashboardFrontendMaxConsoleLogCountName = new("Dashboard:Frontend:MaxConsoleLogCount", "DASHBOARD__FRONTEND__MAXCONSOLELOGCOUNT");
    public static readonly ConfigName DashboardFrontendPublicUrlName = new("Dashboard:Frontend:PublicUrl", "DASHBOARD__FRONTEND__PUBLICURL");
    public static readonly ConfigName ResourceServiceClientAuthModeName = new("Dashboard:ResourceServiceClient:AuthMode", "DASHBOARD__RESOURCESERVICECLIENT__AUTHMODE");
    public static readonly ConfigName ResourceServiceClientCertificateSourceName = new("Dashboard:ResourceServiceClient:ClientCertificate:Source", "DASHBOARD__RESOURCESERVICECLIENT__CLIENTCERTIFICATE__SOURCE");
    public static readonly ConfigName ResourceServiceClientCertificateFilePathName = new("Dashboard:ResourceServiceClient:ClientCertificate:FilePath", "DASHBOARD__RESOURCESERVICECLIENT__CLIENTCERTIFICATE__FILEPATH");
    public static readonly ConfigName ResourceServiceClientCertificateSubjectName = new("Dashboard:ResourceServiceClient:ClientCertificate:Subject", "DASHBOARD__RESOURCESERVICECLIENT__CLIENTCERTIFICATE__SUBJECT");
    public static readonly ConfigName ResourceServiceClientApiKeyName = new("Dashboard:ResourceServiceClient:ApiKey", "DASHBOARD__RESOURCESERVICECLIENT__APIKEY");
    public static readonly ConfigName DebugSessionPortName = new("Dashboard:DebugSession:Port", "DASHBOARD__DEBUGSESSION__PORT");
    public static readonly ConfigName DebugSessionServerCertificateName = new("Dashboard:DebugSession:ServerCertificate", "DASHBOARD__DEBUGSESSION__SERVERCERTIFICATE");
    public static readonly ConfigName DebugSessionTokenName = new("Dashboard:DebugSession:Token", "DASHBOARD__DEBUGSESSION__TOKEN");
    public static readonly ConfigName DebugSessionTelemetryOptOutName = new("Dashboard:DebugSession:TelemetryOptOut", "DASHBOARD__DEBUGSESSION__TELEMETRYOPTOUT");
    public static readonly ConfigName UIDisableResourceGraphName = new("Dashboard:UI:DisableResourceGraph", "DASHBOARD__UI__DISABLERESOURCEGRAPH");

    public static class Legacy
    {
        public static readonly ConfigName DashboardOtlpGrpcUrlName = new(KnownConfigNames.Legacy.DashboardOtlpGrpcEndpointUrl);
        public static readonly ConfigName DashboardOtlpHttpUrlName = new(KnownConfigNames.Legacy.DashboardOtlpHttpEndpointUrl);
        public static readonly ConfigName DashboardUnsecuredAllowAnonymousName = new(KnownConfigNames.Legacy.DashboardUnsecuredAllowAnonymous);
        public static readonly ConfigName DashboardConfigFilePathName = new(KnownConfigNames.Legacy.DashboardConfigFilePath);
        public static readonly ConfigName DashboardFileConfigDirectoryName = new(KnownConfigNames.Legacy.DashboardFileConfigDirectory);
        public static readonly ConfigName ResourceServiceUrlName = new(KnownConfigNames.Legacy.ResourceServiceEndpointUrl);
        public static readonly ConfigName DashboardOtlpSuppressUnsecuredTelemetryMessageName = new("Dashboard:Otlp:SuppressUnsecuredTelemetryMessage", "DASHBOARD__OTLP__SUPPRESSUNSECUREDTELEMETRYMESSAGE");
    }
}

internal readonly struct ConfigName(string configKey, string? envVarName = null)
{
    public string ConfigKey { get; } = configKey;
    public string EnvVarName { get; } = envVarName ?? configKey;
}
