// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text.Encodings.Web;

namespace Aspire.Dashboard.Utils;

internal static class DashboardUrls
{
    public const string ResourcesBasePath = "";
    public const string ConsoleLogBasePath = "consolelogs";
    public const string MetricsBasePath = "metrics";
    public const string StructuredLogsBasePath = "structuredlogs";
    public const string TracesBasePath = "traces";
    public const string LoginBasePath = "login";
    public const string HealthBasePath = "health";

    public static string ResourcesUrl(string? resource = null, string? view = null, string? hiddenTypes = null, string? hiddenStates = null, string? hiddenHealthStates = null)
    {
        var url = $"/{ResourcesBasePath}";
        if (resource != null)
        {
            url = AddQueryString(url, "resource", resource);
        }
        if (view != null)
        {
            url = AddQueryString(url, "view", view);
        }
        if (hiddenTypes != null)
        {
            url = AddQueryString(url, "hiddenTypes", hiddenTypes);
        }
        if (hiddenStates != null)
        {
            url = AddQueryString(url, "hiddenStates", hiddenStates);
        }
        if (hiddenHealthStates != null)
        {
            url = AddQueryString(url, "hiddenHealthStates", hiddenHealthStates);
        }

        return url;
    }

    public static string ConsoleLogsUrl(string? resource = null)
    {
        var url = $"/{ConsoleLogBasePath}";
        if (resource != null)
        {
            url += $"/resource/{Uri.EscapeDataString(resource)}";
        }

        return url;
    }

    public static string MetricsUrl(string? resource = null, string? meter = null, string? instrument = null, int? duration = null, string? view = null)
    {
        var url = $"/{MetricsBasePath}";
        if (resource != null)
        {
            url += $"/resource/{Uri.EscapeDataString(resource)}";
        }
        if (meter is not null)
        {
            // Meter and instrument must be querystring parameters because it's valid for the name to contain forward slashes.
            url = AddQueryString(url, "meter", meter);
            if (instrument is not null)
            {
                url = AddQueryString(url, "instrument", instrument);
            }
        }
        if (duration != null)
        {
            url = AddQueryString(url, "duration", duration.Value.ToString(CultureInfo.InvariantCulture));
        }
        if (view != null)
        {
            url = AddQueryString(url, "view", view);
        }

        return url;
    }

    public static string StructuredLogsUrl(string? resource = null, string? logLevel = null, string? filters = null, string? traceId = null, string? spanId = null, long? logEntryId = null)
    {
        var url = $"/{StructuredLogsBasePath}";
        if (resource != null)
        {
            url += $"/resource/{Uri.EscapeDataString(resource)}";
        }
        if (logLevel != null)
        {
            url = AddQueryString(url, "logLevel", logLevel);
        }
        if (filters != null)
        {
            // Filters contains : and + characters. These are escaped when they're not needed to,
            // which makes the URL harder to read. Consider having a custom method for appending
            // query string here that uses an encoder that doesn't encode those characters.
            url = AddQueryString(url, "filters", filters);
        }
        if (traceId != null)
        {
            url = AddQueryString(url, "traceId", traceId);
        }
        if (spanId != null)
        {
            url = AddQueryString(url, "spanId", spanId);
        }
        if (logEntryId != null)
        {
            url = AddQueryString(url, "logEntryId", logEntryId.Value.ToString(CultureInfo.InvariantCulture));
        }

        return url;
    }

    public static string TracesUrl(string? resource = null, string? type = null, string? filters = null)
    {
        var url = $"/{TracesBasePath}";
        if (resource != null)
        {
            url += $"/resource/{Uri.EscapeDataString(resource)}";
        }
        if (type != null)
        {
            url = AddQueryString(url, "type", type);
        }
        if (filters != null)
        {
            // Filters contains : and + characters. These are escaped when they're not needed to,
            // which makes the URL harder to read. Consider having a custom method for appending
            // query string here that uses an encoder that doesn't encode those characters.
            url = AddQueryString(url, "filters", filters);
        }

        return url;
    }

    public static string TraceDetailUrl(string traceId, string? spanId = null)
    {
        var url = $"/{TracesBasePath}/detail/{Uri.EscapeDataString(traceId)}";
        if (spanId != null)
        {
            url = AddQueryString(url, "spanId", spanId);
        }

        return url;
    }

    public static string LoginUrl(string? returnUrl = null, string? token = null)
    {
        var url = $"/{LoginBasePath}";
        if (returnUrl != null)
        {
            url = AddQueryString(url, "returnUrl", returnUrl);
        }
        if (token != null)
        {
            url = AddQueryString(url, "t", token);
        }

        return url;
    }

    public static string SetLanguageUrl(string language, string redirectUrl)
    {
        var url = "/api/set-language";
        url = AddQueryString(url, "language", language);
        url = AddQueryString(url, "redirectUrl", redirectUrl);

        return url;
    }

    /// <summary>
    /// Combines a base URL with a path.
    /// </summary>
    /// <param name="baseUrl">The base URL (e.g., "https://localhost:5000").</param>
    /// <param name="path">The path (e.g., "/?resource=myapp").</param>
    /// <returns>The combined URL.</returns>
    public static string CombineUrl(string baseUrl, string path)
    {
        // Remove trailing slash from base URL and leading slash from path to avoid double slashes
        var trimmedBase = baseUrl.TrimEnd('/');
        var trimmedPath = path.TrimStart('/');

        return $"{trimmedBase}/{trimmedPath}";
    }

    #region Telemetry API URLs

    private const string TelemetryApiBasePath = "api/telemetry";

    /// <summary>
    /// Builds the URL for the telemetry logs API with resource filtering.
    /// </summary>
    /// <param name="baseUrl">The dashboard base URL.</param>
    /// <param name="resources">Optional list of resource names to filter by.</param>
    /// <param name="additionalParams">Additional query parameters.</param>
    /// <returns>The full API URL.</returns>
    public static string TelemetryLogsApiUrl(string baseUrl, List<string>? resources = null, params (string key, string? value)[] additionalParams)
    {
        var queryString = BuildResourceQueryString(resources, additionalParams);
        return CombineUrl(baseUrl, $"{TelemetryApiBasePath}/logs{queryString}");
    }

    /// <summary>
    /// Builds the URL for the telemetry spans API with resource filtering.
    /// </summary>
    /// <param name="baseUrl">The dashboard base URL.</param>
    /// <param name="resources">Optional list of resource names to filter by.</param>
    /// <param name="additionalParams">Additional query parameters.</param>
    /// <returns>The full API URL.</returns>
    public static string TelemetrySpansApiUrl(string baseUrl, List<string>? resources = null, params (string key, string? value)[] additionalParams)
    {
        var queryString = BuildResourceQueryString(resources, additionalParams);
        return CombineUrl(baseUrl, $"{TelemetryApiBasePath}/spans{queryString}");
    }

    /// <summary>
    /// Builds the URL for the telemetry traces API with resource filtering.
    /// </summary>
    /// <param name="baseUrl">The dashboard base URL.</param>
    /// <param name="resources">Optional list of resource names to filter by.</param>
    /// <param name="additionalParams">Additional query parameters.</param>
    /// <returns>The full API URL.</returns>
    public static string TelemetryTracesApiUrl(string baseUrl, List<string>? resources = null, params (string key, string? value)[] additionalParams)
    {
        var queryString = BuildResourceQueryString(resources, additionalParams);
        return CombineUrl(baseUrl, $"{TelemetryApiBasePath}/traces{queryString}");
    }

    /// <summary>
    /// Builds the URL for a specific trace in the telemetry API.
    /// </summary>
    /// <param name="baseUrl">The dashboard base URL.</param>
    /// <param name="traceId">The trace ID.</param>
    /// <returns>The full API URL.</returns>
    public static string TelemetryTraceDetailApiUrl(string baseUrl, string traceId)
    {
        var path = $"/{TelemetryApiBasePath}/traces/{Uri.EscapeDataString(traceId)}";
        return CombineUrl(baseUrl, path);
    }

    /// <summary>
    /// Builds the URL for the telemetry resources API endpoint.
    /// </summary>
    /// <param name="baseUrl">The dashboard base URL.</param>
    /// <returns>The full API URL.</returns>
    public static string TelemetryResourcesApiUrl(string baseUrl)
    {
        var path = $"/{TelemetryApiBasePath}/resources";
        return CombineUrl(baseUrl, path);
    }

    /// <summary>
    /// Builds a query string with multiple resource parameters and optional additional parameters.
    /// </summary>
    internal static string BuildResourceQueryString(
        List<string>? resources,
        params (string key, string? value)[] additionalParams)
    {
        var parts = new List<string>();

        // Add each resource as a separate query parameter
        if (resources is not null)
        {
            foreach (var resource in resources)
            {
                parts.Add($"resource={Uri.EscapeDataString(resource)}");
            }
        }

        // Add additional parameters
        foreach (var (key, value) in additionalParams)
        {
            if (!string.IsNullOrEmpty(value))
            {
                parts.Add($"{key}={Uri.EscapeDataString(value)}");
            }
        }

        return parts.Count > 0 ? "?" + string.Join("&", parts) : "";
    }

    #endregion

    /// <summary>
    /// Adds a query string parameter to a URL.
    /// This implementation matches the behavior of QueryHelpers.AddQueryString from ASP.NET Core,
    /// which uses UrlEncoder.Default that doesn't encode certain characters like ! and @.
    /// </summary>
    private static string AddQueryString(string url, string name, string value)
    {
        var separator = url.Contains('?') ? '&' : '?';
        return $"{url}{separator}{UrlEncoder.Default.Encode(name)}={UrlEncoder.Default.Encode(value)}";
    }
}
