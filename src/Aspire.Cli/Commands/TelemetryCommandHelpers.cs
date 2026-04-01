// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Interaction;
using Aspire.Cli.Mcp.Tools;
using Aspire.Cli.Resources;
using Aspire.Cli.Utils;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Utils;
using Aspire.Otlp.Serialization;
using Aspire.Shared;
using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace Aspire.Cli.Commands;

/// <summary>
/// Shared helper methods for telemetry commands.
/// </summary>
internal static class TelemetryCommandHelpers
{
    /// <summary>
    /// HTTP header name for API authentication.
    /// </summary>
    internal const string ApiKeyHeaderName = "X-API-Key";

    #region Shared Command Options

    /// <summary>
    /// Resource name argument shared across telemetry commands.
    /// </summary>
    internal static Argument<string?> CreateResourceArgument() => new("resource")
    {
        Description = TelemetryCommandStrings.ResourceArgumentDescription,
        Arity = ArgumentArity.ZeroOrOne
    };

    /// <summary>
    /// AppHost option shared across telemetry commands.
    /// </summary>
    internal static OptionWithLegacy<FileInfo?> CreateAppHostOption() => new("--apphost", "--project", SharedCommandStrings.AppHostOptionDescription);

    /// <summary>
    /// Output format option shared across telemetry commands.
    /// </summary>
    internal static Option<OutputFormat> CreateFormatOption() => new("--format")
    {
        Description = TelemetryCommandStrings.FormatOptionDescription
    };

    /// <summary>
    /// Limit option shared across telemetry commands.
    /// </summary>
    internal static Option<int?> CreateLimitOption() => new("--limit", "-n")
    {
        Description = TelemetryCommandStrings.LimitOptionDescription
    };

    /// <summary>
    /// Follow/streaming option for logs and spans commands.
    /// </summary>
    internal static Option<bool> CreateFollowOption() => new("--follow", "-f")
    {
        Description = TelemetryCommandStrings.FollowOptionDescription
    };

    /// <summary>
    /// Trace ID filter option shared across telemetry commands.
    /// </summary>
    internal static Option<string?> CreateTraceIdOption(string name, string? alias = null)
    {
        var option = alias is null ? new Option<string?>(name) : new Option<string?>(name, alias);
        option.Description = TelemetryCommandStrings.TraceIdOptionDescription;
        return option;
    }

    /// <summary>
    /// Has error filter option for spans and traces commands.
    /// </summary>
    internal static Option<bool?> CreateHasErrorOption() => new("--has-error")
    {
        Description = TelemetryCommandStrings.HasErrorOptionDescription
    };

    /// <summary>
    /// Dashboard URL option for connecting directly to a standalone dashboard.
    /// </summary>
    internal static Option<string?> CreateDashboardUrlOption() => new("--dashboard-url")
    {
        Description = TelemetryCommandStrings.DashboardUrlOptionDescription
    };

    /// <summary>
    /// API key option for authenticating with a standalone dashboard.
    /// </summary>
    internal static Option<string?> CreateApiKeyOption() => new("--api-key")
    {
        Description = TelemetryCommandStrings.ApiKeyOptionDescription
    };

    #endregion

    /// <summary>
    /// Validates that an HTTP response has a JSON content type.
    /// </summary>
    /// <param name="response">The HTTP response to validate.</param>
    /// <returns>True if the response has a JSON content type; false otherwise.</returns>
    public static bool HasJsonContentType(HttpResponseMessage response)
    {
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        return mediaType is "application/json" or "text/json" or "application/x-ndjson";
    }

    /// <summary>
    /// Validates a telemetry API response by checking for conditions that indicate the API is not enabled,
    /// then ensuring a success status code and JSON content type.
    /// </summary>
    /// <param name="response">The HTTP response to validate.</param>
    /// <remarks>
    /// When the dashboard telemetry API is not enabled, requests may return a 404 status code
    /// or a 200 with text/html content (Blazor fallback route). In either case, this method throws
    /// an <see cref="HttpRequestException"/> with a <see cref="HttpStatusCode.NotFound"/> status code
    /// so that existing error handling can detect the condition and display an appropriate message.
    /// </remarks>
    /// <exception cref="HttpRequestException">
    /// Thrown when the response indicates the API is not enabled (404 or HTML content type),
    /// when the response has a non-success status code, or when the content type is not JSON.
    /// </exception>
    public static void EnsureTelemetryApiResponse(HttpResponseMessage response)
    {
        // A 200 with text/html content type indicates the Blazor fallback route handled the request,
        // meaning the telemetry API endpoint doesn't exist. Treat this the same as a 404.
        if (response.IsSuccessStatusCode &&
            response.Content.Headers.ContentType?.MediaType is "text/html")
        {
            throw new HttpRequestException(
                HttpRequestError.InvalidResponse,
                statusCode: HttpStatusCode.NotFound);
        }

        response.EnsureSuccessStatusCode();

        if (!HasJsonContentType(response))
        {
            var mediaType = response.Content.Headers.ContentType?.MediaType ?? "(none)";
            throw new HttpRequestException(
                HttpRequestError.InvalidResponse,
                string.Format(CultureInfo.InvariantCulture, TelemetryCommandStrings.UnexpectedContentType, mediaType),
                inner: null,
                response.StatusCode);
        }
    }

    /// <summary>
    /// Resolves an AppHost connection and gets Dashboard API info.
    /// </summary>
    /// <param name="connectionResolver">The connection resolver for AppHost discovery.</param>
    /// <param name="interactionService">The interaction service for displaying messages.</param>
    /// <param name="projectFile">The optional AppHost project file.</param>
    /// <param name="dashboardUrl">The optional direct dashboard URL (mutually exclusive with <paramref name="projectFile"/>).</param>
    /// <param name="apiKey">The optional API key for dashboard authentication.</param>
    /// <param name="requireDashboard">
    /// When <c>true</c>, a missing Dashboard API is a hard error.
    /// When <c>false</c>, a missing Dashboard API is non-fatal and the method returns success with <c>null</c> base URL and token.
    /// </param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A <see cref="DashboardApiResult"/> with the resolved connection and dashboard API info.</returns>
    public static async Task<DashboardApiResult> GetDashboardApiAsync(
        AppHostConnectionResolver connectionResolver,
        IInteractionService interactionService,
        FileInfo? projectFile,
        string? dashboardUrl,
        string? apiKey,
        bool requireDashboard,
        CancellationToken cancellationToken)
    {
        // Validate mutual exclusivity of --apphost and --dashboard-url
        if (projectFile is not null && dashboardUrl is not null)
        {
            interactionService.DisplayError(TelemetryCommandStrings.DashboardUrlAndAppHostExclusive);
            return DashboardApiResult.Failure(ExitCodeConstants.InvalidCommand);
        }

        // Direct dashboard URL mode — bypass AppHost discovery
        if (dashboardUrl is not null)
        {
            if (!UrlHelper.IsHttpUrl(dashboardUrl))
            {
                interactionService.DisplayError(string.Format(CultureInfo.CurrentCulture, TelemetryCommandStrings.DashboardUrlInvalid, dashboardUrl));
                return DashboardApiResult.Failure(ExitCodeConstants.InvalidCommand);
            }

            var token = apiKey ?? string.Empty;
            return new DashboardApiResult(true, null, dashboardUrl, token, dashboardUrl, 0);
        }

        var result = await connectionResolver.ResolveConnectionAsync(
            projectFile,
            SharedCommandStrings.ScanningForRunningAppHosts,
            string.Format(CultureInfo.CurrentCulture, SharedCommandStrings.SelectAppHost, TelemetryCommandStrings.SelectAppHostAction),
            SharedCommandStrings.AppHostNotRunning,
            cancellationToken);

        if (!result.Success)
        {
            interactionService.DisplayMessage(KnownEmojis.Information, result.ErrorMessage);
            return DashboardApiResult.Failure(ExitCodeConstants.Success);
        }

        var connection = result.Connection!;
        var dashboardInfo = await connection.GetDashboardInfoV2Async(cancellationToken);
        if (dashboardInfo?.ApiBaseUrl is null || dashboardInfo.ApiToken is null)
        {
            if (requireDashboard)
            {
                interactionService.DisplayError(TelemetryCommandStrings.DashboardApiNotAvailable);
                return DashboardApiResult.Failure(ExitCodeConstants.DashboardFailure);
            }

            // Dashboard is optional — return success with null API info
            return new DashboardApiResult(true, connection, null, null, null, 0);
        }

        // Extract dashboard base URL (without /login path) for hyperlinks
        var extractedDashboardUrl = ExtractDashboardBaseUrl(dashboardInfo.DashboardUrls?.FirstOrDefault());

        return new DashboardApiResult(true, connection, dashboardInfo.ApiBaseUrl, dashboardInfo.ApiToken, extractedDashboardUrl, 0);
    }

    /// <summary>
    /// Strips the /login path segment from a dashboard URL returned by the AppHost.
    /// </summary>
    internal static string? ExtractDashboardBaseUrl(string? dashboardUrlWithToken)
    {
        return McpToolHelpers.StripLoginPath(dashboardUrlWithToken);
    }

    /// <summary>
    /// Creates an HTTP client configured for Dashboard API access.
    /// </summary>
    public static HttpClient CreateApiClient(IHttpClientFactory factory, string apiToken)
    {
        var client = factory.CreateClient();
        if (!string.IsNullOrEmpty(apiToken))
        {
            client.DefaultRequestHeaders.Add(ApiKeyHeaderName, apiToken);
        }
        return client;
    }

    /// <summary>
    /// Formats an error message for a telemetry HTTP failure, using dashboard-specific diagnostics
    /// when a direct dashboard URL was provided, or a generic message otherwise.
    /// </summary>
    public static async Task<string> FormatTelemetryErrorMessageAsync(
        HttpRequestException ex,
        string baseUrl,
        bool dashboardOnly,
        IHttpClientFactory httpClientFactory,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (dashboardOnly)
        {
            return await GetDashboardApiErrorMessageAsync(ex, baseUrl, httpClientFactory, logger, cancellationToken);
        }

        return string.Format(CultureInfo.CurrentCulture, TelemetryCommandStrings.FailedToFetchTelemetry, ex.Message);
    }

    /// <summary>
    /// Produces a user-friendly error message for dashboard API failures when using --dashboard-url.
    /// </summary>
    public static async Task<string> GetDashboardApiErrorMessageAsync(
        HttpRequestException ex,
        string dashboardBaseUrl,
        IHttpClientFactory httpClientFactory,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (ex.StatusCode == HttpStatusCode.Unauthorized)
        {
            return TelemetryCommandStrings.DashboardAuthFailed;
        }

        if (ex.StatusCode == HttpStatusCode.NotFound)
        {
            // Probe the dashboard base URL to distinguish "wrong URL" from "API not enabled"
            try
            {
                using var probeClient = httpClientFactory.CreateClient();
                var probeResponse = await probeClient.GetAsync(dashboardBaseUrl, cancellationToken).ConfigureAwait(false);

                if (probeResponse.IsSuccessStatusCode)
                {
                    // Dashboard is reachable but the API endpoint returned 404 — API not enabled
                    return string.Format(CultureInfo.CurrentCulture, TelemetryCommandStrings.DashboardApiNotEnabled, dashboardBaseUrl);
                }
            }
            catch (Exception probeEx)
            {
                logger.LogDebug(probeEx, "Dashboard probe failed for {Url}", dashboardBaseUrl);
            }

            // Dashboard base URL is also not reachable — wrong URL
            return string.Format(CultureInfo.CurrentCulture, TelemetryCommandStrings.DashboardUrlNotReachable, dashboardBaseUrl);
        }

        if (ex.StatusCode is null)
        {
            // No HTTP status — connection refused or network error
            return string.Format(CultureInfo.CurrentCulture, TelemetryCommandStrings.DashboardConnectionFailed, dashboardBaseUrl);
        }

        return string.Format(CultureInfo.CurrentCulture, TelemetryCommandStrings.FailedToFetchTelemetry, ex.Message);
    }

    public static bool TryResolveResourceNames(
        string? resourceName,
        IList<ResourceInfoJson> resources,
        out List<string>? resolvedResources)
    {
        if (string.IsNullOrEmpty(resourceName))
        {
            // No filter - return true to indicate success
            resolvedResources = null;
            return true;
        }

        if (resources is null || resources.Count == 0)
        {
            resolvedResources = null;
            return false;
        }

        // First, try exact match on display name (full instance name like "catalogservice-abc123")
        var exactMatch = resources.FirstOrDefault(r =>
            string.Equals(r.GetCompositeName(), resourceName, StringComparison.OrdinalIgnoreCase));
        if (exactMatch is not null)
        {
            resolvedResources = [exactMatch.GetCompositeName()];
            return true;
        }

        // Then, try matching by base name to find all replicas
        var matchingReplicas = resources
            .Where(r => string.Equals(r.Name, resourceName, StringComparison.OrdinalIgnoreCase))
            .Select(r => r.GetCompositeName())
            .ToList();

        if (matchingReplicas.Count > 0)
        {
            resolvedResources = matchingReplicas;
            return true;
        }

        // No match found
        resolvedResources = null;
        return false;
    }

    public static async Task<ResourceInfoJson[]> GetAllResourcesAsync(HttpClient client, string baseUrl, CancellationToken cancellationToken)
    {
        var url = DashboardUrls.TelemetryResourcesApiUrl(baseUrl);
        var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
        EnsureTelemetryApiResponse(response);

        var resources = await response.Content.ReadFromJsonAsync(OtlpJsonSerializerContext.Default.ResourceInfoJsonArray, cancellationToken).ConfigureAwait(false) ?? [];

        // Sort resources by name for consistent ordering.
        Array.Sort(resources, (a, b) =>
        {
            var cmp = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            return cmp != 0 ? cmp : string.Compare(a.InstanceId, b.InstanceId, StringComparison.OrdinalIgnoreCase);
        });

        return resources;
    }

    /// <summary>
    /// Displays a "no data found" message with consistent styling.
    /// </summary>
    /// <param name="interactionService">The interaction service for output.</param>
    /// <param name="dataType">The type of data (e.g., "logs", "spans", "traces").</param>
    public static void DisplayNoData(IInteractionService interactionService, string dataType)
    {
        interactionService.DisplayMarkupLine($"[yellow]No {dataType} found[/]");
    }

    /// <summary>
    /// Creates a Spectre Console hyperlink markup for a trace detail in the Dashboard.
    /// </summary>
    /// <param name="dashboardUrl">The base dashboard URL.</param>
    /// <param name="traceId">The trace ID.</param>
    /// <param name="displayText">The text to display (defaults to shortened trace ID).</param>
    /// <returns>A Spectre markup string with hyperlink, or plain text if dashboardUrl is null.</returns>
    public static string FormatTraceLink(string? dashboardUrl, string traceId, string? displayText = null)
    {
        var text = displayText ?? OtlpHelpers.ToShortenedId(traceId);
        if (string.IsNullOrEmpty(dashboardUrl))
        {
            return text;
        }

        // Dashboard trace detail URL: /traces/detail/{traceId}
        var url = DashboardUrls.CombineUrl(dashboardUrl, DashboardUrls.TraceDetailUrl(traceId));
        return $"[link={url}]{text}[/]";
    }

    /// <summary>
    /// Formats a duration using the shared DurationFormatter.
    /// </summary>
    public static string FormatDuration(TimeSpan duration)
    {
        return DurationFormatter.FormatDuration(duration, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Gets abbreviated severity text for an OTLP severity number.
    /// OTLP severity numbers: 1-4=TRACE, 5-8=DEBUG, 9-12=INFO, 13-16=WARN, 17-20=ERROR, 21-24=FATAL
    /// </summary>
    public static string GetSeverityText(int? severityNumber)
    {
        return severityNumber switch
        {
            >= 21 => "CRIT",
            >= 17 => "FAIL",
            >= 13 => "WARN",
            >= 9 => "INFO",
            >= 5 => "DBUG",
            >= 1 => "TRCE",
            _ => "-"
        };
    }

    /// <summary>
    /// Gets Spectre Console color for a log severity number.
    /// OTLP severity numbers: 1-4=TRACE, 5-8=DEBUG, 9-12=INFO, 13-16=WARN, 17-20=ERROR, 21-24=FATAL
    /// </summary>
    public static Color GetSeverityColor(int? severityNumber)
    {
        return severityNumber switch
        {
            >= 17 => Color.Red,      // ERROR/FATAL
            >= 13 => Color.Yellow,   // WARN
            >= 9 => Color.Blue,      // INFO
            >= 5 => Color.Grey,      // DEBUG
            >= 1 => Color.Grey,      // TRACE
            _ => Color.White
        };
    }

    /// <summary>
    /// Reads lines from an HTTP streaming response, yielding each complete line as it arrives.
    /// </summary>
    public static async IAsyncEnumerable<string> ReadLinesAsync(
        this StreamReader reader,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                yield break;
            }

            if (!string.IsNullOrEmpty(line))
            {
                yield return line;
            }
        }
    }

    /// <summary>
    /// Converts an array of <see cref="ResourceInfoJson"/> to a list of <see cref="IOtlpResource"/> for use with <see cref="OtlpHelpers.GetResourceName"/>.
    /// </summary>
    public static IReadOnlyList<IOtlpResource> ToOtlpResources(ResourceInfoJson[] resources)
    {
        var result = new IOtlpResource[resources.Length];
        for (var i = 0; i < resources.Length; i++)
        {
            result[i] = new SimpleOtlpResource(resources[i].Name, resources[i].InstanceId);
        }
        return result;
    }

    /// <summary>
    /// Pre-resolves resource colors for all resources in sorted order so that
    /// color assignment is deterministic regardless of encounter order in telemetry data.
    /// </summary>
    public static void ResolveResourceColors(ResourceColorMap colorMap, IReadOnlyList<IOtlpResource> allResources)
    {
        colorMap.ResolveAll(allResources.Select(r => OtlpHelpers.GetResourceName(r, allResources)));
    }

    /// <summary>
    /// Resolves the display name for an OTLP resource using <see cref="OtlpHelpers.GetResourceName"/>,
    /// appending a shortened instance ID when there are replicas with the same base name.
    /// </summary>
    public static string ResolveResourceName(OtlpResourceJson? resource, IReadOnlyList<IOtlpResource> allResources)
    {
        if (resource is null)
        {
            return "unknown";
        }

        var otlpResource = new SimpleOtlpResource(resource.GetServiceName(), resource.GetServiceInstanceId());
        return OtlpHelpers.GetResourceName(otlpResource, allResources);
    }
}

/// <summary>
/// Result of resolving the Dashboard API connection via <see cref="TelemetryCommandHelpers.GetDashboardApiAsync"/>.
/// </summary>
/// <param name="Success">Whether the resolution succeeded.</param>
/// <param name="Connection">The AppHost backchannel connection, if resolved via an AppHost.</param>
/// <param name="BaseUrl">The Dashboard API base URL, or <c>null</c> if the dashboard is unavailable.</param>
/// <param name="ApiToken">The Dashboard API authentication token, or <c>null</c> if the dashboard is unavailable.</param>
/// <param name="DashboardUrl">The Dashboard UI base URL for hyperlinks, or <c>null</c> if unavailable.</param>
/// <param name="ExitCode">The exit code to return when <paramref name="Success"/> is <c>false</c>.</param>
internal sealed record DashboardApiResult(
    bool Success,
    IAppHostAuxiliaryBackchannel? Connection,
    string? BaseUrl,
    string? ApiToken,
    string? DashboardUrl,
    int ExitCode)
{
    /// <summary>
    /// Creates a failed result with the specified exit code.
    /// </summary>
    public static DashboardApiResult Failure(int exitCode)
        => new(false, null, null, null, null, exitCode);
}
