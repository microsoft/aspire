// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Microsoft.AspNetCore.Http.Extensions;

namespace Aspire.Dashboard.Backend;

internal sealed record DashboardMetricSeriesQuery(
    string ResourceName,
    string MeterName,
    string InstrumentName,
    int? WindowSeconds,
    int? MaxPoints,
    bool? ShowCount,
    string? HistogramMode,
    IReadOnlyDictionary<string, string?[]> Dimensions);

internal interface IDashboardMetricSource
{
    ValueTask<DashboardMetricSummary[]> GetSummariesAsync(
        DashboardRequestCredentials credentials,
        CancellationToken cancellationToken);

    ValueTask<DashboardMetricSeriesResponse?> GetSeriesAsync(
        DashboardMetricSeriesQuery query,
        DashboardRequestCredentials credentials,
        CancellationToken cancellationToken);

    ValueTask<bool> ClearAsync(
        string? resourceName,
        DashboardRequestCredentials credentials,
        CancellationToken cancellationToken);
}

internal sealed class DashboardMetricServiceUnavailableException(string message, Exception? innerException = null)
    : Exception(message, innerException);

internal sealed class DashboardMetricProxy(IConfiguration configuration) : IDashboardMetricSource
{
    private const string LegacyDashboardUrlKey = "DashboardBackend:LegacyDashboardUrl";
    private static readonly HttpClient s_client = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false
    });

    public async ValueTask<DashboardMetricSummary[]> GetSummariesAsync(
        DashboardRequestCredentials credentials,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, "api/deck/telemetry/metrics", credentials);
        using var response = await SendAsync(
            request,
            allowNotFound: false,
            cancellationToken).ConfigureAwait(false);
        using var content = await OpenContentStreamAsync(response, cancellationToken).ConfigureAwait(false);
        try
        {
            return await JsonSerializer.DeserializeAsync(
                content,
                DashboardBackendJsonSerializerContext.Default.DashboardMetricSummaryArray,
                cancellationToken).ConfigureAwait(false)
                ?? throw new DashboardMetricServiceUnavailableException(
                    "The legacy dashboard returned an incompatible metric summary.");
        }
        catch (JsonException ex)
        {
            throw new DashboardMetricServiceUnavailableException(
                "The legacy dashboard returned an incompatible metric summary.",
                ex);
        }
    }

    public async ValueTask<DashboardMetricSeriesResponse?> GetSeriesAsync(
        DashboardMetricSeriesQuery query,
        DashboardRequestCredentials credentials,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, BuildSeriesPath(query), credentials);
        using var response = await SendAsync(
            request,
            allowNotFound: true,
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        using var content = await OpenContentStreamAsync(response, cancellationToken).ConfigureAwait(false);
        try
        {
            return await JsonSerializer.DeserializeAsync(
                content,
                DashboardBackendJsonSerializerContext.Default.DashboardMetricSeriesResponse,
                cancellationToken).ConfigureAwait(false)
                ?? throw new DashboardMetricServiceUnavailableException(
                    "The legacy dashboard returned an incompatible metric series.");
        }
        catch (JsonException ex)
        {
            throw new DashboardMetricServiceUnavailableException(
                "The legacy dashboard returned an incompatible metric series.",
                ex);
        }
    }

    public async ValueTask<bool> ClearAsync(
        string? resourceName,
        DashboardRequestCredentials credentials,
        CancellationToken cancellationToken)
    {
        var path = "api/deck/telemetry/metrics";
        if (!string.IsNullOrWhiteSpace(resourceName))
        {
            path += QueryString.Create("resource", resourceName);
        }

        using var request = CreateRequest(HttpMethod.Delete, path, credentials);
        using var response = await SendAsync(
            request,
            allowNotFound: true,
            cancellationToken).ConfigureAwait(false);
        return response.StatusCode is not System.Net.HttpStatusCode.NotFound;
    }

    private static string BuildSeriesPath(DashboardMetricSeriesQuery query)
    {
        var builder = new QueryBuilder
        {
            { "resource", query.ResourceName },
            { "meter", query.MeterName },
            { "instrument", query.InstrumentName }
        };
        if (query.WindowSeconds is { } windowSeconds)
        {
            builder.Add("windowSeconds", windowSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        if (query.MaxPoints is { } maxPoints)
        {
            builder.Add("maxPoints", maxPoints.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        if (query.ShowCount is { } showCount)
        {
            builder.Add("showCount", showCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        if (!string.IsNullOrWhiteSpace(query.HistogramMode))
        {
            builder.Add("histogramMode", query.HistogramMode);
        }
        foreach (var (name, values) in query.Dimensions.OrderBy(static item => item.Key, StringComparer.Ordinal))
        {
            if (values.Length is 0)
            {
                builder.Add($"dimension.{name}", "x:");
                continue;
            }

            foreach (var value in values)
            {
                builder.Add($"dimension.{name}", value is null ? "n:" : $"s:{value}");
            }
        }

        return $"api/deck/telemetry/metrics/series{builder.ToQueryString()}";
    }

    private Uri GetLegacyDashboardUrl()
    {
        var configuredUrl = configuration[LegacyDashboardUrlKey];
        if (!Uri.TryCreate(configuredUrl, UriKind.Absolute, out var legacyDashboardUrl)
            || !DashboardDevelopmentAccessPolicy.IsAllowedOrigin(legacyDashboardUrl.GetLeftPart(UriPartial.Authority)))
        {
            throw new DashboardMetricServiceUnavailableException(
                $"Configure {LegacyDashboardUrlKey} with the loopback URL of the existing dashboard.");
        }

        return legacyDashboardUrl;
    }

    private HttpRequestMessage CreateRequest(
        HttpMethod method,
        string path,
        DashboardRequestCredentials credentials)
    {
        var request = new HttpRequestMessage(method, new Uri(GetLegacyDashboardUrl(), path));
        request.Headers.Accept.ParseAdd("application/json");
        if (!string.IsNullOrEmpty(credentials.Cookie))
        {
            request.Headers.TryAddWithoutValidation("Cookie", credentials.Cookie);
        }
        if (!string.IsNullOrEmpty(credentials.Authorization))
        {
            request.Headers.TryAddWithoutValidation("Authorization", credentials.Authorization);
        }

        return request;
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        bool allowNotFound,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await s_client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode
                && !(allowNotFound && response.StatusCode is System.Net.HttpStatusCode.NotFound))
            {
                response.Dispose();
                throw new DashboardMetricServiceUnavailableException(
                    $"The legacy dashboard metric endpoint returned {(int)response.StatusCode} {response.ReasonPhrase}.");
            }

            return response;
        }
        catch (DashboardMetricServiceUnavailableException)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            throw new DashboardMetricServiceUnavailableException(
                "The legacy dashboard metric endpoint is unavailable.",
                ex);
        }
    }

    private static async ValueTask<Stream> OpenContentStreamAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            throw new DashboardMetricServiceUnavailableException(
                "The legacy dashboard metric endpoint is unavailable.",
                ex);
        }
    }
}
