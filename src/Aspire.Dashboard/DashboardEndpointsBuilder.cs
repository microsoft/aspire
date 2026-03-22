// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Api;
using Aspire.Dashboard.Configuration;
using Aspire.Dashboard.Mcp;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Utils;
using Aspire.Otlp.Serialization;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Mvc;
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
            builder = endpoints.MapPost("/api/validatetoken", async (string token, HttpContext httpContext, IOptionsMonitor<DashboardOptions> dashboardOptions) =>
            {
                return await ValidateTokenMiddleware.TryAuthenticateAsync(token, httpContext, dashboardOptions).ConfigureAwait(false);
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

    public static void MapDashboardMcp(this IEndpointRouteBuilder endpoints, DashboardOptions dashboardOptions)
    {
        IEndpointConventionBuilder builder;
        if (!dashboardOptions.Mcp.Disabled.GetValueOrDefault())
        {
            builder = endpoints.MapMcp("/mcp").RequireAuthorization(McpApiKeyAuthenticationHandler.PolicyName);
        }
        else
        {
            builder = endpoints.MapPostNotFound("/mcp");
        }
        builder.SkipStatusCodePages();
    }

    public static void MapTelemetryApi(this IEndpointRouteBuilder endpoints, DashboardOptions dashboardOptions)
    {
        // Check if API is enabled (defaults to disabled if not specified)
        if (!dashboardOptions.Api.Enabled.GetValueOrDefault())
        {
            return;
        }

        var group = endpoints.MapGroup("/api/telemetry")
            .RequireAuthorization(ApiAuthenticationHandler.PolicyName)
            .SkipStatusCodePages();

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
            CancellationToken cancellationToken) =>
        {
            if (follow == true)
            {
                await StreamNdjsonAsync(httpContext, service.FollowSpansAsync(resource, traceId, hasError, cancellationToken), cancellationToken).ConfigureAwait(false);
                return Results.Empty;
            }

            var response = service.GetSpans(resource, traceId, hasError, limit);
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
            CancellationToken cancellationToken) =>
        {
            if (follow == true)
            {
                await StreamNdjsonAsync(httpContext, service.FollowLogsAsync(resource, traceId, severity, cancellationToken), cancellationToken).ConfigureAwait(false);
                return Results.Empty;
            }

            var response = service.GetLogs(resource, traceId, severity, limit);
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
            [FromQuery] int? limit) =>
        {
            var response = service.GetTraces(resource, hasError, limit);
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

    private static async Task StreamNdjsonAsync(HttpContext httpContext, IAsyncEnumerable<string> items, CancellationToken cancellationToken)
    {
        // Set headers for NDJSON streaming:
        // - application/x-ndjson: Standard content type for newline-delimited JSON
        // - no-cache: Prevent caching of streaming response
        // - X-Accel-Buffering: no: Disable nginx buffering for real-time streaming
        httpContext.Response.ContentType = "application/x-ndjson";
        httpContext.Response.Headers.CacheControl = "no-cache";
        httpContext.Response.Headers["X-Accel-Buffering"] = "no";

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
}
