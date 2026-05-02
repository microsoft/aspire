// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aspire.Hosting.Browsers.Resources;

namespace Aspire.Hosting;

internal interface ISafariWebDriverClient : IDisposable
{
    Task<SafariWebDriverSession> CreateSessionAsync(Task<BrowserLogsProcessResult> driverProcessTask, CancellationToken cancellationToken);

    Task DeleteSessionAsync(string sessionId, CancellationToken cancellationToken);
}

// Minimal W3C WebDriver client used only to bootstrap Safari's WebDriver BiDi endpoint.
//
// References:
// - New Session: https://w3c.github.io/webdriver/#new-session
// - WebDriver BiDi webSocketUrl capability: https://w3c.github.io/webdriver-bidi/#webdriver-bidi
// - Safari WebDriver setup: https://developer.apple.com/documentation/webkit/testing-with-webdriver-in-safari
internal sealed class SafariWebDriverClient(Uri endpoint, string driverPath, HttpClient? httpClient = null) : ISafariWebDriverClient
{
    private static readonly TimeSpan s_sessionCreationTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan s_sessionCreationRetryDelay = TimeSpan.FromMilliseconds(100);

    private readonly string _driverPath = driverPath;
    private readonly Uri _endpoint = endpoint;
    private readonly HttpClient _httpClient = httpClient ?? new HttpClient();
    private readonly bool _ownsHttpClient = httpClient is null;

    public async Task<SafariWebDriverSession> CreateSessionAsync(Task<BrowserLogsProcessResult> driverProcessTask, CancellationToken cancellationToken)
    {
        // New Session request shape:
        // {
        //   "capabilities": {
        //     "alwaysMatch": {
        //       "browserName": "safari",
        //       "acceptInsecureCerts": true,
        //       "webSocketUrl": true
        //     }
        //   }
        // }
        //
        // The webSocketUrl capability is the WebDriver BiDi opt-in. Classic WebDriver alone can navigate and capture
        // screenshots, but BrowserLogs needs the event stream exposed over the returned BiDi websocket.
        var request = new SafariWebDriverNewSessionRequest(
            new SafariWebDriverCapabilities(
                new SafariWebDriverAlwaysMatchCapabilities(
                    BrowserName: "safari",
                    AcceptInsecureCerts: true,
                    WebSocketUrl: true)));

        using var response = await SendNewSessionRequestAsync(request, driverProcessTask, cancellationToken).ConfigureAwait(false);
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        // New Session success response shape:
        // {
        //   "value": {
        //     "sessionId": "webdriver-session-id",
        //     "capabilities": {
        //       "browserName": "safari",
        //       "webSocketUrl": "ws://127.0.0.1:port/session/..."
        //     }
        //   }
        // }
        //
        // Error responses use the same outer "value" object with "error" and "message" fields. If safaridriver accepts
        // the session but omits webSocketUrl, the installed Safari/WebKit build is not useful for BrowserLogs telemetry.
        var sessionResponse = await JsonSerializer.DeserializeAsync(stream, SafariWebDriverJsonContext.Default.SafariWebDriverNewSessionResponse, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException(CreateSessionFailedMessage());

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(CreateSessionFailedMessage(sessionResponse.Value?.Message));
        }

        var sessionId = sessionResponse.Value?.SessionId;
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new InvalidOperationException(CreateSessionFailedMessage("The WebDriver response did not include a session id."));
        }

        var webSocketUrl = sessionResponse.Value?.Capabilities?.WebSocketUrl;
        if (string.IsNullOrWhiteSpace(webSocketUrl) ||
            !Uri.TryCreate(webSocketUrl, UriKind.Absolute, out var webSocketUri))
        {
            throw new InvalidOperationException(string.Format(CultureInfo.CurrentCulture, BrowserMessageStrings.BrowserLogsSafariBiDiNotSupported, _driverPath));
        }

        return new SafariWebDriverSession(sessionId, webSocketUri);
    }

    public async Task DeleteSessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        using var request = new HttpRequestMessage(HttpMethod.Delete, new Uri(_endpoint, $"session/{Uri.EscapeDataString(sessionId)}"));
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var message = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException(CreateSessionFailedMessage($"Deleting the WebDriver session failed with HTTP {(int)response.StatusCode}: {message}"));
        }
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    private async Task<HttpResponseMessage> SendNewSessionRequestAsync(SafariWebDriverNewSessionRequest request, Task<BrowserLogsProcessResult> driverProcessTask, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + s_sessionCreationTimeout;
        Exception? lastError = null;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (driverProcessTask.IsCompleted)
            {
                throw new InvalidOperationException(CreateSessionFailedMessage("The safaridriver process exited before accepting a WebDriver session."), lastError);
            }

            try
            {
                // Keep serialization through the source-generated context so the JSON property names match the W3C
                // WebDriver capability names exactly, including the camel-cased "webSocketUrl" extension capability.
                var json = JsonSerializer.Serialize(request, SafariWebDriverJsonContext.Default.SafariWebDriverNewSessionRequest);
                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                return await _httpClient.PostAsync(new Uri(_endpoint, "session"), content, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException ex) when (DateTimeOffset.UtcNow < deadline)
            {
                lastError = ex;
            }
            catch (IOException ex) when (DateTimeOffset.UtcNow < deadline)
            {
                lastError = ex;
            }

            await Task.Delay(s_sessionCreationRetryDelay, cancellationToken).ConfigureAwait(false);
        }
    }

    private string CreateSessionFailedMessage(string? detail = null)
    {
        var message = string.Format(CultureInfo.CurrentCulture, BrowserMessageStrings.BrowserLogsSafariWebDriverSessionFailed, _driverPath);
        return string.IsNullOrWhiteSpace(detail)
            ? message
            : $"{message} {detail}";
    }
}

internal sealed record SafariWebDriverSession(string SessionId, Uri WebSocketUrl);

internal sealed record SafariWebDriverNewSessionRequest(
    [property: JsonPropertyName("capabilities")] SafariWebDriverCapabilities Capabilities);

internal sealed record SafariWebDriverCapabilities(
    [property: JsonPropertyName("alwaysMatch")] SafariWebDriverAlwaysMatchCapabilities AlwaysMatch);

internal sealed record SafariWebDriverAlwaysMatchCapabilities(
    [property: JsonPropertyName("browserName")] string BrowserName,
    [property: JsonPropertyName("acceptInsecureCerts")] bool AcceptInsecureCerts,
    [property: JsonPropertyName("webSocketUrl")] bool WebSocketUrl);

internal sealed class SafariWebDriverNewSessionResponse
{
    [JsonPropertyName("value")]
    public SafariWebDriverNewSessionValue? Value { get; init; }
}

internal sealed class SafariWebDriverNewSessionValue
{
    [JsonPropertyName("capabilities")]
    public SafariWebDriverReturnedCapabilities? Capabilities { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }

    [JsonPropertyName("sessionId")]
    public string? SessionId { get; init; }
}

internal sealed class SafariWebDriverReturnedCapabilities
{
    [JsonPropertyName("webSocketUrl")]
    public string? WebSocketUrl { get; init; }
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SafariWebDriverNewSessionRequest))]
[JsonSerializable(typeof(SafariWebDriverNewSessionResponse))]
internal sealed partial class SafariWebDriverJsonContext : JsonSerializerContext;
