// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Aspire.TestTools;

/// <summary>Downloads GitHub text, retaining bounded partial content when a download fails.</summary>
public sealed record GitHubTextDownload(string Text, string? Error);

/// <summary>
/// A GitHub request failure with a sanitized message and the original cause.
/// HTTP failures include only validated numeric rate-limit metadata and bounded hexadecimal request IDs.
/// </summary>
public sealed class GitHubApiException : Exception
{
    /// <summary>Creates a request failure. The message must not contain credentials or response bodies.</summary>
    public GitHubApiException(string message, HttpStatusCode? statusCode = null, Exception? innerException = null)
        : base(message, innerException)
    {
        StatusCode = statusCode;
    }

    /// <summary>Gets the response status, when a response was received.</summary>
    public HttpStatusCode? StatusCode { get; }
}

/// <summary>
/// Provides bounded, authenticated GitHub API requests and credential-free external downloads.
/// GET requests have at most three attempts; server retry delays over five seconds fail rather than retry early.
/// </summary>
public sealed class GitHubApiClient : IDisposable
{
    private const int JsonLimit = 16 * 1024 * 1024;
    private const int MaximumRedirects = 5;
    private static readonly TimeSpan s_requestTimeout = TimeSpan.FromMinutes(2);
    private readonly HttpClient _httpClient;
    private readonly Uri _apiBaseUri;
    private readonly Lazy<Task<string>> _token;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _ownsHttpClient;

    /// <summary>
    /// Creates a client without taking ownership of <paramref name="httpClient"/>.
    /// Its handler must disable automatic redirects and cookies, and it must have no default headers.
    /// Tokens are resolved once, lazily, and are only sent to the configured HTTPS API origin.
    /// </summary>
    public GitHubApiClient(HttpClient httpClient, Func<CancellationToken, Task<string>> tokenProvider, Uri apiBaseUri)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(tokenProvider);
        ArgumentNullException.ThrowIfNull(apiBaseUri);
        if (!apiBaseUri.IsAbsoluteUri || apiBaseUri.Scheme != Uri.UriSchemeHttps ||
            apiBaseUri.UserInfo.Length != 0 || apiBaseUri.Query.Length != 0 || apiBaseUri.Fragment.Length != 0)
        {
            throw new ArgumentException("The API base must be an absolute HTTPS URI without credentials, query, or fragment.", nameof(apiBaseUri));
        }

        _httpClient = httpClient;
        _apiBaseUri = apiBaseUri;
        _token = new Lazy<Task<string>>(() => ResolveTokenAsync(tokenProvider));
    }

    /// <summary>
    /// Creates an owning github.com-only client. Uses GH_TOKEN, then GITHUB_TOKEN, or a single
    /// in-memory gh credential lookup when neither environment token is present.
    /// Authentication is deferred until the first request.
    /// </summary>
    public static GitHubApiClient CreateDefault()
    {
        var host = Environment.GetEnvironmentVariable("GH_HOST");
        var api = Environment.GetEnvironmentVariable("GITHUB_API_URL");
        if ((!string.IsNullOrWhiteSpace(host) && !string.Equals(host, "github.com", StringComparison.OrdinalIgnoreCase)) ||
            (!string.IsNullOrWhiteSpace(api) && !string.Equals(api.TrimEnd('/'), "https://api.github.com", StringComparison.OrdinalIgnoreCase)))
        {
            throw new GitHubApiException("The default GitHub client supports github.com only.");
        }

        var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false };
        var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        return new GitHubApiClient(http, GetDefaultTokenAsync, new Uri("https://api.github.com/")) { _ownsHttpClient = true };
    }

    /// <summary>Gets a JSON response, bounded to 16 MiB.</summary>
    public Task<JsonNode?> GetJsonAsync(string endpoint, CancellationToken cancellationToken)
        => SendJsonAsync(HttpMethod.Get, endpoint, body: null, HttpStatusCode.OK, cancellationToken);

    /// <summary>
    /// Gets raw JSON pages by following Link rel=next, including short pages and comparison metadata.
    /// Pagination links must remain on the configured API origin and must not cycle.
    /// The complete result is limited to 1000 pages and 16 MiB of received JSON.
    /// </summary>
    public async Task<JsonArray> GetPagesAsync(string endpoint, CancellationToken cancellationToken)
    {
        var original = ResolveEndpoint(HttpMethod.Get, endpoint);
        var current = original;
        var visited = new HashSet<Uri>();
        var pages = new JsonArray();
        long totalBytes = 0;
        while (true)
        {
            if (pages.Count == 1000)
            {
                throw Error(HttpMethod.Get, original, "pagination exceeds the 1000-page limit.");
            }

            if (!visited.Add(current))
            {
                throw Error(HttpMethod.Get, original, "invalid or cyclic pagination link.");
            }

            using var timeout = CreateTimeout(cancellationToken);
            try
            {
                using var response = await OpenAsync(HttpMethod.Get, current, original, body: null, HttpStatusCode.OK, allowExternalRedirect: false, timeout.Token).ConfigureAwait(continueOnCapturedContext: false);
                var (node, bytes) = await ReadJsonAsync(response, JsonLimit - totalBytes, HttpMethod.Get, original, timeout.Token).ConfigureAwait(continueOnCapturedContext: false);
                totalBytes += bytes;
                pages.Add(node);
                var next = GetNextPage(response, current, original);
                if (next is null)
                {
                    return pages;
                }

                current = next;
            }
            catch (Exception ex) when (IsRequestFailure(ex))
            {
                throw Wrap(ex, HttpMethod.Get, original, cancellationToken);
            }
        }
    }

    /// <summary>Sends JSON and parses a bounded JSON response. Mutations are never retried.</summary>
    public async Task<JsonNode?> SendJsonAsync(HttpMethod method, string endpoint, JsonNode? body, HttpStatusCode expectedStatusCode, CancellationToken cancellationToken)
    {
        var uri = ResolveEndpoint(method, endpoint);
        using var timeout = CreateTimeout(cancellationToken);
        try
        {
            using var response = await OpenAsync(method, uri, uri, body, expectedStatusCode, allowExternalRedirect: false, timeout.Token).ConfigureAwait(continueOnCapturedContext: false);
            return (await ReadJsonAsync(response, JsonLimit, method, uri, timeout.Token).ConfigureAwait(continueOnCapturedContext: false)).Node;
        }
        catch (Exception ex) when (IsRequestFailure(ex))
        {
            throw Wrap(ex, method, uri, cancellationToken);
        }
    }

    /// <summary>Sends JSON and checks the status without requiring a response body. Mutations are never retried.</summary>
    public async Task SendAsync(HttpMethod method, string endpoint, JsonNode? body, HttpStatusCode expectedStatusCode, CancellationToken cancellationToken)
    {
        var uri = ResolveEndpoint(method, endpoint);
        using var timeout = CreateTimeout(cancellationToken);
        try
        {
            using var response = await OpenAsync(method, uri, uri, body, expectedStatusCode, allowExternalRedirect: false, timeout.Token).ConfigureAwait(continueOnCapturedContext: false);
        }
        catch (Exception ex) when (IsRequestFailure(ex))
        {
            throw Wrap(ex, method, uri, cancellationToken);
        }
    }

    /// <summary>
    /// Downloads bounded UTF-8 text. Returns partial content and a sanitized error on stream failure,
    /// truncation, or timeout; explicit caller cancellation always propagates instead of returning partial text.
    /// </summary>
    public async Task<GitHubTextDownload> DownloadTextAsync(string endpoint, long maximumBytes, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumBytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumBytes, int.MaxValue);
        var uri = ResolveEndpoint(HttpMethod.Get, endpoint);
        using var timeout = CreateTimeout(cancellationToken);
        using var output = new MemoryStream();
        try
        {
            using var response = await OpenAsync(HttpMethod.Get, uri, uri, body: null, HttpStatusCode.OK, allowExternalRedirect: true, timeout.Token).ConfigureAwait(continueOnCapturedContext: false);
            await CopyBoundedAsync(response, output, maximumBytes, HttpMethod.Get, uri, timeout.Token).ConfigureAwait(continueOnCapturedContext: false);
            cancellationToken.ThrowIfCancellationRequested();
            return new GitHubTextDownload(Encoding.UTF8.GetString(output.GetBuffer(), index: 0, (int)output.Length), Error: null);
        }
        catch (Exception ex) when (IsRequestFailure(ex))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var error = Wrap(ex, HttpMethod.Get, uri, cancellationToken);
            return new GitHubTextDownload(Encoding.UTF8.GetString(output.GetBuffer(), index: 0, (int)output.Length), error.Message);
        }
    }

    /// <summary>
    /// Streams a download to a new file, enforcing actual received bytes.
    /// On failure, retains bounded partial output so callers can account for it before cleanup.
    /// </summary>
    public async Task<long> DownloadFileAsync(string endpoint, string outputPath, long maximumBytes, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumBytes);
        var uri = ResolveEndpoint(HttpMethod.Get, endpoint);
        using var timeout = CreateTimeout(cancellationToken);
        try
        {
            using var response = await OpenAsync(HttpMethod.Get, uri, uri, body: null, HttpStatusCode.OK, allowExternalRedirect: true, timeout.Token).ConfigureAwait(continueOnCapturedContext: false);
            using var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 81920, useAsync: true);
            var count = await CopyBoundedAsync(response, output, maximumBytes, HttpMethod.Get, uri, timeout.Token).ConfigureAwait(continueOnCapturedContext: false);
            await output.FlushAsync(timeout.Token).ConfigureAwait(continueOnCapturedContext: false);
            cancellationToken.ThrowIfCancellationRequested();
            return count;
        }
        catch (Exception ex) when (IsRequestFailure(ex))
        {
            throw Wrap(ex, HttpMethod.Get, uri, cancellationToken);
        }
    }

    /// <summary>Disposes owned transport resources, but never disposes an injected HttpClient.</summary>
    public void Dispose()
    {
        _lifetime.Cancel();
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    private async Task<string> ResolveTokenAsync(Func<CancellationToken, Task<string>> provider)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        return await provider(timeout.Token).WaitAsync(timeout.Token).ConfigureAwait(continueOnCapturedContext: false);
    }

    private CancellationTokenSource CreateTimeout(CancellationToken cancellationToken)
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        // ResponseHeadersRead does not cover body reads with HttpClient.Timeout.
        // Keep this budget alive through redirects, retries, and body consumption.
        timeout.CancelAfter(s_requestTimeout);
        return timeout;
    }

    private Uri ResolveEndpoint(HttpMethod method, string endpoint)
    {
        if (!Uri.TryCreate(_apiBaseUri, endpoint, out var uri) || !IsApiOrigin(uri) || !IsSafeUri(uri))
        {
            throw new GitHubApiException($"{method}: endpoint must use the configured HTTPS API origin.");
        }

        return uri;
    }

    private bool IsApiOrigin(Uri uri) => uri.Scheme == _apiBaseUri.Scheme &&
        string.Equals(uri.Host, _apiBaseUri.Host, StringComparison.OrdinalIgnoreCase) && uri.Port == _apiBaseUri.Port;

    private static bool IsSafeUri(Uri uri) => uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps &&
        uri.UserInfo.Length == 0 && uri.Fragment.Length == 0;

    private async Task<HttpResponseMessage> OpenAsync(HttpMethod method, Uri uri, Uri original, JsonNode? body, HttpStatusCode expectedStatus, bool allowExternalRedirect, CancellationToken cancellationToken)
    {
        // Defaults are implicitly copied onto every request, including external signed downloads.
        // Requiring request-local headers prevents a caller's default authorization/cookie from leaking.
        if (_httpClient.DefaultRequestHeaders.Any())
        {
            throw Error(method, original, "the HTTP client must not have default request headers.");
        }

        string token;
        try
        {
            token = await _token.Value.WaitAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw Error(method, original, "authentication failed.", inner: ex);
        }

        if (string.IsNullOrWhiteSpace(token) || token.Any(char.IsControl))
        {
            throw Error(method, original, "authentication failed.");
        }
        for (var attempt = 0; ; attempt++)
        {
            var current = uri;
            var visited = new HashSet<Uri> { current };
            for (var redirects = 0; ; redirects++)
            {
                using var request = new HttpRequestMessage(method, current);
                if (IsApiOrigin(current))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
                    request.Headers.UserAgent.ParseAdd("Aspire-TestTools");
                    // Pin the unversioned API's baseline, rather than silently opting into breaking changes.
                    // https://docs.github.com/en/rest/about-the-rest-api/api-versions
                    request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
                }

                if (body is not null)
                {
                    request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
                }

                var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
                if (response.StatusCode == expectedStatus)
                {
                    return response;
                }

                using (response)
                {
                    // Only GET is replayable. In particular, a lost mutation response must not
                    // turn a successful issue creation/rerun into a second write.
                    if (method == HttpMethod.Get && attempt < 2 && ShouldRetry(response) &&
                        GetRetryDelay(response, attempt) is { } retryDelay)
                    {
                        await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
                        break;
                    }

                    if (method == HttpMethod.Get && response.StatusCode is HttpStatusCode.MovedPermanently or
                        HttpStatusCode.Found or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
                    {
                        var location = response.Headers.Location;
                        if (redirects >= MaximumRedirects || location is null ||
                            !Uri.TryCreate(current, location, out var next) || !IsSafeUri(next) ||
                            (!allowExternalRedirect && !IsApiOrigin(next)) || !visited.Add(next))
                        {
                            throw Error(method, original, "invalid or cyclic redirect.", response.StatusCode);
                        }

                        current = next;
                        continue;
                    }

                    throw Error(method, original, DescribeHttpFailure(response), response.StatusCode);
                }
            }
        }
    }

    private static bool ShouldRetry(HttpResponseMessage response)
        => response.StatusCode is HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or
            HttpStatusCode.GatewayTimeout or HttpStatusCode.TooManyRequests ||
            (response.StatusCode == HttpStatusCode.Forbidden &&
                HasRateLimitHeaders(response));

    private static bool HasRateLimitHeaders(HttpResponseMessage response)
        => ReadNumericHeader(response, "X-RateLimit-Remaining") == 0 ||
            ReadNumericHeader(response, "Retry-After") is not null || ReadRetryAfterDate(response) is not null;

    private static TimeSpan? GetRetryDelay(HttpResponseMessage response, int attempt)
    {
        double? seconds = ReadNumericHeader(response, "Retry-After");
        if (seconds is null && ReadRetryAfterDate(response) is { } date)
        {
            seconds = (date - DateTimeOffset.UtcNow).TotalSeconds;
        }

        if (seconds is null && ReadNumericHeader(response, "X-RateLimit-Remaining") == 0 &&
            ReadNumericHeader(response, "X-RateLimit-Reset") is { } reset)
        {
            // Unix seconds can be arbitrarily large in an untrusted header. Subtract numerically
            // and enforce our wait budget before constructing a TimeSpan that could overflow.
            seconds = (double)reset - DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }

        // GitHub requires at least a minute for a secondary limit without explicit retry guidance.
        // Never shorten a server's requested wait to fit a local budget; fail with the original status.
        // https://docs.github.com/en/rest/using-the-rest-api/best-practices-for-using-the-rest-api#handle-rate-limit-errors-appropriately
        seconds ??= response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests
            ? 60 : 0.25 * (attempt + 1);
        return seconds > 5 ? null : TimeSpan.FromSeconds(Math.Max(seconds.Value, 0));
    }

    private static string DescribeHttpFailure(HttpResponseMessage response)
    {
        List<string> metadata = [];
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests && HasRateLimitHeaders(response))
        {
            metadata.Add("rate limited");
        }

        foreach (var header in new[] { "X-RateLimit-Remaining", "X-RateLimit-Reset", "Retry-After" })
        {
            if (ReadNumericHeader(response, header) is { } number)
            {
                metadata.Add($"{header}={number.ToString(CultureInfo.InvariantCulture)}");
            }
        }

        if (ReadRetryAfterDate(response) is { } date)
        {
            metadata.Add($"Retry-After-Unix={date.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)}");
        }

        // GitHub request IDs have colon-separated hexadecimal groups, for example
        // "ABCD:1234:5678:9ABC:DEF0". Reject other shapes rather than redacting freeform text.
        var requestId = ReadSingleHeader(response, "X-GitHub-Request-Id");
        if (requestId is not null && requestId.Contains(':') &&
            requestId.Split(':').All(group => group.Length is > 0 and <= 32 && group.All(char.IsAsciiHexDigit)))
        {
            metadata.Add($"X-GitHub-Request-Id={requestId.ToUpperInvariant()}");
        }

        var message = $"HTTP {(int)response.StatusCode}.";
        return metadata.Count == 0 ? message : $"{message} [{string.Join("; ", metadata)}]";
    }

    private static string? ReadSingleHeader(HttpResponseMessage response, string name)
    {
        if (!response.Headers.TryGetValues(name, out var values))
        {
            return null;
        }

        using var enumerator = values.GetEnumerator();
        if (!enumerator.MoveNext())
        {
            return null;
        }

        var value = enumerator.Current;
        if (enumerator.MoveNext() || value.Length > 128 || value.Any(char.IsControl))
        {
            return null;
        }

        return value.Trim();
    }

    private static long? ReadNumericHeader(HttpResponseMessage response, string name)
    {
        var value = ReadSingleHeader(response, name);
        return value is not null && value.Length != 0 && value.All(char.IsAsciiDigit) &&
            long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : null;
    }

    private static DateTimeOffset? ReadRetryAfterDate(HttpResponseMessage response)
    {
        // Retry-After accepts decimal seconds or an HTTP date such as
        // "Sun, 06 Nov 1994 08:49:37 GMT"; never copy the raw value into diagnostics.
        var value = ReadSingleHeader(response, "Retry-After");
        return DateTimeOffset.TryParseExact(value, "r", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)
            ? date : null;
    }

    private static async Task<(JsonNode? Node, long Bytes)> ReadJsonAsync(HttpResponseMessage response, long maximumBytes, HttpMethod method, Uri original, CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        await CopyBoundedAsync(response, output, maximumBytes, method, original, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
        if (output.Length == 0)
        {
            return (null, 0);
        }

        try
        {
            return (JsonNode.Parse(output.GetBuffer().AsSpan(start: 0, (int)output.Length)), output.Length);
        }
        catch (JsonException ex)
        {
            throw Error(method, original, "invalid JSON response.", inner: ex);
        }
    }

    private static async Task<long> CopyBoundedAsync(HttpResponseMessage response, Stream output, long maximumBytes, HttpMethod method, Uri original, CancellationToken cancellationToken)
    {
        using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
        var buffer = new byte[81920];
        long count = 0;
        try
        {
            while (true)
            {
                var remaining = maximumBytes - count;
                var readSize = remaining >= buffer.Length ? buffer.Length : (int)remaining + 1;
                var read = await input.ReadAsync(buffer.AsMemory(start: 0, readSize), cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
                cancellationToken.ThrowIfCancellationRequested();
                if (read == 0)
                {
                    return count;
                }

                var accepted = (int)Math.Min(read, remaining);
                await output.WriteAsync(buffer.AsMemory(start: 0, accepted), cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
                count += accepted;
                if (read > accepted)
                {
                    throw Error(method, original, $"response exceeds the {maximumBytes}-byte limit.");
                }
            }
        }
        catch (IOException ex)
        {
            throw Error(method, original, "response body could not be read completely.", inner: ex);
        }
    }

    private Uri? GetNextPage(HttpResponseMessage response, Uri current, Uri original)
    {
        if (!response.Headers.TryGetValues("Link", out var headers))
        {
            return null;
        }

        Uri? next = null;
        // GitHub emits: <https://api.github.com/...?...>; rel="next", <...>; rel="last".
        // Commas inside <URLs> or quoted parameters are not link delimiters.
        foreach (var header in headers)
        {
            foreach (var link in SplitLinks(header))
            {
                var value = link.Trim();
                var end = value.IndexOf('>');
                if (!value.StartsWith('<') || end < 1)
                {
                    throw Error(HttpMethod.Get, original, "invalid pagination link.");
                }

                foreach (var parameter in value[(end + 1)..].Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var equals = parameter.IndexOf('=');
                    if (equals < 0 || !parameter[..equals].Trim().Equals("rel", StringComparison.OrdinalIgnoreCase) ||
                        !parameter[(equals + 1)..].Trim().Trim('"').Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("next"))
                    {
                        continue;
                    }

                    if (next is not null || !Uri.TryCreate(response.RequestMessage?.RequestUri ?? current, value[1..end], out next) ||
                        !IsSafeUri(next) || !IsApiOrigin(next))
                    {
                        throw Error(HttpMethod.Get, original, "invalid pagination link.");
                    }
                }
            }
        }

        return next;
    }

    private static IEnumerable<string> SplitLinks(string header)
    {
        var inUrl = false;
        var quoted = false;
        var start = 0;
        for (var index = 0; index < header.Length; index++)
        {
            var character = header[index];
            if (character == '<' && !quoted)
            {
                inUrl = true;
            }
            else if (character == '>' && !quoted)
            {
                inUrl = false;
            }
            else if (character == '"' && !inUrl)
            {
                quoted = !quoted;
            }
            else if (character == ',' && !inUrl && !quoted)
            {
                yield return header[start..index];
                start = index + 1;
            }
        }

        yield return header[start..];
    }

    private static bool IsRequestFailure(Exception ex)
        => ex is GitHubApiException or HttpRequestException or IOException or OperationCanceledException or UnauthorizedAccessException or FormatException;

    private static GitHubApiException Wrap(Exception ex, HttpMethod method, Uri uri, CancellationToken callerToken)
    {
        callerToken.ThrowIfCancellationRequested();
        return ex as GitHubApiException ?? Error(
            method,
            uri,
            ex is OperationCanceledException ? "request timed out or was disposed." : "request failed.",
            inner: ex);
    }

    private static GitHubApiException Error(HttpMethod method, Uri uri, string message, HttpStatusCode? status = null, Exception? inner = null)
        // Drop the query altogether: signed URLs, access tokens, and search text must not enter diagnostics.
        => new($"{method} {uri.AbsolutePath}: {message}", status, inner);

    private static async Task<string> GetDefaultTokenAsync(CancellationToken cancellationToken)
    {
        foreach (var variable in new[] { "GH_TOKEN", "GITHUB_TOKEN" })
        {
            if (Environment.GetEnvironmentVariable(variable) is { } token && !string.IsNullOrWhiteSpace(token))
            {
                return token.Trim();
            }
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("gh")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                ArgumentList = { "auth", "token", "--hostname", "github.com" }
            }
        };
        try
        {
            process.Start();
            using var registration = timeout.Token.Register(() =>
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // The process may exit between the check and the cancellation callback.
                }
            });
            // Drain both streams concurrently so an unexpected CLI diagnostic cannot block exit.
            // Both are bounded and memory-only; stderr is deliberately never surfaced.
            var stdout = ReadCredentialOutputAsync(process.StandardOutput, timeout.Token);
            var stderr = ReadCredentialOutputAsync(process.StandardError, timeout.Token);
            await Task.WhenAll(stdout, stderr, process.WaitForExitAsync(timeout.Token)).ConfigureAwait(continueOnCapturedContext: false);
            if (process.ExitCode == 0 && !string.IsNullOrWhiteSpace(stdout.Result))
            {
                return stdout.Result.Trim();
            }

            throw new GitHubApiException("GitHub authentication is unavailable. Set GH_TOKEN or sign in with gh.");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new GitHubApiException("GitHub credential lookup failed.", innerException: ex);
        }
        finally
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(continueOnCapturedContext: false);
                }
            }
            catch (InvalidOperationException)
            {
                // Start may have failed before an OS process was associated.
            }
        }
    }

    private static async Task<string> ReadCredentialOutputAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var buffer = new char[16385];
        var count = 0;
        while (count < buffer.Length)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(count), cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
            if (read == 0)
            {
                return new string(buffer, startIndex: 0, count);
            }

            count += read;
        }

        throw new GitHubApiException("GitHub credential lookup exceeded its output limit.");
    }
}
