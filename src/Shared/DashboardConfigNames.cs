// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting;

internal static class DashboardConfigNames
{
    public static readonly ConfigName DashboardFrontendUrlName = new(KnownAspNetCoreConfigNames.Urls);

    public static readonly ConfigName DashboardOtlpGrpcUrlName = new(KnownConfigNames.DashboardOtlpGrpcEndpointUrl);
    public static readonly ConfigName DashboardOtlpHttpUrlName = new(KnownConfigNames.DashboardOtlpHttpEndpointUrl);
    public static readonly ConfigName DashboardUnsecuredAllowAnonymousName = new(KnownConfigNames.DashboardUnsecuredAllowAnonymous);
    public static readonly ConfigName DashboardConfigFilePathName = new(KnownConfigNames.DashboardConfigFilePath);
    public static readonly ConfigName DashboardFileConfigDirectoryName = new(KnownConfigNames.DashboardFileConfigDirectory);
    public static readonly ConfigName DashboardAspireApiEnabledName = new(KnownConfigNames.DashboardApiEnabled);
    public static readonly ConfigName DashboardAspireApiDisabledName = new(KnownConfigNames.DashboardApiDisabled);
    public static readonly ConfigName ResourceServiceUrlName = new(KnownConfigNames.ResourceServiceEndpointUrl);
    public static readonly ConfigName ForwardedHeaders = new(KnownConfigNames.DashboardForwardedHeadersEnabled);
    public static readonly ConfigName DashboardApplicationName = new("Dashboard:ApplicationName", "ASPIRE_DASHBOARD_APPLICATION_NAME");
    public static readonly ConfigName DashboardDataDirectoryName = new("Dashboard:Data:Directory", "ASPIRE_DASHBOARD_DATA_DIRECTORY");
    public static readonly ConfigName DashboardPersistenceModeName = new("Dashboard:Data:PersistenceMode", "ASPIRE_DASHBOARD_PERSISTENCE_MODE");
    public static readonly ConfigName DashboardRunIdName = new("Dashboard:Data:RunId", "ASPIRE_DASHBOARD_RUN_ID");

    public static readonly ConfigName DashboardOtlpAuthModeName = new("Dashboard:Otlp:AuthMode", "DASHBOARD__OTLP__AUTHMODE");
    public static readonly ConfigName DashboardOtlpPrimaryApiKeyName = new("Dashboard:Otlp:PrimaryApiKey", "DASHBOARD__OTLP__PRIMARYAPIKEY");
    public static readonly ConfigName DashboardOtlpSecondaryApiKeyName = new("Dashboard:Otlp:SecondaryApiKey", "DASHBOARD__OTLP__SECONDARYAPIKEY");

    // Dashboard API Authentication - used for Telemetry API
    public static readonly ConfigName DashboardApiEnabledName = new("Dashboard:Api:Enabled", "DASHBOARD__API__ENABLED");
    public static readonly ConfigName DashboardApiDisabledName = new("Dashboard:Api:Disabled", "DASHBOARD__API__DISABLED");
    public static readonly ConfigName DashboardApiAuthModeName = new("Dashboard:Api:AuthMode", "DASHBOARD__API__AUTHMODE");
    public static readonly ConfigName DashboardApiPrimaryApiKeyName = new("Dashboard:Api:PrimaryApiKey", "DASHBOARD__API__PRIMARYAPIKEY");
    public static readonly ConfigName DashboardApiSecondaryApiKeyName = new("Dashboard:Api:SecondaryApiKey", "DASHBOARD__API__SECONDARYAPIKEY");

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
    public static readonly ConfigName DebugSessionDcpInstanceIdName = new("Dashboard:DebugSession:DcpInstanceId", "DASHBOARD__DEBUGSESSION__DCPINSTANCEID");
    public static readonly ConfigName DebugSessionTelemetryOptOutName = new("Dashboard:DebugSession:TelemetryOptOut", "DASHBOARD__DEBUGSESSION__TELEMETRYOPTOUT");
    public static readonly ConfigName UIDisableResourceGraphName = new("Dashboard:UI:DisableResourceGraph", "DASHBOARD__UI__DISABLERESOURCEGRAPH");
    public static readonly ConfigName UIDisableImportName = new("Dashboard:UI:DisableImport", "DASHBOARD__UI__DISABLEIMPORT");
    public static readonly ConfigName UIDisableAgentHelpName = new("Dashboard:UI:DisableAgentHelp", "DASHBOARD__UI__DISABLEAGENTHELP");

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

internal static class DashboardRunId
{
    public const int MaxLength = 64;

    public static string Create(DateTimeOffset startedAt) => $"{startedAt:yyyyMMddTHHmmssfffZ}";

    public static bool IsValid(string? value) => TryValidate(value, out _);

    public static bool TryValidate(string? value, out string? errorMessage)
    {
        if (string.IsNullOrEmpty(value))
        {
            errorMessage = "it must contain at least one character";
            return false;
        }

        if (value.Length > MaxLength)
        {
            errorMessage = $"it must not exceed {MaxLength} characters";
            return false;
        }

        if (!IsAsciiLetterOrDigit(value[0]) || !IsAsciiLetterOrDigit(value[^1]))
        {
            errorMessage = "it must start and end with an ASCII letter or number";
            return false;
        }

        foreach (var character in value)
        {
            if (!IsAsciiLetterOrDigit(character) && character is not '-' and not '_' and not '.')
            {
                errorMessage = "it may contain only ASCII letters, numbers, periods, underscores, and hyphens";
                return false;
            }
        }

        var baseName = value.Split('.')[0];
        if (IsWindowsReservedName(baseName))
        {
            errorMessage = $"'{baseName}' is a reserved file name";
            return false;
        }

        errorMessage = null;
        return true;
    }

    private static bool IsAsciiLetterOrDigit(char value) =>
        value is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9');

    private static bool IsWindowsReservedName(string value)
    {
        if (value.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("NUL", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return value.Length == 4 &&
            (value.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || value.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
            value[3] is >= '1' and <= '9';
    }
}

internal readonly struct ConfigName(string configKey, string? envVarName = null)
{
    public string ConfigKey { get; } = configKey;
    public string EnvVarName { get; } = envVarName ?? configKey;
}
