// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Aspire.Dashboard.Backend;

internal sealed record DashboardRequestCredentials(string? Cookie, string? Authorization)
{
    public static DashboardRequestCredentials From(HttpRequest request) => new(
        request.Headers.Cookie,
        request.Headers.Authorization);
}

internal interface IDashboardStructuredLogSource
{
    ValueTask<DashboardStructuredLogsSnapshot> GetSnapshotAsync(
        DashboardRequestCredentials credentials,
        CancellationToken cancellationToken);

    IAsyncEnumerable<DashboardStructuredLogsEvent> WatchAsync(
        DashboardRequestCredentials credentials,
        CancellationToken cancellationToken);
}

internal sealed class DashboardStructuredLogServiceUnavailableException(string message, Exception? innerException = null)
    : Exception(message, innerException);

internal sealed class DashboardStructuredLogProxy(IConfiguration configuration) : IDashboardStructuredLogSource
{
    private const string LegacyDashboardUrlKey = "DashboardBackend:LegacyDashboardUrl";
    private static readonly HttpClient s_client = new(new SocketsHttpHandler { UseCookies = false });

    public async ValueTask<DashboardStructuredLogsSnapshot> GetSnapshotAsync(
        DashboardRequestCredentials credentials,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, "api/deck/telemetry/logs?limit=5000", credentials);
        using var response = await SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        using var content = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(content, cancellationToken: cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        if (!root.TryGetProperty("totalCount", out var totalCount)
            || !totalCount.TryGetInt32(out var count)
            || !root.TryGetProperty("data", out var data))
        {
            throw new DashboardStructuredLogServiceUnavailableException("The legacy dashboard returned an incompatible structured-log snapshot.");
        }

        return new DashboardStructuredLogsSnapshot(count, data.Clone());
    }

    public async IAsyncEnumerable<DashboardStructuredLogsEvent> WatchAsync(
        DashboardRequestCredentials credentials,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, "api/deck/telemetry/logs?follow=true", credentials);
        request.Headers.Accept.ParseAdd("application/x-ndjson");
        using var response = await SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        using var content = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(content);

        // The legacy endpoint emits one complete OTLP JSON object per line:
        //   {"resourceLogs":[{"resource":{...},"scopeLogs":[...]}]}
        // Messages contain escaped newlines inside JSON strings, so physical line boundaries
        // remain safe NDJSON record delimiters.
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            using var document = JsonDocument.Parse(line);
            yield return new DashboardStructuredLogsEvent(document.RootElement.Clone());
        }
    }

    private Uri GetLegacyDashboardUrl()
    {
        var configuredUrl = configuration[LegacyDashboardUrlKey];
        if (!Uri.TryCreate(configuredUrl, UriKind.Absolute, out var legacyDashboardUrl)
            || !DashboardDevelopmentAccessPolicy.IsAllowedOrigin(legacyDashboardUrl.GetLeftPart(UriPartial.Authority)))
        {
            throw new DashboardStructuredLogServiceUnavailableException(
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
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await s_client.SendAsync(request, completionOption, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                response.Dispose();
                throw new DashboardStructuredLogServiceUnavailableException(
                    $"The legacy dashboard structured-log endpoint returned {(int)response.StatusCode} {response.ReasonPhrase}.");
            }

            return response;
        }
        catch (DashboardStructuredLogServiceUnavailableException)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            throw new DashboardStructuredLogServiceUnavailableException(
                "The legacy dashboard structured-log endpoint is unavailable.",
                ex);
        }
    }
}
