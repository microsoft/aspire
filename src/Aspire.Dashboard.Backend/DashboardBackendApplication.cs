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
        builder.Services.TryAddSingleton<ITerminalConnectionResolver>(services => services.GetRequiredService<DashboardResourceSnapshotService>());
        builder.Services.TryAddSingleton<IDashboardCommandExecutor, DashboardCommandExecutor>();
        builder.Services.TryAddSingleton<DashboardInteractionService>();
        builder.Services.TryAddSingleton<IDashboardInteractionService>(services => services.GetRequiredService<DashboardInteractionService>());
        builder.Services.TryAddSingleton<IDashboardStructuredLogSource, DashboardStructuredLogProxy>();
        builder.Services.TryAddSingleton<IDashboardTraceSource, DashboardTraceProxy>();
        builder.Services.TryAddSingleton<IDashboardMetricSource, DashboardMetricProxy>();
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
        app.UseDashboardLegacyAuthentication();
        app.UseWebSockets();

        app.MapGet(DashboardApiContract.DiscoveryPath, (IDashboardLegacyApiProxy legacyApiProxy) =>
        {
            var capabilities = new List<string>
            {
                DashboardApiContract.ConfigurationCapability
            };
            if (legacyApiProxy.IsConfigured)
            {
                capabilities.Add(DashboardApiContract.ShellCapability);
                capabilities.Add(DashboardApiContract.CultureCapability);
                capabilities.Add(DashboardApiContract.AuthenticationCapability);
                capabilities.Add(DashboardApiContract.ManageDataCapability);
            }
            capabilities.AddRange(
            [
                DashboardApiContract.ResourcesCapability,
                DashboardApiContract.ResourceStreamCapability,
                DashboardApiContract.CommandsCapability,
                DashboardApiContract.StructuredLogsCapability,
                DashboardApiContract.StructuredLogStreamCapability,
                DashboardApiContract.StructuredLogClearCapability,
                DashboardApiContract.TracesCapability,
                DashboardApiContract.TraceStreamCapability,
                DashboardApiContract.TraceClearCapability,
                DashboardApiContract.MetricsCapability,
                DashboardApiContract.MetricSeriesCapability,
                DashboardApiContract.MetricClearCapability,
                DashboardApiContract.ConsoleLogsCapability,
                DashboardApiContract.ConsoleLogStreamCapability,
                DashboardApiContract.TerminalCapability,
                DashboardApiContract.InteractionsCapability
            ]);

            var discovery = new DashboardApiDiscovery(
                DashboardApiContract.Product,
                [
                    new DashboardApiVersion(
                        DashboardApiContract.CurrentVersion,
                        DashboardApiContract.VersionOneBasePath,
                        [.. capabilities])
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

        app.MapGet(DashboardApiContract.ShellPath, async (
            HttpContext context,
            IDashboardLegacyApiProxy legacyApiProxy) =>
        {
            await legacyApiProxy.ProxyAsync(context, "api/deck/config").ConfigureAwait(false);
        });

        app.MapGet(DashboardApiContract.CulturePath, async (
            HttpContext context,
            IDashboardLegacyApiProxy legacyApiProxy) =>
        {
            await legacyApiProxy.ProxyAsync(
                context,
                $"api/set-language{context.Request.QueryString}").ConfigureAwait(false);
        });

        app.MapPost(DashboardApiContract.AuthenticationLogoutPath, async (
            HttpContext context,
            IDashboardLegacyApiProxy legacyApiProxy) =>
        {
            await legacyApiProxy.ProxyAsync(context, "authentication/logout").ConfigureAwait(false);
        });

        app.MapGet(DashboardApiContract.ManageDataPath, async (
            HttpContext context,
            IDashboardLegacyApiProxy legacyApiProxy) =>
        {
            await legacyApiProxy.ProxyAsync(context, "api/deck/manage-data").ConfigureAwait(false);
        });
        app.MapPost($"{DashboardApiContract.ManageDataPath}/export", async (
            HttpContext context,
            IDashboardLegacyApiProxy legacyApiProxy) =>
        {
            await legacyApiProxy.ProxyAsync(context, "api/deck/manage-data/export").ConfigureAwait(false);
        });
        app.MapPost($"{DashboardApiContract.ManageDataPath}/import", async (
            HttpContext context,
            IDashboardLegacyApiProxy legacyApiProxy) =>
        {
            // Match the existing dashboard's 100 MB import ceiling. Kestrel's default request
            // limit is smaller, so the sidecar must raise its own limit before streaming the
            // body to the authoritative telemetry import service.
            const long maximumFileSize = 100 * 1024 * 1024;
            var maximumBodySize = context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
            if (maximumBodySize is { IsReadOnly: false })
            {
                maximumBodySize.MaxRequestBodySize = maximumFileSize;
            }
            if (context.Request.ContentLength is > maximumFileSize)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsync(
                    "The import file exceeds the 100 MB limit.",
                    context.RequestAborted).ConfigureAwait(false);
                return;
            }

            await legacyApiProxy.ProxyAsync(context, "api/deck/manage-data/import").ConfigureAwait(false);
        });
        app.MapPost($"{DashboardApiContract.ManageDataPath}/remove", async (
            HttpContext context,
            IDashboardLegacyApiProxy legacyApiProxy) =>
        {
            await legacyApiProxy.ProxyAsync(context, "api/deck/manage-data/remove").ConfigureAwait(false);
        });

        app.MapMethods("/login", [HttpMethods.Get, HttpMethods.Post], async (
            HttpContext context,
            IDashboardLegacyApiProxy legacyApiProxy) =>
        {
            await legacyApiProxy.ProxyAsync(
                context,
                $"login{context.Request.QueryString}").ConfigureAwait(false);
        });
        app.MapPost("/api/validatetoken", async (
            HttpContext context,
            IDashboardLegacyApiProxy legacyApiProxy) =>
        {
            await legacyApiProxy.ProxyAsync(context, "api/validatetoken").ConfigureAwait(false);
        });
        app.MapMethods("/authentication/{**path}", [HttpMethods.Get, HttpMethods.Post], async (
            string? path,
            HttpContext context,
            IDashboardLegacyApiProxy legacyApiProxy) =>
        {
            await legacyApiProxy.ProxyAsync(
                context,
                $"authentication/{path}{context.Request.QueryString}").ConfigureAwait(false);
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
        app.MapHub<DashboardTracesHub>(DashboardApiContract.TraceStreamPath);
        app.MapHub<DashboardConsoleLogsHub>(DashboardApiContract.ConsoleLogStreamPath);
        app.MapTerminalWebSocket();

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

        app.MapDelete($"{DashboardApiContract.VersionOneBasePath}/structured-logs", async (
            HttpContext context,
            IDashboardStructuredLogSource structuredLogSource) =>
        {
            try
            {
                var cleared = await structuredLogSource.ClearAsync(
                    GetSingleQueryValue(context.Request.Query, "resource"),
                    DashboardRequestCredentials.From(context.Request),
                    context.RequestAborted).ConfigureAwait(false);
                return cleared ? Results.NoContent() : Results.NotFound();
            }
            catch (DashboardStructuredLogServiceUnavailableException ex)
            {
                return Results.Text(ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });

        app.MapGet($"{DashboardApiContract.VersionOneBasePath}/traces", async (
            HttpContext context,
            IDashboardTraceSource traceSource) =>
        {
            if (!TryCreateTraceQuery(context.Request.Query, out var query))
            {
                return Results.BadRequest();
            }

            try
            {
                context.Response.Headers.CacheControl = "no-store";
                var snapshot = await traceSource.GetSnapshotAsync(
                    query,
                    DashboardRequestCredentials.From(context.Request),
                    context.RequestAborted).ConfigureAwait(false);
                return snapshot is null
                    ? Results.NotFound()
                    : Results.Json(
                        snapshot,
                        DashboardBackendJsonSerializerContext.Default.DashboardTraceSnapshot);
            }
            catch (DashboardTraceServiceUnavailableException ex)
            {
                return Results.Text(ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });

        app.MapDelete($"{DashboardApiContract.VersionOneBasePath}/traces", async (
            HttpContext context,
            IDashboardTraceSource traceSource) =>
        {
            var resourceName = GetSingleQueryValue(context.Request.Query, "resource");
            try
            {
                var cleared = await traceSource.ClearAsync(
                    resourceName,
                    DashboardRequestCredentials.From(context.Request),
                    context.RequestAborted).ConfigureAwait(false);
                return cleared ? Results.NoContent() : Results.NotFound();
            }
            catch (DashboardTraceServiceUnavailableException ex)
            {
                return Results.Text(ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });

        app.MapGet($"{DashboardApiContract.VersionOneBasePath}/metrics", async (
            HttpContext context,
            IDashboardMetricSource metricSource) =>
        {
            try
            {
                context.Response.Headers.CacheControl = "no-store";
                var summaries = await metricSource.GetSummariesAsync(
                    DashboardRequestCredentials.From(context.Request),
                    context.RequestAborted).ConfigureAwait(false);
                return Results.Json(
                    summaries,
                    DashboardBackendJsonSerializerContext.Default.DashboardMetricSummaryArray);
            }
            catch (DashboardMetricServiceUnavailableException ex)
            {
                return Results.Text(ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });

        app.MapGet($"{DashboardApiContract.VersionOneBasePath}/metrics/series", async (
            HttpContext context,
            IDashboardMetricSource metricSource) =>
        {
            if (!TryCreateMetricSeriesQuery(context.Request.Query, out var query))
            {
                return Results.BadRequest();
            }

            try
            {
                context.Response.Headers.CacheControl = "no-store";
                var series = await metricSource.GetSeriesAsync(
                    query,
                    DashboardRequestCredentials.From(context.Request),
                    context.RequestAborted).ConfigureAwait(false);
                return series is null
                    ? Results.NotFound()
                    : Results.Json(
                        series,
                        DashboardBackendJsonSerializerContext.Default.DashboardMetricSeriesResponse);
            }
            catch (DashboardMetricServiceUnavailableException ex)
            {
                return Results.Text(ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });

        app.MapDelete($"{DashboardApiContract.VersionOneBasePath}/metrics", async (
            HttpContext context,
            IDashboardMetricSource metricSource) =>
        {
            try
            {
                var cleared = await metricSource.ClearAsync(
                    GetSingleQueryValue(context.Request.Query, "resource"),
                    DashboardRequestCredentials.From(context.Request),
                    context.RequestAborted).ConfigureAwait(false);
                return cleared ? Results.NoContent() : Results.NotFound();
            }
            catch (DashboardMetricServiceUnavailableException ex)
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

        static bool TryCreateTraceQuery(
            IQueryCollection values,
            out DashboardTraceQuery query)
        {
            bool? hasError = null;
            var hasErrorText = GetSingleQueryValue(values, "hasError");
            if (hasErrorText is not null)
            {
                if (!bool.TryParse(hasErrorText, out var parsedHasError))
                {
                    query = default!;
                    return false;
                }
                hasError = parsedHasError;
            }

            int? limit = null;
            var limitText = GetSingleQueryValue(values, "limit");
            if (limitText is not null)
            {
                if (!int.TryParse(
                    limitText,
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var parsedLimit)
                    || parsedLimit < 0)
                {
                    query = default!;
                    return false;
                }
                limit = parsedLimit;
            }

            query = new DashboardTraceQuery(
                values["resource"]
                    .Where(static resourceName => !string.IsNullOrWhiteSpace(resourceName))
                    .Select(static resourceName => resourceName!)
                    .ToArray(),
                GetSingleQueryValue(values, "traceId"),
                hasError,
                limit,
                GetSingleQueryValue(values, "search"));
            return true;
        }

        static string? GetSingleQueryValue(IQueryCollection values, string name)
        {
            var value = values[name];
            return value.Count > 0 && !string.IsNullOrWhiteSpace(value[0])
                ? value[0]
                : null;
        }

        static bool TryCreateMetricSeriesQuery(
            IQueryCollection values,
            out DashboardMetricSeriesQuery query)
        {
            var resourceName = GetSingleQueryValue(values, "resource");
            var meterName = GetSingleQueryValue(values, "meter");
            var instrumentName = GetSingleQueryValue(values, "instrument");
            if (resourceName is null || meterName is null || instrumentName is null)
            {
                query = default!;
                return false;
            }

            if (!TryParseOptionalInt(values, "windowSeconds", out var windowSeconds)
                || !TryParseOptionalInt(values, "maxPoints", out var maxPoints)
                || !TryParseOptionalBool(values, "showCount", out var showCount))
            {
                query = default!;
                return false;
            }

            var histogramMode = GetSingleQueryValue(values, "histogramMode");
            if (histogramMode is not null
                && histogramMode is not ("percentiles" or "count" or "sum" or "buckets"))
            {
                query = default!;
                return false;
            }

            var dimensions = new Dictionary<string, string?[]>(StringComparer.Ordinal);
            foreach (var (name, encodedValues) in values)
            {
                const string Prefix = "dimension.";
                if (!name.StartsWith(Prefix, StringComparison.Ordinal))
                {
                    continue;
                }

                var dimensionName = name[Prefix.Length..];
                if (string.IsNullOrWhiteSpace(dimensionName)
                    || !TryDecodeDimensionValues(encodedValues, out var dimensionValues))
                {
                    query = default!;
                    return false;
                }
                dimensions.Add(dimensionName, dimensionValues);
            }

            query = new DashboardMetricSeriesQuery(
                resourceName,
                meterName,
                instrumentName,
                windowSeconds,
                maxPoints,
                showCount,
                histogramMode,
                dimensions);
            return true;
        }

        static bool TryParseOptionalInt(
            IQueryCollection values,
            string name,
            out int? result)
        {
            var text = GetSingleQueryValue(values, name);
            if (text is null)
            {
                result = null;
                return true;
            }

            if (!int.TryParse(
                text,
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out var parsed))
            {
                result = null;
                return false;
            }

            result = parsed;
            return true;
        }

        static bool TryParseOptionalBool(
            IQueryCollection values,
            string name,
            out bool? result)
        {
            var text = GetSingleQueryValue(values, name);
            if (text is null)
            {
                result = null;
                return true;
            }

            if (!bool.TryParse(text, out var parsed))
            {
                result = null;
                return false;
            }

            result = parsed;
            return true;
        }

        static bool TryDecodeDimensionValues(
            Microsoft.Extensions.Primitives.StringValues encodedValues,
            out string?[] values)
        {
            if (encodedValues.Count is 1 && encodedValues[0] is "x:")
            {
                values = [];
                return true;
            }

            values = new string?[encodedValues.Count];
            for (var i = 0; i < encodedValues.Count; i++)
            {
                var value = encodedValues[i];
                if (value is "n:")
                {
                    values[i] = null;
                }
                else if (value?.StartsWith("s:", StringComparison.Ordinal) is true)
                {
                    values[i] = value[2..];
                }
                else
                {
                    values = [];
                    return false;
                }
            }

            return true;
        }
    }
}
