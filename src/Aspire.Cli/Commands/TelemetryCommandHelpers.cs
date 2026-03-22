// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.Globalization;
using System.Net.Http.Json;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Interaction;
using Aspire.Cli.Resources;
using Aspire.Cli.Utils;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Utils;
using Aspire.Otlp.Serialization;
using Aspire.Shared;
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
    /// Resolves an AppHost connection and gets Dashboard API info.
    /// </summary>
    /// <returns>A tuple with success status, base URL, API token, dashboard UI URL, and exit code if failed.</returns>
    public static async Task<(bool Success, string? BaseUrl, string? ApiToken, string? DashboardUrl, int ExitCode)> GetDashboardApiAsync(
        AppHostConnectionResolver connectionResolver,
        IInteractionService interactionService,
        FileInfo? projectFile,
        CancellationToken cancellationToken)
    {
        var result = await connectionResolver.ResolveConnectionAsync(
            projectFile,
            SharedCommandStrings.ScanningForRunningAppHosts,
            string.Format(CultureInfo.CurrentCulture, SharedCommandStrings.SelectAppHost, TelemetryCommandStrings.SelectAppHostAction),
            SharedCommandStrings.AppHostNotRunning,
            cancellationToken);

        if (!result.Success)
        {
            interactionService.DisplayMessage(KnownEmojis.Information, result.ErrorMessage);
            return (false, null, null, null, ExitCodeConstants.Success);
        }

        var dashboardInfo = await result.Connection!.GetDashboardInfoV2Async(cancellationToken);
        if (dashboardInfo?.ApiBaseUrl is null || dashboardInfo.ApiToken is null)
        {
            interactionService.DisplayError(TelemetryCommandStrings.DashboardApiNotAvailable);
            return (false, null, null, null, ExitCodeConstants.DashboardFailure);
        }

        // Extract dashboard base URL (without /login path) for hyperlinks
        var dashboardUrl = ExtractDashboardBaseUrl(dashboardInfo.DashboardUrls?.FirstOrDefault());

        return (true, dashboardInfo.ApiBaseUrl, dashboardInfo.ApiToken, dashboardUrl, 0);
    }

    /// <summary>
    /// Extracts the base URL from a dashboard URL (removes /login?t=... path).
    /// </summary>
    private static string? ExtractDashboardBaseUrl(string? dashboardUrlWithToken)
    {
        if (string.IsNullOrEmpty(dashboardUrlWithToken))
        {
            return null;
        }

        // Dashboard URLs look like: http://localhost:18888/login?t=abcd1234
        // We want: http://localhost:18888
        var uri = new Uri(dashboardUrlWithToken);
        return $"{uri.Scheme}://{uri.Authority}";
    }

    /// <summary>
    /// Creates an HTTP client configured for Dashboard API access.
    /// </summary>
    public static HttpClient CreateApiClient(IHttpClientFactory factory, string apiToken)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(ApiKeyHeaderName, apiToken);
        return client;
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
        response.EnsureSuccessStatusCode();

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
