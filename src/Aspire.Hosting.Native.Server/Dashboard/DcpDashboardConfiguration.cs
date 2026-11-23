// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Native.Dcp;
using Aspire.Hosting.Native.Server;

namespace Aspire.Hosting.Native.Dashboard;

/// <summary>Projects Dashboard activation settings to its DCP workload.</summary>
internal static class DcpDashboardConfiguration
{
    public static DcpWorkloadSpec Create(string path, string service, Uri resourceService, NativeDashboardCredentials credentials)
    {
        if (!Path.IsPathFullyQualified(path) || !File.Exists(path))
        {
            throw new ArgumentException("The Dashboard path must identify an existing absolute executable or assembly.", nameof(path));
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(credentials.BrowserToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(credentials.DashboardApiKey);
        var assembly = path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
        var bundled = Path.GetFileNameWithoutExtension(path) == "aspire-managed";

        return new DcpWorkloadSpec
        {
            ExecutablePath = assembly ? "dotnet" : path,
            WorkingDirectory = Path.GetDirectoryName(path)!,
            ExecutionType = DcpExecutionType.Process,
            Args = (assembly, bundled) switch
            {
                (true, true) => [path, "dashboard"],
                (true, false) => [path],
                (false, true) => ["dashboard"],
                _ => []
            },
            Env =
            [
                // DCP substitutes the assigned frontend port before process launch.
                new("ASPNETCORE_URLS", "http://127.0.0.1:{{- portForServing \"" + service + "\" -}}"),
                new("ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL", "http://127.0.0.1:0"),
                new("ASPIRE_RESOURCE_SERVICE_ENDPOINT_URL", resourceService.AbsoluteUri),
                new("DASHBOARD__RESOURCESERVICECLIENT__AUTHMODE", "ApiKey"),
                new("DASHBOARD__RESOURCESERVICECLIENT__APIKEY", credentials.ResourceServiceApiKey),
                new("DASHBOARD__FRONTEND__AUTHMODE", "BrowserToken"),
                new("DASHBOARD__FRONTEND__BROWSERTOKEN", credentials.BrowserToken),
                // Frontend API credentials are separate from browser and resource-service credentials.
                new("DASHBOARD__API__AUTHMODE", "ApiKey"),
                new("DASHBOARD__API__PRIMARYAPIKEY", credentials.DashboardApiKey),
                new("DASHBOARD__OTLP__AUTHMODE", "ApiKey"),
                new("DASHBOARD__OTLP__PRIMARYAPIKEY", credentials.OtlpApiKey),
                new("ASPIRE_DASHBOARD_SUPPRESS_BROWSER_TOKEN_IN_OUTPUT", "true")
            ]
        };
    }
}
