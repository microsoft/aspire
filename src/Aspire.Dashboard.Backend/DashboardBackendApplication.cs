// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.InteropServices;
using Aspire.Shared;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aspire.Dashboard.Backend;

internal static class DashboardBackendApplication
{
    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configureBuilder = null)
    {
        var builder = WebApplication.CreateSlimBuilder(args);
        // CreateSlimBuilder omits HTTPS configuration to keep the default host small. The
        // standalone dashboard still needs HTTPS when it participates in browser-token or OIDC
        // authentication, so opt into Kestrel's configuration-backed certificate support.
        builder.WebHost.UseKestrelHttpsConfiguration();
        builder.Services.TryAddSingleton<IDashboardResourceServiceConnection, DashboardResourceServiceConnection>();
        builder.Services.TryAddSingleton<DashboardResourceSnapshotService>();
        builder.Services.TryAddSingleton<IDashboardResourceSnapshotProvider>(services => services.GetRequiredService<DashboardResourceSnapshotService>());
        builder.Services.TryAddSingleton<IDashboardResourceEventSource>(services => services.GetRequiredService<DashboardResourceSnapshotService>());
        builder.Services.TryAddSingleton<IDashboardCommandExecutor, DashboardCommandExecutor>();
        builder.Services.TryAddSingleton<DashboardInteractionService>();
        builder.Services.TryAddSingleton<IDashboardInteractionService>(services => services.GetRequiredService<DashboardInteractionService>());
        builder.Services.TryAddSingleton<IDashboardStructuredLogSource, DashboardStructuredLogProxy>();
        builder.Services.TryAddSingleton<IDashboardConsoleLogSource, DashboardConsoleLogProxy>();
        builder.Services.TryAddSingleton<IDashboardLegacyApiProxy, DashboardLegacyApiProxy>();
        builder.Services.TryAddSingleton<IDashboardFrontendAssetProvider, EmbeddedDashboardFrontendAssetProvider>();
        builder.Services.AddHostedService(services => services.GetRequiredService<DashboardResourceSnapshotService>());
        builder.Services.AddHostedService(services => services.GetRequiredService<DashboardInteractionService>());
        builder.Services.AddSignalR();
        builder.Services.Configure<JsonHubProtocolOptions>(options =>
        {
            options.PayloadSerializerOptions.TypeInfoResolverChain.Insert(0, DashboardBackendJsonSerializerContext.Default);
        });
        configureBuilder?.Invoke(builder);

        var app = builder.Build();
        app.UseDashboardDevelopmentAccessPolicy();

        app.MapGet(DashboardApiContract.DiscoveryPath, () =>
        {
            var discovery = new DashboardApiDiscovery(
                DashboardApiContract.Product,
                [
                    new DashboardApiVersion(
                        DashboardApiContract.CurrentVersion,
                        DashboardApiContract.VersionOneBasePath,
                        [
                            DashboardApiContract.ConfigurationCapability,
                            DashboardApiContract.ResourcesCapability,
                            DashboardApiContract.ResourceStreamCapability,
                            DashboardApiContract.CommandsCapability,
                            DashboardApiContract.StructuredLogsCapability,
                            DashboardApiContract.StructuredLogStreamCapability,
                            DashboardApiContract.ConsoleLogsCapability,
                            DashboardApiContract.ConsoleLogStreamCapability,
                            DashboardApiContract.InteractionsCapability
                        ])
                ]);

            return Results.Json(
                discovery,
                DashboardBackendJsonSerializerContext.Default.DashboardApiDiscovery);
        });

        app.MapGet($"{DashboardApiContract.VersionOneBasePath}/config", () =>
        {
            var configuration = new DashboardConfiguration(
                builder.Configuration["DashboardBackend:ApplicationName"] ?? "Aspire",
                builder.Configuration["DashboardBackend:Version"]
                    ?? AssemblyVersionHelper.GetDisplayVersion(typeof(DashboardBackendApplication).Assembly)
                    ?? "unknown",
                RuntimeInformation.FrameworkDescription);

            return Results.Json(
                configuration,
                DashboardBackendJsonSerializerContext.Default.DashboardConfiguration);
        });

        app.MapGet($"{DashboardApiContract.VersionOneBasePath}/resources", async (
            IDashboardResourceSnapshotProvider resourceSnapshotProvider,
            HttpContext context) =>
        {
            try
            {
                context.Response.Headers.CacheControl = "no-store";
                var resources = await resourceSnapshotProvider.GetSnapshotAsync(context.RequestAborted).ConfigureAwait(false);
                return Results.Json(
                    resources,
                    DashboardBackendJsonSerializerContext.Default.DashboardResourceArray);
            }
            catch (DashboardResourceServiceUnavailableException ex)
            {
                return Results.Text(ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });

        app.MapHub<DashboardResourcesHub>(DashboardApiContract.ResourceStreamPath);
        app.MapHub<DashboardStructuredLogsHub>(DashboardApiContract.StructuredLogStreamPath);
        app.MapHub<DashboardConsoleLogsHub>(DashboardApiContract.ConsoleLogStreamPath);

        app.MapGet($"{DashboardApiContract.VersionOneBasePath}/structured-logs", async (
            HttpContext context,
            IDashboardStructuredLogSource structuredLogSource) =>
        {
            try
            {
                context.Response.Headers.CacheControl = "no-store";
                var snapshot = await structuredLogSource.GetSnapshotAsync(
                    DashboardRequestCredentials.From(context.Request),
                    context.RequestAborted).ConfigureAwait(false);
                return Results.Json(
                    snapshot,
                    DashboardBackendJsonSerializerContext.Default.DashboardStructuredLogsSnapshot);
            }
            catch (DashboardStructuredLogServiceUnavailableException ex)
            {
                return Results.Text(ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });

        app.MapPost($"{DashboardApiContract.VersionOneBasePath}/commands/execute", ExecuteCommandAsync);

        app.MapGet($"{DashboardApiContract.VersionOneBasePath}/interactions", (
            HttpContext context,
            IDashboardInteractionService interactionService) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var interactions = interactionService.GetInteractions();
            return Results.Json(
                interactions,
                DashboardBackendJsonSerializerContext.Default.DashboardInteractionArray);
        });
        app.MapPost($"{DashboardApiContract.VersionOneBasePath}/interactions/respond", RespondToInteractionAsync);

        // Older React bundles use these unversioned routes when served by the standalone
        // backend. Keep the aliases, but route them into the same direct resource-service
        // session as the versioned contract instead of splitting command and interaction
        // ownership across the legacy dashboard.
        app.MapGet("/api/deck/interactions", (
            HttpContext context,
            IDashboardInteractionService interactionService) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var interactions = interactionService.GetInteractions();
            return Results.Json(
                interactions,
                DashboardBackendJsonSerializerContext.Default.DashboardInteractionArray);
        });
        app.MapPost("/api/deck/interactions/respond", RespondToInteractionAsync);
        app.MapPost("/api/deck/commands/execute", ExecuteCommandAsync);

        // Keep the SPA fallback last so versioned API and SignalR routes always win. Unknown
        // /api paths remain 404s instead of being disguised as successful HTML responses.
        DashboardFrontendAssets.Map(app);

        return app;

        static async Task<IResult> ExecuteCommandAsync(
            HttpContext context,
            IDashboardCommandExecutor commandExecutor)
        {
            var request = await context.Request.ReadFromJsonAsync(
                DashboardBackendJsonSerializerContext.Default.DashboardExecuteCommandRequest,
                context.RequestAborted).ConfigureAwait(false);
            if (request is null
                || string.IsNullOrWhiteSpace(request.ResourceName)
                || string.IsNullOrWhiteSpace(request.CommandName))
            {
                return Results.BadRequest();
            }

            try
            {
                var response = await commandExecutor.ExecuteAsync(request, context.RequestAborted).ConfigureAwait(false);
                return response is null
                    ? Results.NotFound()
                    : Results.Json(response, DashboardBackendJsonSerializerContext.Default.DashboardCommandResponse);
            }
            catch (DashboardResourceServiceUnavailableException ex)
            {
                return Results.Text(ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        }

        static async Task<IResult> RespondToInteractionAsync(
            HttpContext context,
            IDashboardInteractionService interactionService)
        {
            var request = await context.Request.ReadFromJsonAsync(
                DashboardBackendJsonSerializerContext.Default.DashboardRespondInteractionRequest,
                context.RequestAborted).ConfigureAwait(false);
            if (request is null
                || request.InteractionId <= 0
                || string.IsNullOrWhiteSpace(request.Action))
            {
                return Results.BadRequest();
            }

            return await interactionService.RespondAsync(request, context.RequestAborted).ConfigureAwait(false)
                ? Results.NoContent()
                : Results.NotFound();
        }
    }
}
