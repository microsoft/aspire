// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Dcp;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace Aspire.Hosting.Dashboard;

internal class DashboardOptions
{
    public string? DashboardPath { get; set; }
    public string? DashboardUrl { get; set; }
    public string? DashboardToken { get; set; }
    public string? OtlpGrpcEndpointUrl { get; set; }
    public string? OtlpHttpEndpointUrl { get; set; }
    public string? OtlpApiKey { get; set; }
    public string? ApiKey { get; set; }
    public string AspNetCoreEnvironment { get; set; } = "Production";
    public bool? TelemetryOptOut { get; set; }

    /// <summary>
    /// Suppresses the login URL, and only the login URL, from the AppHost's dashboard startup summary.
    /// </summary>
    /// <remarks>
    /// Set by <c>Aspire.Hosting.Testing</c> when it runs a dashboard for a test application. That dashboard's
    /// browser token is generated per application and handed to the test through
    /// <c>GetDashboardUrlAsync</c>, so writing it to the AppHost logger only publishes a live credential
    /// into test and CI output. The dashboard and OTLP endpoint lines are still written.
    /// </remarks>
    public bool SuppressLoginUrlInStartupSummary { get; set; }
}

internal class ConfigureDefaultDashboardOptions(IConfiguration configuration, IOptions<DcpOptions> dcpOptions) : IConfigureOptions<DashboardOptions>
{
    public void Configure(DashboardOptions options)
    {
        options.DashboardPath = dcpOptions.Value.DashboardPath;
        options.DashboardUrl = configuration[KnownAspNetCoreConfigNames.Urls];
        options.DashboardToken = configuration["AppHost:BrowserToken"];

        options.OtlpGrpcEndpointUrl = NormalizeUrl(configuration.GetString(KnownConfigNames.DashboardOtlpGrpcEndpointUrl, KnownConfigNames.Legacy.DashboardOtlpGrpcEndpointUrl));
        options.OtlpHttpEndpointUrl = NormalizeUrl(configuration.GetString(KnownConfigNames.DashboardOtlpHttpEndpointUrl, KnownConfigNames.Legacy.DashboardOtlpHttpEndpointUrl));

        options.OtlpApiKey = configuration["AppHost:OtlpApiKey"];
        options.ApiKey = configuration["AppHost:DashboardApiKey"];

        options.AspNetCoreEnvironment = configuration[KnownAspNetCoreConfigNames.Environment] ?? "Production";

        options.SuppressLoginUrlInStartupSummary = bool.TryParse(configuration["AppHost:SuppressDashboardLoginUrlInStartupSummary"], out var suppressLoginUrl) && suppressLoginUrl;

        options.TelemetryOptOut = bool.TryParse(configuration["ASPIRE_DASHBOARD_TELEMETRY_OPTOUT"], out var telemetryOptOut)
            ? telemetryOptOut
            : null;
    }

    private static string? NormalizeUrl(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
