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
}

internal class ConfigureDefaultDashboardOptions(IConfiguration configuration, IOptions<DcpOptions> dcpOptions) : IConfigureOptions<DashboardOptions>
{
    public void Configure(DashboardOptions options)
    {
        options.DashboardPath = dcpOptions.Value.DashboardPath;
        options.DashboardUrl = configuration[KnownConfigNames.AspNetCoreUrls];
        options.DashboardToken = configuration["AppHost:BrowserToken"];

        options.OtlpGrpcEndpointUrl = NormalizeUrl(configuration.GetString(KnownConfigNames.DashboardOtlpGrpcEndpointUrl, KnownConfigNames.Legacy.DashboardOtlpGrpcEndpointUrl));
        options.OtlpHttpEndpointUrl = NormalizeUrl(configuration.GetString(KnownConfigNames.DashboardOtlpHttpEndpointUrl, KnownConfigNames.Legacy.DashboardOtlpHttpEndpointUrl));

        options.OtlpApiKey = configuration["AppHost:OtlpApiKey"];
        options.ApiKey = configuration["AppHost:DashboardApiKey"];

        options.AspNetCoreEnvironment = configuration["ASPNETCORE_ENVIRONMENT"] ?? "Production";

        options.TelemetryOptOut = bool.TryParse(configuration["ASPIRE_DASHBOARD_TELEMETRY_OPTOUT"], out var telemetryOptOut)
            ? telemetryOptOut
            : null;
    }

    private static string? NormalizeUrl(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
