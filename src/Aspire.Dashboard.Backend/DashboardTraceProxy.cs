// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Extensions;

namespace Aspire.Dashboard.Backend;

internal sealed record DashboardTraceQuery(
    string[] ResourceNames,
    string? TraceId,
    bool? HasError,
    int? Limit,
    string? Search);

internal interface IDashboardTraceSource
{
    ValueTask<DashboardTraceSnapshot?> GetSnapshotAsync(
        DashboardTraceQuery query,
        DashboardRequestCredentials credentials,
        CancellationToken cancellationToken);

    IAsyncEnumerable<DashboardTraceEvent> WatchAsync(
        DashboardTraceQuery query,
        DashboardRequestCredentials credentials,
        CancellationToken cancellationToken);

    ValueTask<bool> ClearAsync(
        string? resourceName,
        DashboardRequestCredentials credentials,
        CancellationToken cancellationToken);
}

internal sealed class DashboardTraceServiceUnavailableException(string message, Exception? innerException = null)
    : Exception(message, innerException);

internal sealed class DashboardTraceProxy(IConfiguration configuration) : IDashboardTraceSource
{
    private const string LegacyDashboardUrlKey = "DashboardBackend:LegacyDashboardUrl";
    private static readonly HttpClient s_client = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false
    });

    public async ValueTask<DashboardTraceSnapshot?> GetSnapshotAsync(
        DashboardTraceQuery query,
        DashboardRequestCredentials credentials,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, BuildPath(query, follow: false), credentials);
        using var response = await SendAsync(
            request,
            HttpCompletionOption.ResponseContentRead,
            allowNotFound: true,
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        using var content = await OpenContentStreamAsync(response, cancellationToken).ConfigureAwait(false);
        return await ParseSnapshotAsync(content, cancellationToken).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<DashboardTraceEvent> WatchAsync(
        DashboardTraceQuery query,
        DashboardRequestCredentials credentials,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, BuildPath(query, follow: true), credentials);
        request.Headers.Accept.Clear();
        request.Headers.Accept.ParseAdd("application/x-ndjson");
        using var response = await SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            allowNotFound: false,
            cancellationToken).ConfigureAwait(false);
        using var content = await OpenContentStreamAsync(response, cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(content);

        // The legacy span watcher emits one complete OTLP JSON object per physical line:
        //   {"resourceSpans":[{"resource":{...},"scopeSpans":[{"spans":[...]}]}]}
        // It registers its bounded watcher before reading the initial snapshot, so these
        // lines preserve chronological backlog-to-live ordering without a race at handoff.
        while (await ReadLineAsync(reader, cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            yield return ParseTraceEvent(line);
        }
    }

    public async ValueTask<bool> ClearAsync(
        string? resourceName,
        DashboardRequestCredentials credentials,
        CancellationToken cancellationToken)
    {
        var path = "api/deck/telemetry/spans";
        if (!string.IsNullOrWhiteSpace(resourceName))
        {
            path += QueryString.Create("resource", resourceName);
        }

        using var request = CreateRequest(HttpMethod.Delete, path, credentials);
        using var response = await SendAsync(
            request,
            HttpCompletionOption.ResponseContentRead,
            allowNotFound: true,
            cancellationToken).ConfigureAwait(false);
        return response.StatusCode is not System.Net.HttpStatusCode.NotFound;
    }

    private static string BuildPath(DashboardTraceQuery query, bool follow)
    {
        var builder = new QueryBuilder();
        foreach (var resourceName in query.ResourceNames)
        {
            builder.Add("resource", resourceName);
        }
        if (!string.IsNullOrWhiteSpace(query.TraceId))
        {
            builder.Add("traceId", query.TraceId);
        }
        if (query.HasError is { } hasError)
        {
            builder.Add("hasError", hasError.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        if (!follow && query.Limit is { } limit)
        {
            builder.Add("limit", limit.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        if (follow)
        {
            builder.Add("follow", "true");
        }
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            builder.Add("search", query.Search);
        }

        return $"api/deck/telemetry/spans{builder.ToQueryString()}";
    }

    private Uri GetLegacyDashboardUrl()
    {
        var configuredUrl = configuration[LegacyDashboardUrlKey];
        if (!Uri.TryCreate(configuredUrl, UriKind.Absolute, out var legacyDashboardUrl)
            || !DashboardDevelopmentAccessPolicy.IsLoopbackTarget(legacyDashboardUrl.GetLeftPart(UriPartial.Authority)))
        {
            throw new DashboardTraceServiceUnavailableException(
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
        HttpCompletionOption completionOption,
        bool allowNotFound,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await s_client.SendAsync(request, completionOption, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode
                && !(allowNotFound && response.StatusCode is System.Net.HttpStatusCode.NotFound))
            {
                response.Dispose();
                throw new DashboardTraceServiceUnavailableException(
                    $"The legacy dashboard trace endpoint returned {(int)response.StatusCode} {response.ReasonPhrase}.");
            }

            return response;
        }
        catch (DashboardTraceServiceUnavailableException)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            throw new DashboardTraceServiceUnavailableException(
                "The legacy dashboard trace endpoint is unavailable.",
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
            throw new DashboardTraceServiceUnavailableException(
                "The legacy dashboard trace endpoint is unavailable.",
                ex);
        }
    }

    private static async ValueTask<DashboardTraceSnapshot> ParseSnapshotAsync(
        Stream content,
        CancellationToken cancellationToken)
    {
        try
        {
            using var document = await JsonDocument.ParseAsync(
                content,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            var root = document.RootElement;
            if (!root.TryGetProperty("totalCount", out var totalCount)
                || !totalCount.TryGetInt32(out var count)
                || !root.TryGetProperty("returnedCount", out var returnedCount)
                || !returnedCount.TryGetInt32(out var returned)
                || !root.TryGetProperty("data", out var data))
            {
                throw new DashboardTraceServiceUnavailableException(
                    "The legacy dashboard returned an incompatible trace snapshot.");
            }

            return new DashboardTraceSnapshot(count, returned, data.Clone());
        }
        catch (JsonException ex)
        {
            throw new DashboardTraceServiceUnavailableException(
                "The legacy dashboard returned an incompatible trace snapshot.",
                ex);
        }
    }

    private static async ValueTask<string?> ReadLineAsync(
        StreamReader reader,
        CancellationToken cancellationToken)
    {
        try
        {
            return await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            throw new DashboardTraceServiceUnavailableException(
                "The legacy dashboard trace stream ended unexpectedly.",
                ex);
        }
    }

    private static DashboardTraceEvent ParseTraceEvent(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            return new DashboardTraceEvent(document.RootElement.Clone());
        }
        catch (JsonException ex)
        {
            throw new DashboardTraceServiceUnavailableException(
                "The legacy dashboard returned an incompatible trace event.",
                ex);
        }
    }
}
