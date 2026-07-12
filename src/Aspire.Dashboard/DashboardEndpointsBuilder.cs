// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Aspire.Dashboard.Api;
using Aspire.Dashboard.Configuration;
using Aspire.Dashboard.Extensions;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Otlp.Storage;
using Aspire.Dashboard.Utils;
using Aspire.Otlp.Serialization;
using Aspire.Shared.ConsoleLogs;
using Humanizer;
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

        group.MapGet("/config", (
            HttpContext httpContext,
            IDashboardClient dashboardClient,
            IOptionsMonitor<DashboardOptions> options,
            IStringLocalizer<Resources.Login> loginLocalizer,
            IStringLocalizer<Resources.Dialogs> dialogsLocalizer) =>
        {
            var dashboardOptions = options.CurrentValue;
            DeckUser? user = null;
            if (dashboardOptions.Frontend.AuthMode == FrontendAuthMode.OpenIdConnect &&
                httpContext.User.Identity is System.Security.Claims.ClaimsIdentity { IsAuthenticated: true } identity)
            {
                var name = identity.FindFirst(dashboardOptions.Frontend.OpenIdConnect.GetNameClaimTypes());
                if (string.IsNullOrWhiteSpace(name))
                {
                    name = loginLocalizer[nameof(Resources.Login.AuthorizedUser)];
                }
                var username = identity.FindFirst(dashboardOptions.Frontend.OpenIdConnect.GetUsernameClaimTypes());
                user = new DeckUser(name, username);
            }
            var currentCulture = GlobalizationHelpers.TryGetKnownParentCulture(CultureInfo.CurrentUICulture, out var matchedCulture)
                ? matchedCulture
                : CultureInfo.CurrentUICulture;
            var isAgentHelpEnabled = dashboardOptions.UI.DisableAgentHelp != true;
            string? agentHelpMarkdown = null;
            if (isAgentHelpEnabled)
            {
                const string appHostLearnMoreUrl = "https://aka.ms/aspire/ai-agents-apphost";
                const string standaloneLearnMoreUrl = "https://aka.ms/aspire/dashboard-ai-standalone";
                const string installCliUrl = "https://aka.ms/aspire/install-cli";
                agentHelpMarkdown = dashboardClient.IsEnabled
                    ? string.Format(
                        CultureInfo.CurrentCulture,
                        dialogsLocalizer[nameof(Resources.Dialogs.AIAgentsDialogAppHostDescription)],
                        appHostLearnMoreUrl,
                        installCliUrl)
                    : string.Format(
                        CultureInfo.CurrentCulture,
                        dialogsLocalizer[nameof(Resources.Dialogs.AIAgentsDialogStandaloneDescription)],
                        GetAgentDashboardUrl(dashboardOptions),
                        standaloneLearnMoreUrl,
                        installCliUrl);
            }
            var config = new DeckConfig(
                ApplicationName: dashboardClient.ApplicationName,
                ResourceServiceUrl: null,
                OtlpGrpcUrl: null,
                OtlpHttpUrl: null,
                Version: VersionHelpers.DashboardDisplayVersion ?? string.Empty,
                RuntimeVersion: RuntimeInformation.FrameworkDescription,
                IsTelemetryEndpointUnsecured:
                    (dashboardOptions.Otlp.GetGrpcEndpointAddress() is not null || dashboardOptions.Otlp.GetHttpEndpointAddress() is not null) &&
                    dashboardOptions.Otlp.AuthMode == OtlpAuthMode.Unsecured &&
                    !dashboardOptions.Otlp.SuppressUnsecuredMessage,
                IsApiEndpointUnsecured:
                    !dashboardOptions.Api.Disabled.GetValueOrDefault() &&
                    dashboardOptions.Api.AuthMode == ApiAuthMode.Unsecured,
                FrontendAuthMode: dashboardOptions.Frontend.AuthMode?.ToString() ?? "Unknown",
                User: user,
                Culture: currentCulture.Name,
                Cultures: [.. GlobalizationHelpers.OrderedLocalizedCultures.Select(culture => new DeckCulture(culture.Name, culture.NativeName.Humanize()))],
                IsAgentHelpEnabled: isAgentHelpEnabled,
                AgentHelpMarkdown: agentHelpMarkdown);

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

        group.MapGet("/manage-data", (
            HttpContext httpContext,
            IDashboardClient dashboardClient,
            TelemetryRepository telemetryRepository,
            TelemetryImportService telemetryImportService) =>
        {
            httpContext.Response.Headers.CacheControl = "no-store";
            var resources = dashboardClient.GetResources()
                .ToDictionary(
                    resource => resource.Name,
                    resource => new DeckManageDataResource(
                        resource.Name,
                        resource.DisplayName,
                        [nameof(AspireDataType.ResourceDetails), nameof(AspireDataType.ConsoleLogs)]),
                    StringComparers.ResourceName);

            foreach (var otlpResource in telemetryRepository.GetResources())
            {
                var name = otlpResource.ResourceKey.GetCompositeName();
                var telemetryTypes = new List<string>();
                if (otlpResource.HasLogs)
                {
                    telemetryTypes.Add(nameof(AspireDataType.StructuredLogs));
                }
                if (otlpResource.HasTraces)
                {
                    telemetryTypes.Add(nameof(AspireDataType.Traces));
                }
                if (otlpResource.HasMetrics)
                {
                    telemetryTypes.Add(nameof(AspireDataType.Metrics));
                }

                if (resources.TryGetValue(name, out var resource))
                {
                    resources[name] = resource with { DataTypes = [.. resource.DataTypes, .. telemetryTypes] };
                }
                else
                {
                    resources[name] = new DeckManageDataResource(name, name, [.. telemetryTypes]);
                }
            }

            var response = new DeckManageDataResponse(
                [.. resources.Values.OrderBy(resource => resource.DisplayName, StringComparers.ResourceName)],
                telemetryImportService.IsImportEnabled);
            return Results.Json(response, DeckApiJsonSerializerContext.Default.DeckManageDataResponse);
        });

        group.MapPost("/manage-data/export", async (
            HttpContext httpContext,
            TelemetryExportService exportService) =>
        {
            var request = await httpContext.Request.ReadFromJsonAsync(
                DeckApiJsonSerializerContext.Default.DeckManageDataRequest,
                httpContext.RequestAborted).ConfigureAwait(false);
            if (request is null)
            {
                return Results.BadRequest();
            }

            var selectedResources = MapManageDataSelections(request);
            using var export = await exportService.ExportSelectedAsync(selectedResources, httpContext.RequestAborted).ConfigureAwait(false);
            var fileName = $"aspire-telemetry-export-{DateTime.UtcNow:yyyyMMdd-HHmmss}.zip";
            return Results.File(export.ToArray(), "application/zip", fileName);
        });

        group.MapPost("/manage-data/import", async (
            HttpContext httpContext,
            TelemetryImportService importService) =>
        {
            const long maximumFileSize = 100 * 1024 * 1024;
            var maximumBodySize = httpContext.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
            if (maximumBodySize is { IsReadOnly: false })
            {
                maximumBodySize.MaxRequestBodySize = maximumFileSize;
            }
            if (!importService.IsImportEnabled)
            {
                return Results.NotFound();
            }
            if (httpContext.Request.ContentLength is > maximumFileSize)
            {
                return Results.BadRequest("The import file exceeds the 100 MB limit.");
            }
            if (!httpContext.Request.Headers.TryGetValue("X-Aspire-File-Name", out var fileName) || string.IsNullOrWhiteSpace(fileName))
            {
                return Results.BadRequest("The X-Aspire-File-Name header is required.");
            }

            var safeFileName = Path.GetFileName(fileName.ToString());
            var extension = Path.GetExtension(safeFileName).ToLowerInvariant();
            if (extension is not (".zip" or ".json"))
            {
                return Results.BadRequest("Only .zip and .json telemetry imports are supported.");
            }

            await importService.ImportAsync(safeFileName, httpContext.Request.Body, httpContext.RequestAborted).ConfigureAwait(false);
            return Results.NoContent();
        });

        group.MapPost("/manage-data/remove", async (
            HttpContext httpContext,
            TelemetryRepository telemetryRepository,
            ConsoleLogsManager consoleLogsManager,
            BrowserTimeProvider timeProvider,
            IDashboardClient dashboardClient) =>
        {
            var request = await httpContext.Request.ReadFromJsonAsync(
                DeckApiJsonSerializerContext.Default.DeckManageDataRequest,
                httpContext.RequestAborted).ConfigureAwait(false);
            if (request is null)
            {
                return Results.BadRequest();
            }

            var selectedResources = MapManageDataSelections(request);
            telemetryRepository.ClearSelectedSignals(selectedResources);

            var consoleResources = selectedResources
                .Where(selection => selection.Value.Contains(AspireDataType.ConsoleLogs))
                .Select(selection => selection.Key)
                .ToArray();
            if (dashboardClient.IsEnabled && consoleResources.Length > 0)
            {
                await consoleLogsManager.EnsureInitializedAsync().ConfigureAwait(false);
                var filters = consoleLogsManager.Filters;
                var clearedAt = timeProvider.GetUtcNow().UtcDateTime;
                foreach (var resourceName in consoleResources)
                {
                    filters = filters.WithResourceCleared(resourceName, clearedAt);
                }
                await consoleLogsManager.UpdateFiltersAsync(filters).ConfigureAwait(false);
            }

            return Results.NoContent();
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
            [FromQuery] bool? showCount,
            [FromQuery] string? histogramMode) =>
        {
            if (string.IsNullOrWhiteSpace(resource)
                || string.IsNullOrWhiteSpace(meter)
                || string.IsNullOrWhiteSpace(instrument))
            {
                return Results.BadRequest();
            }
            if (histogramMode is not null
                && histogramMode is not ("percentiles" or "count" or "sum" or "buckets"))
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
            var response = service.GetMetricSeries(resource, meter, instrument, windowSeconds, maxPoints, dimensionFilters, showCount, histogramMode);
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

    private static Dictionary<string, HashSet<AspireDataType>> MapManageDataSelections(DeckManageDataRequest request)
    {
        var result = new Dictionary<string, HashSet<AspireDataType>>(StringComparers.ResourceName);
        foreach (var selection in request.Resources)
        {
            if (string.IsNullOrWhiteSpace(selection.ResourceName))
            {
                continue;
            }

            if (!result.TryGetValue(selection.ResourceName, out var dataTypes))
            {
                dataTypes = [];
                result.Add(selection.ResourceName, dataTypes);
            }
            foreach (var value in selection.DataTypes)
            {
                if (Enum.TryParse<AspireDataType>(value, ignoreCase: false, out var dataType))
                {
                    dataTypes.Add(dataType);
                }
            }
        }
        return result;
    }

    private static string GetAgentDashboardUrl(DashboardOptions options)
    {
        var baseUrl = Aspire.Dashboard.Model.Assistant.AIHelpers.GetDashboardUrl(options)?.TrimEnd('/') ?? "http://localhost:18888";
        return options.Api.AuthMode is ApiAuthMode.ApiKey &&
               options.Frontend.AuthMode is FrontendAuthMode.BrowserToken &&
               options.Frontend.BrowserToken is { } token
            ? $"{baseUrl}/login?t={token}"
            : baseUrl;
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
