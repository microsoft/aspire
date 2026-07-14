// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Aspire.Dashboard.Backend;

internal interface IDashboardConsoleLogSource
{
    IAsyncEnumerable<DashboardConsoleLogsEvent> WatchAsync(
        string resourceName,
        DashboardRequestCredentials credentials,
        CancellationToken cancellationToken);
}

internal sealed class DashboardConsoleLogProxy(IConfiguration configuration) : IDashboardConsoleLogSource
{
    private const string LegacyDashboardUrlKey = "DashboardBackend:LegacyDashboardUrl";
    private static readonly HttpClient s_client = new(new SocketsHttpHandler { UseCookies = false });

    public async IAsyncEnumerable<DashboardConsoleLogsEvent> WatchAsync(
        string resourceName,
        DashboardRequestCredentials credentials,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var legacyDashboardUrl = GetLegacyDashboardUrl();
        var path = $"api/deck/resources/{Uri.EscapeDataString(resourceName)}/console-logs";
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(legacyDashboardUrl, path));
        request.Headers.Accept.ParseAdd("application/x-ndjson");
        if (!string.IsNullOrEmpty(credentials.Cookie))
        {
            request.Headers.TryAddWithoutValidation("Cookie", credentials.Cookie);
        }
        if (!string.IsNullOrEmpty(credentials.Authorization))
        {
            request.Headers.TryAddWithoutValidation("Authorization", credentials.Authorization);
        }

        using var response = await s_client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var content = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(content);

        // The legacy endpoint writes one complete event per physical line. A batch contains
        // the initial resource backlog before it remains open for live events:
        //   {"resourceName":"api","lines":[{"lineNumber":1,"text":"started","isStdErr":false}]}
        // ReadLineAsync retains an incomplete final record until its newline arrives, so a
        // transport read boundary can never expose a partial JSON event.
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var logEvent = JsonSerializer.Deserialize(
                line,
                DashboardBackendJsonSerializerContext.Default.DashboardConsoleLogsEvent);
            if (logEvent is null || !string.Equals(logEvent.ResourceName, resourceName, StringComparison.Ordinal))
            {
                throw new InvalidDataException("The legacy dashboard returned an incompatible console-log event.");
            }

            yield return logEvent;
        }
    }

    private Uri GetLegacyDashboardUrl()
    {
        var configuredUrl = configuration[LegacyDashboardUrlKey];
        if (!Uri.TryCreate(configuredUrl, UriKind.Absolute, out var legacyDashboardUrl)
            || !DashboardDevelopmentAccessPolicy.IsAllowedOrigin(legacyDashboardUrl.GetLeftPart(UriPartial.Authority)))
        {
            throw new InvalidOperationException(
                $"Configure {LegacyDashboardUrlKey} with the loopback URL of the existing dashboard.");
        }

        return legacyDashboardUrl;
    }
}
