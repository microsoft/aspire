// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using System.Text.Json.Nodes;
using Aspire.TestTools;
using Infrastructure.Tests.TestServices;
using Xunit;

namespace Infrastructure.Tests;

/// <summary>Exercises authentication, pagination, redirects, bounded streaming and non-replayed mutations.</summary>
public sealed class GitHubApiClientTests
{
    private static readonly Uri s_api = new("https://api.github.com/");

    [Fact]
    public async Task TokenIsLazyAndResolvedOnceAcrossConcurrentRequests()
    {
        var tokenCalls = 0;
        using var handler = new GitHubHttpMessageHandler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath != "/")
            {
                Assert.Equal("Bearer example-token", request.Headers.Authorization?.ToString());
                Assert.Equal("application/vnd.github+json", Assert.Single(request.Headers.Accept).MediaType);
            }
            return Task.FromResult(GitHubHttpMessageHandler.Json("{}"));
        });
        using var http = new HttpClient(handler);
        var client = new GitHubApiClient(
            http,
            _ =>
        {
            Interlocked.Increment(ref tokenCalls);
            return Task.FromResult("example-token");
        },
            s_api);
        Assert.Equal(0, tokenCalls);
        await Task.WhenAll(Enumerable.Range(start: 0, count: 5).Select(_ => client.GetJsonAsync("repos/a/b", default)));
        Assert.Equal(1, tokenCalls);
        client.Dispose();
        using var response = await http.GetAsync(s_api);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task PaginationPreservesRawShortPagesAndComparisonMetadata()
    {
        var calls = 0;
        using var handler = new GitHubHttpMessageHandler((_, _) =>
        {
            calls++;
            return Task.FromResult(calls == 1
                ? GitHubHttpMessageHandler.Json(
                    """{"status":"ahead","commits":[{"sha":"one"}]}""",
                    """<https://api.github.com/compare?page=2>; rel="next", <https://api.github.com/compare?page=2>; rel="last" """)
                : GitHubHttpMessageHandler.Json("""{"status":"ahead","commits":[]}"""));
        });
        using var http = new HttpClient(handler);
        using var client = Create(http);
        var pages = await client.GetPagesAsync("compare?per_page=100", default);
        Assert.Equal(2, pages.Count);
        Assert.Equal("ahead", pages[0]!["status"]!.GetValue<string>());
        Assert.Equal("one", pages[0]!["commits"]![0]!["sha"]!.GetValue<string>());
        Assert.Empty(pages[1]!["commits"]!.AsArray());
    }

    [Fact]
    public async Task RelativePaginationLinksResolveAgainstCurrentPage()
    {
        var paths = new List<string>();
        using var handler = new GitHubHttpMessageHandler((request, _) =>
        {
            paths.Add(request.RequestUri!.AbsolutePath);
            return Task.FromResult(paths.Count switch
            {
                1 => GitHubHttpMessageHandler.Json("[]", """<nested/page>; rel="next" """),
                2 => GitHubHttpMessageHandler.Json("[]", """<final>; rel="next" """),
                _ => GitHubHttpMessageHandler.Json("[]")
            });
        });
        using var http = new HttpClient(handler);
        using var client = Create(http);
        Assert.Equal(3, (await client.GetPagesAsync("start", default)).Count);
        Assert.Equal(["/start", "/nested/page", "/nested/final"], paths);
    }

    [Fact]
    public async Task TokenProviderFailuresHaveSanitizedRequestContext()
    {
        using var handler = new GitHubHttpMessageHandler((_, _) => throw new InvalidOperationException("Must not send."));
        using var http = new HttpClient(handler);
        var cause = new InvalidOperationException("sensitive provider details");
        using var client = new GitHubApiClient(http, _ => Task.FromException<string>(cause), s_api);
        var error = await Assert.ThrowsAsync<GitHubApiException>(() => client.GetJsonAsync("repos/a/b?token=secret", default));
        Assert.Equal("GET /repos/a/b: authentication failed.", error.Message);
        Assert.Same(cause, error.InnerException);
    }

    [Fact]
    public async Task CanceledWaiterDoesNotPoisonSharedTokenLookup()
    {
        var tokenCalls = 0;
        var credential = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new GitHubHttpMessageHandler((_, _) => Task.FromResult(GitHubHttpMessageHandler.Json("{}")));
        using var http = new HttpClient(handler);
        using var client = new GitHubApiClient(
            http,
            _ =>
        {
            tokenCalls++;
            return credential.Task;
        },
            s_api);
        using var cancellation = new CancellationTokenSource();
        var canceled = client.GetJsonAsync("first", cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);
        credential.SetResult("example-token");
        Assert.NotNull(await client.GetJsonAsync("second", default));
        Assert.Equal(1, tokenCalls);
    }

    [Theory]
    [InlineData("")]
    [InlineData("example-token\r\nInjected: secret")]
    public async Task InvalidTokensDoNotReachTransport(string token)
    {
        using var handler = new GitHubHttpMessageHandler((_, _) => throw new InvalidOperationException("Must not send."));
        using var http = new HttpClient(handler);
        using var client = new GitHubApiClient(http, _ => Task.FromResult(token), s_api);
        var error = await Assert.ThrowsAsync<GitHubApiException>(() => client.GetJsonAsync("start", default));
        Assert.Equal("GET /start: authentication failed.", error.Message);
    }

    [Theory]
    [InlineData("""<https://elsewhere.invalid/page?secret=value>; rel="next" """, 1)]
    [InlineData("""<https://api.github.com/start>; rel="next" """, 1)]
    [InlineData("""<http://api.github.com/page>; rel="next" """, 1)]
    [InlineData("""not-a-link; rel="next" """, 1)]
    public async Task PaginationRejectsUnsafeOrCyclicLinks(string link, int expectedCalls)
    {
        var calls = 0;
        using var handler = new GitHubHttpMessageHandler((_, _) =>
        {
            calls++;
            return Task.FromResult(GitHubHttpMessageHandler.Json("[]", link));
        });
        using var http = new HttpClient(handler);
        using var client = Create(http);
        var error = await Assert.ThrowsAsync<GitHubApiException>(() => client.GetPagesAsync("start", default));
        Assert.StartsWith("GET /start:", error.Message, StringComparison.Ordinal);
        Assert.Equal(expectedCalls, calls);
    }

    [Fact]
    public async Task PaginationHasFinitePageBudgetEvenForUniqueLinks()
    {
        var calls = 0;
        using var handler = new GitHubHttpMessageHandler((_, _) =>
        {
            calls++;
            if (calls > 1000)
            {
                throw new InvalidOperationException("Request exceeded the allowed page budget.");
            }

            return Task.FromResult(GitHubHttpMessageHandler.Json("[]", $"<https://api.github.com/start?page={calls}>; rel=\"next\""));
        });
        using var http = new HttpClient(handler);
        using var client = Create(http);
        var error = await Assert.ThrowsAsync<GitHubApiException>(() => client.GetPagesAsync("start", default));
        Assert.Equal("GET /start: pagination exceeds the 1000-page limit.", error.Message);
        Assert.Equal(1000, calls);
    }

    [Fact]
    public async Task PaginationHasAggregateByteBudget()
    {
        var calls = 0;
        using var handler = new GitHubHttpMessageHandler((_, _) =>
        {
            calls++;
            return Task.FromResult(calls == 1
                ? GitHubHttpMessageHandler.Json(
                    new string(' ', 16 * 1024 * 1024 - 2) + "[]",
                    """<https://api.github.com/start?page=2>; rel="next" """)
                : GitHubHttpMessageHandler.Json("[]"));
        });
        using var http = new HttpClient(handler);
        using var client = Create(http);
        var error = await Assert.ThrowsAsync<GitHubApiException>(() => client.GetPagesAsync("start", default));
        Assert.Equal("GET /start: response exceeds the 0-byte limit.", error.Message);
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    public async Task PaginationStopsReadingAtRemainingBudgetPlusDetectionByte(int remainingBytes)
    {
        var calls = 0;
        using var secondPage = new GitHubDownloadStream(new string(' ', count: 100) + "[]");
        using var handler = new GitHubHttpMessageHandler((_, _) =>
        {
            calls++;
            return Task.FromResult(calls == 1
                ? GitHubHttpMessageHandler.Json(
                    new string(' ', 16 * 1024 * 1024 - remainingBytes - 2) + "[]",
                    """<https://api.github.com/start?page=2>; rel="next" """)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(secondPage) });
        });
        using var http = new HttpClient(handler);
        using var client = Create(http);
        await Assert.ThrowsAsync<GitHubApiException>(() => client.GetPagesAsync("start", default));
        Assert.Equal(remainingBytes + 1, secondPage.BytesRead);
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(504)]
    [InlineData(429)]
    [InlineData(403)]
    public async Task RetryableReadStatusRetriesBoundedly(int status)
    {
        var calls = 0;
        using var handler = new GitHubHttpMessageHandler((_, _) =>
        {
            calls++;
            var response = calls == 1
                ? new HttpResponseMessage((HttpStatusCode)status)
                : GitHubHttpMessageHandler.Json("""{"ok":true}""");
            response.Headers.TryAddWithoutValidation("Retry-After", "0");
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        using var client = Create(http);
        Assert.True((await client.GetJsonAsync("start", default))!["ok"]!.GetValue<bool>());
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task RetryBudgetIsFiniteAndStatusIsPreserved()
    {
        var calls = 0;
        using var handler = new GitHubHttpMessageHandler((_, _) =>
        {
            calls++;
            var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            response.Headers.TryAddWithoutValidation("Retry-After", "0");
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        using var client = Create(http);
        var error = await Assert.ThrowsAsync<GitHubApiException>(() => client.GetJsonAsync("start?token=sensitive", default));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, error.StatusCode);
        Assert.Equal("GET /start: HTTP 503. [Retry-After=0]", error.Message);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task RateLimitFailureIncludesNormalizedAllowlistedMetadata()
    {
        var calls = 0;
        using var handler = new GitHubHttpMessageHandler((_, _) =>
        {
            calls++;
            var response = new HttpResponseMessage(HttpStatusCode.Forbidden);
            response.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", "000");
            response.Headers.TryAddWithoutValidation("X-RateLimit-Reset", "009223372036854775807");
            response.Headers.TryAddWithoutValidation("Retry-After", "003600");
            response.Headers.TryAddWithoutValidation("X-GitHub-Request-Id", "abcd:0123:ef45");
            response.Headers.TryAddWithoutValidation("X-Diagnostic", "sensitive freeform value");
            response.Content = new StringContent("sensitive response body");
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        using var client = Create(http);
        var error = await Assert.ThrowsAsync<GitHubApiException>(() => client.GetJsonAsync("start?token=sensitive", default));
        Assert.Equal("GET /start: HTTP 403. [rate limited; X-RateLimit-Remaining=0; X-RateLimit-Reset=9223372036854775807; Retry-After=3600; X-GitHub-Request-Id=ABCD:0123:EF45]", error.Message);
        Assert.Equal(HttpStatusCode.Forbidden, error.StatusCode);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task PositiveRemainingQuotaDoesNotLabelForbiddenAsRateLimited()
    {
        using var handler = new GitHubHttpMessageHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Forbidden);
            response.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", "04801");
            response.Headers.TryAddWithoutValidation("X-RateLimit-Reset", "01700000000");
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        using var client = Create(http);
        var error = await Assert.ThrowsAsync<GitHubApiException>(() => client.GetJsonAsync("start", default));
        Assert.Equal("GET /start: HTTP 403. [X-RateLimit-Remaining=4801; X-RateLimit-Reset=1700000000]", error.Message);
    }

    [Fact]
    public async Task RetryAfterDateIsReportedAsNumericUnixTimeWithoutMutationRetry()
    {
        var calls = 0;
        using var handler = new GitHubHttpMessageHandler((_, _) =>
        {
            calls++;
            var response = new HttpResponseMessage(HttpStatusCode.Forbidden);
            response.Headers.TryAddWithoutValidation("Retry-After", "Sun, 06 Nov 1994 08:49:37 GMT");
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        using var client = Create(http);
        var error = await Assert.ThrowsAsync<GitHubApiException>(() =>
            client.SendAsync(HttpMethod.Post, "rerun", body: null, HttpStatusCode.Created, default));
        Assert.Equal("POST /rerun: HTTP 403. [rate limited; Retry-After-Unix=784111777]", error.Message);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task FutureRetryAfterDateFailsWithoutRetryingBeforeServerDeadline()
    {
        var retryAt = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3600);
        var calls = 0;
        using var handler = new GitHubHttpMessageHandler((_, _) =>
        {
            calls++;
            var response = new HttpResponseMessage(HttpStatusCode.Forbidden);
            response.Headers.TryAddWithoutValidation("Retry-After", retryAt.ToString("r", System.Globalization.CultureInfo.InvariantCulture));
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        using var client = Create(http);
        var error = await Assert.ThrowsAsync<GitHubApiException>(() => client.GetJsonAsync("start", default));
        Assert.Equal($"GET /start: HTTP 403. [rate limited; Retry-After-Unix={retryAt.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture)}]", error.Message);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(33, 2)]
    [InlineData(32, 4)]
    public async Task RequestIdLengthAndGroupBoundsExcludeOversizedValues(int groupSize, int groupCount)
    {
        using var handler = new GitHubHttpMessageHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Forbidden);
            response.Headers.TryAddWithoutValidation("X-GitHub-Request-Id", string.Join(":", Enumerable.Repeat(new string('A', groupSize), groupCount)));
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        using var client = Create(http);
        var error = await Assert.ThrowsAsync<GitHubApiException>(() => client.GetJsonAsync("start", default));
        Assert.Equal("GET /start: HTTP 403.", error.Message);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("https://private.invalid/?signature=secret")]
    [InlineData("1234567890123456789012345678901234567890")]
    [InlineData("12\r\nToken: secret")]
    [InlineData("12, 34")]
    public async Task HostileHeaderValuesNeverEnterDiagnostics(string value)
    {
        var calls = 0;
        using var handler = new GitHubHttpMessageHandler((_, _) =>
        {
            calls++;
            var response = new HttpResponseMessage(HttpStatusCode.Forbidden);
            foreach (var header in new[] { "X-RateLimit-Remaining", "X-RateLimit-Reset", "Retry-After", "X-GitHub-Request-Id" })
            {
                response.Headers.TryAddWithoutValidation(header, value);
            }

            response.Content = new StringContent("sensitive response body");
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        using var client = Create(http);
        var error = await Assert.ThrowsAsync<GitHubApiException>(() => client.GetJsonAsync("start?token=sensitive", default));
        Assert.Equal("GET /start: HTTP 403.", error.Message);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task AmbiguousRepeatedHeaderValuesAreExcluded()
    {
        var calls = 0;
        using var handler = new GitHubHttpMessageHandler((_, _) =>
        {
            calls++;
            var response = new HttpResponseMessage(HttpStatusCode.Forbidden);
            response.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", ["0", "4801"]);
            response.Headers.TryAddWithoutValidation("X-RateLimit-Reset", ["1", "2"]);
            response.Headers.TryAddWithoutValidation("Retry-After", ["0", "3600"]);
            response.Headers.TryAddWithoutValidation("X-GitHub-Request-Id", ["ABCD:1234", "ABCD:5678"]);
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        using var client = Create(http);
        var error = await Assert.ThrowsAsync<GitHubApiException>(() => client.GetJsonAsync("start", default));
        Assert.Equal("GET /start: HTTP 403.", error.Message);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public async Task AuthenticationAndOrdinaryForbiddenFailuresDoNotRetry(int status)
    {
        var calls = 0;
        using var handler = new GitHubHttpMessageHandler((_, _) =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status));
        });
        using var http = new HttpClient(handler);
        using var client = Create(http);
        await Assert.ThrowsAsync<GitHubApiException>(() => client.GetJsonAsync("start", default));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ExhaustedRateLimitResetHeaderAllowsReadRetry()
    {
        var calls = 0;
        using var handler = new GitHubHttpMessageHandler((_, _) =>
        {
            calls++;
            if (calls > 1)
            {
                return Task.FromResult(GitHubHttpMessageHandler.Json("{}"));
            }

            var response = new HttpResponseMessage(HttpStatusCode.Forbidden);
            response.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", "0");
            response.Headers.TryAddWithoutValidation("X-RateLimit-Reset", "1");
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        using var client = Create(http);
        Assert.NotNull(await client.GetJsonAsync("start", default));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task CancellationInterruptsRetryAfterDelay()
    {
        using var cancellation = new CancellationTokenSource();
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var handler = new GitHubHttpMessageHandler((_, _) =>
        {
            calls++;
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.TryAddWithoutValidation("Retry-After", "5");
            received.SetResult();
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        using var client = Create(http);
        var pending = client.GetJsonAsync("start", cancellation.Token);
        await received.Task;
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("retry-after")]
    [InlineData("reset")]
    [InlineData("unspecified")]
    public async Task RateLimitWaitBeyondBudgetDoesNotRetryPrematurely(string limit)
    {
        var calls = 0;
        using var handler = new GitHubHttpMessageHandler((_, _) =>
        {
            calls++;
            if (calls > 1)
            {
                return Task.FromResult(GitHubHttpMessageHandler.Json("{}"));
            }

            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            if (limit == "retry-after")
            {
                response.Headers.TryAddWithoutValidation("Retry-After", "3600");
            }
            else if (limit == "reset")
            {
                response.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", "0");
                response.Headers.TryAddWithoutValidation("X-RateLimit-Reset", long.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        using var client = Create(http);
        var error = await Assert.ThrowsAsync<GitHubApiException>(() => client.GetJsonAsync("start", default));
        Assert.Equal(HttpStatusCode.TooManyRequests, error.StatusCode);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("POST", false)]
    [InlineData("PATCH", false)]
    [InlineData("PUT", false)]
    [InlineData("DELETE", false)]
    [InlineData("POST", true)]
    public async Task MutationIsNeverReplayedAfterFailure(string method, bool lostResponse)
    {
        var calls = 0;
        var cause = new HttpRequestException("sensitive transport details");
        using var handler = new GitHubHttpMessageHandler((_, _) =>
        {
            calls++;
            return lostResponse ? Task.FromException<HttpResponseMessage>(cause)
                : Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        });
        using var http = new HttpClient(handler);
        using var client = Create(http);
        var error = await Assert.ThrowsAsync<GitHubApiException>(() =>
            client.SendAsync(new HttpMethod(method), "repos/a/b/rerun", body: null, HttpStatusCode.Created, default));
        Assert.Equal(1, calls);
        if (lostResponse)
        {
            Assert.Same(cause, error.InnerException);
            Assert.Equal($"{method} /repos/a/b/rerun: request failed.", error.Message);
        }
    }

    [Fact]
    public async Task MutationsSupportJsonAndEmptyCreatedResponses()
    {
        var calls = 0;
        using var handler = new GitHubHttpMessageHandler(async (request, token) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("""{"title":"issue"}""", await request.Content!.ReadAsStringAsync(token));
            calls++;
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent(calls == 1 ? """{"number":42}""" : "")
            };
        });
        using var http = new HttpClient(handler);
        using var client = Create(http);
        var json = await client.SendJsonAsync(HttpMethod.Post, "issues", JsonNode.Parse("""{"title":"issue"}"""), HttpStatusCode.Created, default);
        Assert.Equal(42, json!["number"]!.GetValue<int>());
        await client.SendAsync(HttpMethod.Post, "rerun", JsonNode.Parse("""{"title":"issue"}"""), HttpStatusCode.Created, default);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task LostMutationBodyIsNotReplayed()
    {
        var calls = 0;
        using var stream = new GitHubDownloadStream("""{"number":""", _ => Task.FromException(new IOException("Connection lost.")));
        using var handler = new GitHubHttpMessageHandler((_, _) =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created) { Content = new StreamContent(stream) });
        });
        using var http = new HttpClient(handler);
        using var client = Create(http);
        var error = await Assert.ThrowsAsync<GitHubApiException>(() =>
            client.SendJsonAsync(HttpMethod.Post, "issues", body: null, HttpStatusCode.Created, default));
        Assert.Equal("POST /issues: response body could not be read completely.", error.Message);
        Assert.IsType<IOException>(error.InnerException);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task DownloadRedirectDoesNotForwardApiHeadersOrAuthorization()
    {
        var calls = 0;
        using var handler = new GitHubHttpMessageHandler((request, _) =>
        {
            calls++;
            if (calls == 1)
            {
                Assert.NotNull(request.Headers.Authorization);
                var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                redirect.Headers.Location = new Uri("https://blob.invalid/log?signature=secret");
                return Task.FromResult(redirect);
            }

            Assert.Null(request.Headers.Authorization);
            Assert.Empty(request.Headers.Accept);
            Assert.False(request.Headers.Contains("X-GitHub-Api-Version"));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("logs") });
        });
        using var http = new HttpClient(handler);
        using var client = Create(http);
        var result = await client.DownloadTextAsync("logs", maximumBytes: 100, default);
        Assert.Equal("logs", result.Text);
        Assert.Null(result.Error);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task SignedRedirectUrlNeverAppearsInErrors()
    {
        var calls = 0;
        using var handler = new GitHubHttpMessageHandler((_, _) =>
        {
            calls++;
            var response = new HttpResponseMessage(calls == 1 ? HttpStatusCode.Found : HttpStatusCode.Forbidden);
            response.Headers.Location = new Uri("https://blob.invalid/private?signature=secret");
            response.Content = new StringContent("secret response");
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        using var client = Create(http);
        var result = await client.DownloadTextAsync("repos/a/b/logs?token=secret", maximumBytes: 100, default);
        Assert.Equal("", result.Text);
        Assert.Equal("GET /repos/a/b/logs: HTTP 403.", result.Error);
    }

    [Theory]
    [InlineData("http://blob.invalid/log")]
    [InlineData("https://api.github.com/logs")]
    [InlineData("https://user:password@blob.invalid/log")]
    public async Task DownloadsRejectInsecureCredentialedOrCyclicRedirects(string target)
    {
        var calls = 0;
        using var handler = new GitHubHttpMessageHandler((_, _) =>
        {
            calls++;
            var response = new HttpResponseMessage(HttpStatusCode.Found);
            response.Headers.Location = new Uri(target);
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        using var client = Create(http);
        var result = await client.DownloadTextAsync("logs", maximumBytes: 100, default);
        Assert.Equal("", result.Text);
        Assert.Equal("GET /logs: invalid or cyclic redirect.", result.Error);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task NonCyclicRedirectChainHasFiniteBudget()
    {
        var calls = 0;
        using var handler = new GitHubHttpMessageHandler((_, _) =>
        {
            calls++;
            var response = new HttpResponseMessage(HttpStatusCode.Found);
            response.Headers.Location = new Uri($"https://blob.invalid/log/{calls}?signature=secret");
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        using var client = Create(http);
        var result = await client.DownloadTextAsync("logs", maximumBytes: 100, default);
        Assert.Equal("GET /logs: invalid or cyclic redirect.", result.Error);
        Assert.Equal(6, calls);
    }

    [Fact]
    public async Task DefaultHeadersAreRejectedBeforeTheyCanLeakAcrossOrigins()
    {
        using var handler = new GitHubHttpMessageHandler((_, _) => throw new InvalidOperationException("Must not send."));
        using var http = new HttpClient(handler);
        http.DefaultRequestHeaders.Add("Cookie", "sensitive=value");
        using var client = Create(http);
        var error = await Assert.ThrowsAsync<GitHubApiException>(() => client.GetJsonAsync("start", default));
        Assert.Equal("GET /start: the HTTP client must not have default request headers.", error.Message);
    }

    [Fact]
    public async Task JsonDoesNotFollowOffOriginRedirects()
    {
        var calls = 0;
        using var handler = new GitHubHttpMessageHandler((_, _) =>
        {
            calls++;
            var response = new HttpResponseMessage(HttpStatusCode.Found);
            response.Headers.Location = new Uri("https://blob.invalid/private?signature=secret");
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        using var client = Create(http);
        var error = await Assert.ThrowsAsync<GitHubApiException>(() => client.GetJsonAsync("logs", default));
        Assert.Equal("GET /logs: invalid or cyclic redirect.", error.Message);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task TextDownloadRetainsPartialBytesWithoutLeakingStreamError()
    {
        var calls = 0;
        var cause = new IOException("https://blob.invalid/private?signature=secret");
        using var stream = new GitHubDownloadStream("partial logs", _ => Task.FromException(cause));
        using var handler = new GitHubHttpMessageHandler((_, _) =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) });
        });
        using var http = new HttpClient(handler);
        using var client = Create(http);
        var result = await client.DownloadTextAsync("logs", maximumBytes: 100, default);
        Assert.Equal("partial logs", result.Text);
        Assert.Equal("GET /logs: response body could not be read completely.", result.Error);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ChunkedTextDownloadStopsAtByteLimit()
    {
        using var stream = new GitHubDownloadStream("0123456789");
        using var handler = new GitHubHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) }));
        using var http = new HttpClient(handler);
        using var client = Create(http);
        var result = await client.DownloadTextAsync("logs", maximumBytes: 5, default);
        Assert.Equal("01234", result.Text);
        Assert.Equal("GET /logs: response exceeds the 5-byte limit.", result.Error);
        Assert.Equal(6, stream.BytesRead);
    }

    [Fact]
    public async Task ChunkedFileDownloadEnforcesActualByteLimit()
    {
        var directory = Directory.CreateDirectory(Path.Combine(RepoRoot.Path, "artifacts", "github-http-tests", Guid.NewGuid().ToString("N")));
        var path = Path.Combine(directory.FullName, "artifact.zip");
        try
        {
            using var stream = new GitHubDownloadStream("0123456789");
            using var handler = new GitHubHttpMessageHandler((_, _) =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) }));
            using var http = new HttpClient(handler);
            using var client = Create(http);
            var error = await Assert.ThrowsAsync<GitHubApiException>(() => client.DownloadFileAsync("artifact", path, maximumBytes: 5, default));
            Assert.Equal("GET /artifact: response exceeds the 5-byte limit.", error.Message);
            Assert.Equal(6, stream.BytesRead);
            Assert.Equal("01234", await File.ReadAllTextAsync(path));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task FileDownloadReturnsActualCountAndDoesNotClobberExistingOutput()
    {
        var directory = Directory.CreateDirectory(Path.Combine(RepoRoot.Path, "artifacts", "github-http-tests", Guid.NewGuid().ToString("N")));
        var path = Path.Combine(directory.FullName, "artifact.zip");
        try
        {
            using var handler = new GitHubHttpMessageHandler((_, _) =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("artifact") }));
            using var http = new HttpClient(handler);
            using var client = Create(http);
            Assert.Equal(8, await client.DownloadFileAsync("artifact", path, maximumBytes: 100, default));
            Assert.Equal("artifact", await File.ReadAllTextAsync(path));
            await Assert.ThrowsAsync<GitHubApiException>(() => client.DownloadFileAsync("artifact", path, maximumBytes: 100, default));
            Assert.Equal("artifact", await File.ReadAllTextAsync(path));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task FileDownloadCancellationRetainsBoundedIncompleteOutput()
    {
        var directory = Directory.CreateDirectory(Path.Combine(RepoRoot.Path, "artifacts", "github-http-tests", Guid.NewGuid().ToString("N")));
        var path = Path.Combine(directory.FullName, "artifact.zip");
        try
        {
            using var cancellation = new CancellationTokenSource();
            using var stream = new GitHubDownloadStream(
                "partial",
                async token =>
            {
                cancellation.Cancel();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            });
            using var handler = new GitHubHttpMessageHandler((_, _) =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) }));
            using var http = new HttpClient(handler);
            using var client = Create(http);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.DownloadFileAsync("artifact", path, maximumBytes: 100, cancellation.Token));
            Assert.Equal("partial", await File.ReadAllTextAsync(path));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task CallerCancellationWhileReadingBodyPropagates()
    {
        using var cancellation = new CancellationTokenSource();
        using var stream = new GitHubDownloadStream(
            "partial",
            async token =>
        {
            cancellation.Cancel();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        });
        using var handler = new GitHubHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) }));
        using var http = new HttpClient(handler);
        using var client = Create(http);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.DownloadTextAsync("logs", maximumBytes: 100, cancellation.Token));
    }

    [Fact]
    public async Task CallerCancellationWhileReadingJsonPropagatesWithoutRetry()
    {
        var calls = 0;
        using var cancellation = new CancellationTokenSource();
        using var stream = new GitHubDownloadStream(
            "{",
            async token =>
        {
            cancellation.Cancel();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        });
        using var handler = new GitHubHttpMessageHandler((_, _) =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) });
        });
        using var http = new HttpClient(handler);
        using var client = Create(http);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetJsonAsync("start", cancellation.Token));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task JsonRejectsOversizedChunkedBody()
    {
        using var stream = new GitHubDownloadStream(new string(' ', 16 * 1024 * 1024 + 1));
        using var handler = new GitHubHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) }));
        using var http = new HttpClient(handler);
        using var client = Create(http);
        var error = await Assert.ThrowsAsync<GitHubApiException>(() => client.GetJsonAsync("large", default));
        Assert.Equal("GET /large: response exceeds the 16777216-byte limit.", error.Message);
    }

    [Fact]
    public async Task InvalidJsonPreservesCauseButNotResponseBody()
    {
        using var handler = new GitHubHttpMessageHandler((_, _) => Task.FromResult(GitHubHttpMessageHandler.Json("secret invalid json")));
        using var http = new HttpClient(handler);
        using var client = Create(http);
        var error = await Assert.ThrowsAsync<GitHubApiException>(() => client.GetJsonAsync("invalid?signature=secret", default));
        Assert.Equal("GET /invalid: invalid JSON response.", error.Message);
        Assert.IsAssignableFrom<System.Text.Json.JsonException>(error.InnerException);
    }

    [Theory]
    [InlineData("https://elsewhere.invalid/private?secret=value")]
    [InlineData("//elsewhere.invalid/private")]
    public async Task OffOriginEndpointsAreRejectedBeforeResolvingToken(string endpoint)
    {
        using var handler = new GitHubHttpMessageHandler((_, _) => throw new InvalidOperationException("Must not send."));
        using var http = new HttpClient(handler);
        using var client = new GitHubApiClient(http, _ => throw new InvalidOperationException("Must not authenticate."), s_api);
        var error = await Assert.ThrowsAsync<GitHubApiException>(() => client.GetJsonAsync(endpoint, default));
        Assert.Equal("GET: endpoint must use the configured HTTPS API origin.", error.Message);
    }

    private static GitHubApiClient Create(HttpClient http) => new(http, _ => Task.FromResult("example-token"), s_api);
}
