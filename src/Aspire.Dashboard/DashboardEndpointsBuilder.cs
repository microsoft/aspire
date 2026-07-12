// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.InteropServices;
using System.Text.Json;
using Aspire.Dashboard.Api;
using Aspire.Dashboard.Configuration;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Utils;
using Aspire.Otlp.Serialization;
using Aspire.Shared.ConsoleLogs;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;

namespace Aspire.Dashboard;

public static class DashboardEndpointsBuilder
{
    public static void MapDashboardHealthChecks(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapHealthChecks($"/{DashboardUrls.HealthBasePath}").AllowAnonymous();
    }

    public static void MapDashboardApi(this IEndpointRouteBuilder endpoints, DashboardOptions dashboardOptions)
    {
        IEndpointConventionBuilder builder;
        if (dashboardOptions.Frontend.AuthMode == FrontendAuthMode.BrowserToken)
        {
            builder = endpoints.MapPost("/api/validatetoken", async ([FromBody] ValidateTokenRequest request, HttpContext httpContext, IOptionsMonitor<DashboardOptions> dashboardOptions) =>
            {
                return await ValidateTokenMiddleware.TryAuthenticateAsync(request.Token, httpContext, dashboardOptions).ConfigureAwait(false);
            });

#if DEBUG
            // Available in local debug for testing.
            endpoints.MapGet("/api/signout", async (HttpContext httpContext) =>
            {
                await Microsoft.AspNetCore.Authentication.AuthenticationHttpContextExtensions.SignOutAsync(
                    httpContext,
                    CookieAuthenticationDefaults.AuthenticationScheme).ConfigureAwait(false);
                httpContext.Response.Redirect("/");
            }).SkipStatusCodePages();
#endif
        }
        else
        {
            builder = endpoints.MapPostNotFound("/api/validatetoken");
        }
        builder.SkipStatusCodePages();

        if (dashboardOptions.Frontend.AuthMode == FrontendAuthMode.OpenIdConnect)
        {
            endpoints.MapPost("/authentication/logout", () => TypedResults.SignOut(authenticationSchemes: [CookieAuthenticationDefaults.AuthenticationScheme, OpenIdConnectDefaults.AuthenticationScheme])).SkipStatusCodePages();
        }

        endpoints.MapGet("/api/set-language", async (string? language, string? redirectUrl, [FromHeader(Name = "Accept-Language")] string? acceptLanguage, HttpContext httpContext) =>
        {
            if (string.IsNullOrEmpty(redirectUrl))
            {
                return Results.BadRequest();
            }

            // The passed in language should be one of the localized cultures.
            var newLanguage = GlobalizationHelpers.OrderedLocalizedCultures.SingleOrDefault(c => string.Equals(c.Name, language, StringComparisons.CultureName));
            if (newLanguage == null)
            {
                return Results.BadRequest();
            }

            if (!GlobalizationHelpers.ExpandedLocalizedCultures.TryGetValue(newLanguage.Name, out var availableCultures))
            {
                return Results.BadRequest();
            }

            // The passed in language is one of the supported localized cultures. e.g. en, fr, de, etc.
            // However, if the browser specifies a culture via accept-language header that is compatible with the language, then we want to use that.
            // For example, the new language is "en" and accept-language is "en-GB", then we want to use "en-GB".
            RequestCulture? requestCulture = null;
            if (acceptLanguage != null)
            {
                requestCulture = await GlobalizationHelpers.ResolveSetCultureToAcceptedCultureAsync(acceptLanguage, availableCultures).ConfigureAwait(false);
            }
            requestCulture ??= new RequestCulture(newLanguage.Name, newLanguage.Name);

            httpContext.Response.Cookies.Append(
                CookieRequestCultureProvider.DefaultCookieName,
                CookieRequestCultureProvider.MakeCookieValue(requestCulture),
                new CookieOptions { Expires = DateTimeOffset.UtcNow.AddYears(1) }); // consistent with theme cookie expiry

            return Results.LocalRedirect(redirectUrl);
        }).SkipStatusCodePages();

    }

    public static void MapDeckApi(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/deck")
            .RequireAuthorization(FrontendAuthorizationDefaults.PolicyName)
            .SkipStatusCodePages();

        group.MapGet("/config", (IDashboardClient dashboardClient) =>
        {
            var config = new DeckConfig(
                ApplicationName: dashboardClient.ApplicationName,
                ResourceServiceUrl: null,
                OtlpGrpcUrl: null,
                OtlpHttpUrl: null,
                Version: VersionHelpers.DashboardDisplayVersion ?? string.Empty,
                RuntimeVersion: RuntimeInformation.FrameworkDescription);

            return Results.Json(config, DeckApiJsonSerializerContext.Default.DeckConfig);
        });

        group.MapGet("/resources", (
            HttpContext httpContext,
            IDashboardClient dashboardClient,
            IStringLocalizer<Resources.Resources> localizer) =>
        {
            // The payload can include environment variables and sensitive resource properties.
            // Prevent authenticated responses from being retained by browser or intermediary caches.
            httpContext.Response.Headers.CacheControl = "no-store";

            var resources = dashboardClient.GetResources()
                .Select(resource => DeckResourceMapper.Map(resource, localizer))
                .ToArray();

            return Results.Json(resources, DeckApiJsonSerializerContext.Default.DeckResourceArray);
        });

        group.MapGet("/interactions", (HttpContext httpContext, DeckInteractionService interactionService) =>
        {
            httpContext.Response.Headers.CacheControl = "no-store";
            return Results.Json(
                interactionService.GetInteractions(),
                DeckApiJsonSerializerContext.Default.DeckInteractionArray);
        });

        group.MapPost("/interactions/respond", async (HttpContext httpContext, DeckInteractionService interactionService) =>
        {
            var request = await httpContext.Request.ReadFromJsonAsync(
                DeckApiJsonSerializerContext.Default.DeckRespondInteractionRequest,
                httpContext.RequestAborted).ConfigureAwait(false);
            if (request is null || request.InteractionId <= 0 || string.IsNullOrWhiteSpace(request.Action))
            {
                return Results.BadRequest();
            }

            var responded = await interactionService.RespondAsync(
                request.InteractionId,
                request.Action,
                request.Values ?? [],
                httpContext.RequestAborted).ConfigureAwait(false);
            return responded ? Results.NoContent() : Results.NotFound();
        });

        group.MapPost("/commands/execute", async (HttpContext httpContext, IDashboardClient dashboardClient) =>
        {
            var request = await httpContext.Request.ReadFromJsonAsync(
                DeckApiJsonSerializerContext.Default.DeckExecuteCommandRequest,
                httpContext.RequestAborted).ConfigureAwait(false);
            if (request is null || string.IsNullOrWhiteSpace(request.ResourceName) || string.IsNullOrWhiteSpace(request.CommandName))
            {
                return Results.BadRequest();
            }

            var resource = dashboardClient.GetResources().SingleOrDefault(
                resource => string.Equals(resource.Name, request.ResourceName, StringComparisons.ResourceName));
            var command = resource?.Commands.SingleOrDefault(
                command => string.Equals(command.Name, request.CommandName, StringComparison.Ordinal));
            if (resource is null || command is null)
            {
                return Results.NotFound();
            }

            var response = await dashboardClient.ExecuteResourceCommandAsync(
                resource.Name,
                resource.ResourceType,
                command,
                new ExecuteResourceCommandOptions(),
                httpContext.RequestAborted).ConfigureAwait(false);

            var result = new DeckCommandResponse(
                Kind: response.Kind switch
                {
                    ResourceCommandResponseKind.Succeeded => "succeeded",
                    ResourceCommandResponseKind.Failed => "failed",
                    ResourceCommandResponseKind.Cancelled => "cancelled",
                    ResourceCommandResponseKind.InvalidArguments => "invalidArguments",
                    _ => "undefined"
                },
                Message: response.Message ?? response.ErrorMessage,
                Result: response.Result is { } commandResult
                    ? new DeckCommandResult(
                        Value: commandResult.Value,
                        Format: commandResult.Format switch
                        {
                            CommandResultFormat.Json => "json",
                            CommandResultFormat.Markdown => "markdown",
                            _ => "text"
                        },
                        DisplayImmediately: commandResult.DisplayImmediately)
                    : null);
            return Results.Json(result, DeckApiJsonSerializerContext.Default.DeckCommandResponse);
        });

        group.MapGet("/resources/{resourceName}/console-logs", async (
            string resourceName,
            HttpContext httpContext,
            IDashboardClient dashboardClient) =>
        {
            if (!dashboardClient.GetResources().Any(resource =>
                string.Equals(resource.Name, resourceName, StringComparisons.ResourceName)))
            {
                return Results.NotFound();
            }

            await StreamDeckConsoleLogsAsync(httpContext, dashboardClient, resourceName, httpContext.RequestAborted).ConfigureAwait(false);
            return Results.Empty;
        });

        group.MapGet("/telemetry/logs", async (
            TelemetryApiService service,
            HttpContext httpContext,
            [FromQuery] string[]? resource,
            [FromQuery] string? traceId,
            [FromQuery] string? severity,
            [FromQuery] int? limit,
            [FromQuery] bool? follow,
            [FromQuery] string? search,
            CancellationToken cancellationToken) =>
        {
            if (follow == true)
            {
                await StreamNdjsonAsync(
                    httpContext,
                    service.FollowLogsAsync(resource, traceId, severity, search, cancellationToken),
                    cancellationToken,
                    cacheControl: "no-store").ConfigureAwait(false);
                return Results.Empty;
            }

            // Structured logs can contain arbitrary application data and attributes.
            // The Deck route uses frontend auth, so its response must not be retained.
            httpContext.Response.Headers.CacheControl = "no-store";
            var response = service.GetLogs(resource, traceId, severity, limit, search);
            if (response is null)
            {
                return Results.NotFound(new ProblemDetails
                {
                    Title = "Resource not found",
                    Detail = "No resource with specified name(s) was found.",
                    Status = StatusCodes.Status404NotFound
                });
            }

            return Results.Json(response, OtlpJsonSerializerContext.Default.TelemetryApiResponse);
        });

        group.MapGet("/telemetry/spans", async (
            TelemetryApiService service,
            HttpContext httpContext,
            [FromQuery] string[]? resource,
            [FromQuery] string? traceId,
            [FromQuery] bool? hasError,
            [FromQuery] int? limit,
            [FromQuery] bool? follow,
            [FromQuery] string? search,
            CancellationToken cancellationToken) =>
        {
            if (follow == true)
            {
                await StreamNdjsonAsync(
                    httpContext,
                    service.FollowSpansAsync(resource, traceId, hasError, search, cancellationToken),
                    cancellationToken,
                    cacheControl: "no-store").ConfigureAwait(false);
                return Results.Empty;
            }

            // Spans can contain arbitrary application data and attributes. The Deck route
            // uses frontend auth, so its response must not be retained.
            httpContext.Response.Headers.CacheControl = "no-store";
            var response = service.GetSpans(resource, traceId, hasError, limit, search);
            if (response is null)
            {
                return Results.NotFound(new ProblemDetails
                {
                    Title = "Resource not found",
                    Detail = "No resource with specified name(s) was found.",
                    Status = StatusCodes.Status404NotFound
                });
            }

            return Results.Json(response, OtlpJsonSerializerContext.Default.TelemetryApiResponse);
        });

        group.MapDelete("/telemetry/logs", (
            TelemetryApiService service,
            [FromQuery] string? resource) =>
        {
            if (!service.ClearLogs(resource))
            {
                return Results.NotFound(new ProblemDetails
                {
                    Title = "Resource not found",
                    Detail = "No resource with the specified name was found.",
                    Status = StatusCodes.Status404NotFound
                });
            }

            return Results.NoContent();
        });

        group.MapDelete("/telemetry/spans", (
            TelemetryApiService service,
            [FromQuery] string? resource) =>
        {
            if (!service.ClearTraces(resource))
            {
                return Results.NotFound(new ProblemDetails
                {
                    Title = "Resource not found",
                    Detail = "No resource with the specified name was found.",
                    Status = StatusCodes.Status404NotFound
                });
            }

            return Results.NoContent();
        });

        group.MapDelete("/telemetry/metrics", (
            TelemetryApiService service,
            [FromQuery] string? resource) =>
        {
            if (!service.ClearMetrics(resource))
            {
                return Results.NotFound(new ProblemDetails
                {
                    Title = "Resource not found",
                    Detail = "No resource with the specified name was found.",
                    Status = StatusCodes.Status404NotFound
                });
            }

            return Results.NoContent();
        });

        group.MapGet("/telemetry/metrics", (TelemetryApiService service, HttpContext httpContext) =>
        {
            httpContext.Response.Headers.CacheControl = "no-store";
            return Results.Json(
                service.GetMetricSummaries(),
                DeckApiJsonSerializerContext.Default.DeckMetricSummaryArray);
        });

        group.MapGet("/telemetry/metrics/series", (
            TelemetryApiService service,
            HttpContext httpContext,
            [FromQuery] string? resource,
            [FromQuery] string? meter,
            [FromQuery] string? instrument,
            [FromQuery] int? windowSeconds,
            [FromQuery] int? maxPoints,
            [FromQuery] bool? showCount) =>
        {
            if (string.IsNullOrWhiteSpace(resource)
                || string.IsNullOrWhiteSpace(meter)
                || string.IsNullOrWhiteSpace(instrument))
            {
                return Results.BadRequest();
            }

            httpContext.Response.Headers.CacheControl = "no-store";
            var dimensionFilters = httpContext.Request.Query
                .Where(static item => item.Key.StartsWith("dimension.", StringComparison.Ordinal))
                .ToDictionary(
                    static item => item.Key["dimension.".Length..],
                    static item => item.Value.Count == 1 && item.Value[0] == "x:"
                        ? []
                        : item.Value.Select(static value => value == "n:" ? null : value?[2..]).ToArray(),
                    StringComparer.Ordinal);
            var response = service.GetMetricSeries(resource, meter, instrument, windowSeconds, maxPoints, dimensionFilters, showCount);
            return response is null
                ? Results.NotFound()
                : Results.Json(response, DeckApiJsonSerializerContext.Default.DeckMetricSeriesResponse);
        });
    }

    public static void MapTelemetryApi(this IEndpointRouteBuilder endpoints, DashboardOptions dashboardOptions)
    {
        // Check if API is disabled
        if (dashboardOptions.Api.Disabled.GetValueOrDefault())
        {
            endpoints.MapGetNotFound("/api/telemetry/{*path}").SkipStatusCodePages();
            endpoints.MapPostNotFound("/api/telemetry/{*path}").SkipStatusCodePages();
            return;
        }

        var group = endpoints.MapGroup("/api/telemetry")
            .RequireAuthorization(ApiAuthenticationHandler.PolicyName)
            .SkipStatusCodePages();

        // POST /api/telemetry/validateToken - Exchange a browser token for the telemetry API key.
        // Returns the API key if the token is valid and the API uses key-based auth, or null if unsecured.
        // Returns 401 if the token is invalid, 404 if the telemetry API is not enabled.
        group.MapPost("/validateToken", ([FromBody] TelemetryValidateTokenRequest request, IOptionsMonitor<DashboardOptions> optionsMonitor) =>
        {
            var currentOptions = optionsMonitor.CurrentValue;

            // Validate the browser token
            if (currentOptions.Frontend.AuthMode != FrontendAuthMode.BrowserToken)
            {
                return Results.Unauthorized();
            }

            var expectedBrowserTokenBytes = currentOptions.Frontend.GetBrowserTokenBytes();
            if (expectedBrowserTokenBytes is null || !CompareHelpers.CompareKey(expectedBrowserTokenBytes, request.Token))
            {
                return Results.Unauthorized();
            }

            // Token is valid — return the API key (null if unsecured)
            var apiKey = currentOptions.Api.AuthMode == ApiAuthMode.ApiKey
                ? currentOptions.Api.PrimaryApiKey
                : null;

            return Results.Json(new TelemetryValidateTokenResponse(apiKey), OtlpJsonSerializerContext.Default.TelemetryValidateTokenResponse);
        }).AllowAnonymous();

        // GET /api/telemetry/resources - List resources that have telemetry data
        group.MapGet("/resources", (TelemetryApiService service) =>
        {
            var resources = service.GetResources();
            return Results.Json(resources, OtlpJsonSerializerContext.Default.ResourceInfoJsonArray);
        });

        // GET /api/telemetry/spans - List spans in OTLP JSON format (with optional streaming via ?follow=true)
        // Supports multiple resource names: ?resource=app1&resource=app2
        group.MapGet("/spans", async (
            TelemetryApiService service,
            HttpContext httpContext,
            [FromQuery] string[]? resource,
            [FromQuery] string? traceId,
            [FromQuery] bool? hasError,
            [FromQuery] int? limit,
            [FromQuery] bool? follow,
            [FromQuery] string? search,
            CancellationToken cancellationToken) =>
        {
            if (follow == true)
            {
                await StreamNdjsonAsync(httpContext, service.FollowSpansAsync(resource, traceId, hasError, search, cancellationToken), cancellationToken).ConfigureAwait(false);
                return Results.Empty;
            }

            var response = service.GetSpans(resource, traceId, hasError, limit, search);
            if (response is null)
            {
                return Results.NotFound(new ProblemDetails
                {
                    Title = "Resource not found",
                    Detail = $"No resource with specified name(s) was found.",
                    Status = StatusCodes.Status404NotFound
                });
            }
            return Results.Json(response, OtlpJsonSerializerContext.Default.TelemetryApiResponse);
        });

        // GET /api/telemetry/logs - List logs in OTLP JSON format (with optional streaming via ?follow=true)
        // Supports multiple resource names: ?resource=app1&resource=app2
        group.MapGet("/logs", async (
            TelemetryApiService service,
            HttpContext httpContext,
            [FromQuery] string[]? resource,
            [FromQuery] string? traceId,
            [FromQuery] string? severity,
            [FromQuery] int? limit,
            [FromQuery] bool? follow,
            [FromQuery] string? search,
            CancellationToken cancellationToken) =>
        {
            if (follow == true)
            {
                await StreamNdjsonAsync(httpContext, service.FollowLogsAsync(resource, traceId, severity, search, cancellationToken), cancellationToken).ConfigureAwait(false);
                return Results.Empty;
            }

            var response = service.GetLogs(resource, traceId, severity, limit, search);
            if (response is null)
            {
                return Results.NotFound(new ProblemDetails
                {
                    Title = "Resource not found",
                    Detail = $"No resource with specified name(s) was found.",
                    Status = StatusCodes.Status404NotFound
                });
            }
            return Results.Json(response, OtlpJsonSerializerContext.Default.TelemetryApiResponse);
        });

        // GET /api/telemetry/traces - List traces in OTLP JSON format (snapshot only, no streaming)
        // Supports multiple resource names: ?resource=app1&resource=app2
        group.MapGet("/traces", (
            TelemetryApiService service,
            [FromQuery] string[]? resource,
            [FromQuery] bool? hasError,
            [FromQuery] int? limit,
            [FromQuery] string? search) =>
        {
            var response = service.GetTraces(resource, hasError, limit, search);
            if (response is null)
            {
                return Results.NotFound(new ProblemDetails
                {
                    Title = "Resource not found",
                    Detail = $"No resource with specified name(s) was found.",
                    Status = StatusCodes.Status404NotFound
                });
            }
            return Results.Json(response, OtlpJsonSerializerContext.Default.TelemetryApiResponse);
        });

        // GET /api/telemetry/traces/{traceId} - Get a specific trace with all spans in OTLP format
        group.MapGet("/traces/{traceId}", (
            TelemetryApiService service,
            string traceId) =>
        {
            var response = service.GetTrace(traceId);
            if (response is null)
            {
                return Results.NotFound(new ProblemDetails
                {
                    Title = "Trace not found",
                    Detail = $"No trace with ID '{traceId}' was found.",
                    Status = StatusCodes.Status404NotFound
                });
            }
            return Results.Json(response, OtlpJsonSerializerContext.Default.TelemetryApiResponse);
        });
    }

    private static async Task StreamNdjsonAsync(
        HttpContext httpContext,
        IAsyncEnumerable<string> items,
        CancellationToken cancellationToken,
        string cacheControl = "no-cache")
    {
        // Set headers for NDJSON streaming:
        // - application/x-ndjson: Standard content type for newline-delimited JSON
        // - no-cache: Prevent caching of streaming response
        // - X-Accel-Buffering: no: Disable nginx buffering for real-time streaming
        httpContext.Response.ContentType = "application/x-ndjson";
        httpContext.Response.Headers.CacheControl = cacheControl;
        httpContext.Response.Headers["X-Accel-Buffering"] = "no";
        await httpContext.Response.StartAsync(cancellationToken).ConfigureAwait(false);
        await httpContext.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await foreach (var json in items.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                await httpContext.Response.WriteAsync(json, cancellationToken).ConfigureAwait(false);
                await httpContext.Response.WriteAsync("\n", cancellationToken).ConfigureAwait(false);
                await httpContext.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Client disconnected - this is expected, exit cleanly
        }
    }

    private static async Task StreamDeckConsoleLogsAsync(
        HttpContext httpContext,
        IDashboardClient dashboardClient,
        string resourceName,
        CancellationToken cancellationToken)
    {
        httpContext.Response.ContentType = "application/x-ndjson";
        // Console output can contain secrets. Prevent browsers and intermediaries from
        // retaining an authenticated stream, and disable reverse-proxy response buffering.
        httpContext.Response.Headers.CacheControl = "no-store";
        httpContext.Response.Headers["X-Accel-Buffering"] = "no";
        await httpContext.Response.StartAsync(cancellationToken).ConfigureAwait(false);
        await httpContext.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await foreach (var batch in dashboardClient.SubscribeConsoleLogs(resourceName, cancellationToken).ConfigureAwait(false))
            {
                var response = new DeckConsoleLogEvent(
                    resourceName,
                    batch.Select(line => new DeckConsoleLogLine(
                        line.LineNumber,
                        AnsiParser.StripControlSequences(line.Content),
                        line.IsErrorMessage)).ToArray());
                await JsonSerializer.SerializeAsync(
                    httpContext.Response.Body,
                    response,
                    DeckApiJsonSerializerContext.Default.DeckConsoleLogEvent,
                    cancellationToken).ConfigureAwait(false);
                await httpContext.Response.WriteAsync("\n", cancellationToken).ConfigureAwait(false);
                await httpContext.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The stream owns no state after the request ends, so a browser disconnect is expected.
        }
    }
}

/// <param name="Token">The browser token to validate.</param>
internal sealed record ValidateTokenRequest(string Token);
