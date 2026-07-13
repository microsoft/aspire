// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.InteropServices;

namespace Aspire.Dashboard.Backend;

internal static class DashboardBackendApplication
{
    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configureBuilder = null)
    {
        var builder = WebApplication.CreateSlimBuilder(args);
        configureBuilder?.Invoke(builder);

        var app = builder.Build();

        app.MapGet(DashboardApiContract.DiscoveryPath, () =>
        {
            var discovery = new DashboardApiDiscovery(
                DashboardApiContract.Product,
                [
                    new DashboardApiVersion(
                        DashboardApiContract.CurrentVersion,
                        DashboardApiContract.VersionOneBasePath,
                        [DashboardApiContract.ConfigurationCapability])
                ]);

            return Results.Json(
                discovery,
                DashboardBackendJsonSerializerContext.Default.DashboardApiDiscovery);
        });

        app.MapGet($"{DashboardApiContract.VersionOneBasePath}/config", () =>
        {
            var configuration = new DashboardConfiguration(
                builder.Configuration["DashboardBackend:ApplicationName"] ?? "Aspire",
                builder.Configuration["DashboardBackend:Version"] ?? "0.0.0-dev",
                RuntimeInformation.FrameworkDescription);

            return Results.Json(
                configuration,
                DashboardBackendJsonSerializerContext.Default.DashboardConfiguration);
        });

        return app;
    }
}
