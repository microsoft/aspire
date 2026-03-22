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
    public string? McpEndpointUrl { get; set; }
    public string? McpApiKey { get; set; }
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

        options.OtlpGrpcEndpointUrl = configuration.GetString(KnownConfigNames.DashboardOtlpGrpcEndpointUrl, KnownConfigNames.Legacy.DashboardOtlpGrpcEndpointUrl);
        options.OtlpHttpEndpointUrl = configuration.GetString(KnownConfigNames.DashboardOtlpHttpEndpointUrl, KnownConfigNames.Legacy.DashboardOtlpHttpEndpointUrl);
        options.McpEndpointUrl = configuration[KnownConfigNames.DashboardMcpEndpointUrl];
        options.OtlpApiKey = configuration["AppHost:OtlpApiKey"];
        options.McpApiKey = configuration["AppHost:McpApiKey"];
        options.ApiKey = configuration["AppHost:DashboardApiKey"];

        options.AspNetCoreEnvironment = configuration["ASPNETCORE_ENVIRONMENT"] ?? "Production";

        options.TelemetryOptOut = bool.TryParse(configuration["ASPIRE_DASHBOARD_TELEMETRY_OPTOUT"], out var telemetryOptOut)
            ? telemetryOptOut
            : null;
    }
}

internal class ValidateDashboardOptions : IValidateOptions<DashboardOptions>
{
    public ValidateOptionsResult Validate(string? name, DashboardOptions options)
    {
        var builder = new ValidateOptionsResultBuilder();

        if (string.IsNullOrEmpty(options.DashboardUrl))
        {
            builder.AddError($"Failed to configure dashboard resource because {KnownConfigNames.AspNetCoreUrls} environment variable was not set.");
        }

        if (string.IsNullOrEmpty(options.OtlpGrpcEndpointUrl) && string.IsNullOrEmpty(options.OtlpHttpEndpointUrl))
        {
            builder.AddError($"Failed to configure dashboard resource because {KnownConfigNames.DashboardOtlpGrpcEndpointUrl} and {KnownConfigNames.DashboardOtlpHttpEndpointUrl} environment variables are not set. At least one OTLP endpoint must be provided.");
        }

        return builder.Build();
    }
}
