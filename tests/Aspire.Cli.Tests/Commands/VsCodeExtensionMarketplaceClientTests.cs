// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Aspire.Cli.Tests.Utils;
using Aspire.Cli.Utils.EnvironmentChecker;
using Microsoft.Extensions.Time.Testing;

namespace Aspire.Cli.Tests.Commands;

public class VsCodeExtensionMarketplaceClientTests
{
    [Fact]
    public async Task GetLatestVersionsAsync_ReturnsLatestStableAndPreReleaseVersionsFromMarketplaceResponse()
    {
        const string responseJson = """
            {
              "results": [
                {
                  "extensions": [
                    {
                      "extensionName": "aspire-vscode",
                      "publisher": {
                        "publisherName": "microsoft-aspire"
                      },
                      "versions": [
                        {
                          "version": "1.17.0",
                          "properties": [
                            {
                              "key": "Microsoft.VisualStudio.Code.PreRelease",
                              "value": "true"
                            }
                          ]
                        },
                        {
                          "version": "1.15.0",
                          "properties": []
                        },
                        {
                          "version": "1.16.0",
                          "properties": []
                        }
                      ]
                    }
                  ]
                }
              ]
            }
            """;
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
        };
        HttpMethod? requestMethod = null;
        Uri? requestUri = null;
        string? requestBody = null;
        string? acceptHeader = null;
        string? userAgent = null;
        AuthenticationHeaderValue? authorization = null;
        var hasCookieHeader = false;
        var hasMarketplaceClientId = false;
        var hasMarketplaceUserId = false;
        using var handler = new MockHttpMessageHandler(async (request, cancellationToken) =>
        {
            requestMethod = request.Method;
            requestUri = request.RequestUri;
            requestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            acceptHeader = request.Headers.Accept.ToString();
            userAgent = request.Headers.UserAgent.ToString();
            authorization = request.Headers.Authorization;
            hasCookieHeader = request.Headers.Contains("Cookie");
            hasMarketplaceClientId = request.Headers.Contains("X-Market-Client-Id");
            hasMarketplaceUserId = request.Headers.Contains("X-Market-User-Id");
            return response;
        });
        using var httpClient = new HttpClient(handler);
        var client = new VsCodeExtensionMarketplaceClient(httpClient, TimeProvider.System);

        var versions = await client.GetLatestVersionsAsync(TestContext.Current.CancellationToken);

        Assert.Equal("1.16.0", versions.StableVersion?.ToString());
        Assert.Equal("1.17.0", versions.PreReleaseVersion?.ToString());
        Assert.Equal(HttpMethod.Post, requestMethod);
        Assert.Equal(
            "https://marketplace.visualstudio.com/_apis/public/gallery/extensionquery",
            requestUri!.AbsoluteUri);
        Assert.Equal(string.Empty, requestUri.Query);
        // The gallery answers HTTP 400 when no API version is supplied, so pin the Accept header
        // that carries it.
        Assert.Equal("application/json; api-version=3.0-preview.1", acceptHeader);
        Assert.StartsWith("Aspire-CLI/", userAgent, StringComparison.Ordinal);
        Assert.Null(authorization);
        Assert.False(hasCookieHeader);
        Assert.False(hasMarketplaceClientId);
        Assert.False(hasMarketplaceUserId);

        using var requestJson = JsonDocument.Parse(requestBody!);
        Assert.Collection(
            requestJson.RootElement.EnumerateObject(),
            property => Assert.Equal("filters", property.Name),
            property => Assert.Equal("assetTypes", property.Name),
            property => Assert.Equal("flags", property.Name));
        var filter = Assert.Single(requestJson.RootElement.GetProperty("filters").EnumerateArray());
        var criteria = filter.GetProperty("criteria").EnumerateArray().ToArray();
        Assert.Collection(
            criteria,
            criterion =>
            {
                Assert.Equal(7, criterion.GetProperty("filterType").GetInt32());
                Assert.Equal(VsCodeExtensionMarketplaceClient.ExtensionId, criterion.GetProperty("value").GetString());
            },
            criterion =>
            {
                Assert.Equal(8, criterion.GetProperty("filterType").GetInt32());
                Assert.Equal("Microsoft.VisualStudio.Code", criterion.GetProperty("value").GetString());
            },
            criterion =>
            {
                Assert.Equal(12, criterion.GetProperty("filterType").GetInt32());
                Assert.Equal("4096", criterion.GetProperty("value").GetString());
            });
        Assert.Equal(65584, requestJson.RootElement.GetProperty("flags").GetInt32());
    }

    [Fact]
    public async Task GetLatestVersionsAsync_RejectsResponseForDifferentExtension()
    {
        const string responseJson = """
            {
              "results": [
                {
                  "extensions": [
                    {
                      "extensionName": "different-extension",
                      "publisher": {
                        "publisherName": "different-publisher"
                      },
                      "versions": [
                        {
                          "version": "99.0.0",
                          "properties": []
                        }
                      ]
                    }
                  ]
                }
              ]
            }
            """;
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
        };
        using var handler = new MockHttpMessageHandler((_, _) => Task.FromResult(response));
        using var httpClient = new HttpClient(handler);
        var client = new VsCodeExtensionMarketplaceClient(httpClient, TimeProvider.System);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => client.GetLatestVersionsAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetLatestVersionsAsync_RejectsOversizedResponseBeforeParsing()
    {
        const string responseJson = """
            {
              "results": [
                {
                  "extensions": [
                    {
                      "extensionName": "aspire-vscode",
                      "publisher": {
                        "publisherName": "microsoft-aspire"
                      },
                      "versions": [
                        {
                          "version": "1.16.0",
                          "properties": []
                        }
                      ]
                    }
                  ]
                }
              ]
            }
            """;
        var oversizedResponse = new string(' ', 2 * 1024 * 1024) + responseJson;
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(oversizedResponse, Encoding.UTF8, "application/json")
        };
        using var handler = new MockHttpMessageHandler((_, _) => Task.FromResult(response));
        using var httpClient = new HttpClient(handler);
        var client = new VsCodeExtensionMarketplaceClient(httpClient, TimeProvider.System);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => client.GetLatestVersionsAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetLatestVersionsAsync_ThrowsJsonExceptionForInvalidResponse()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{", Encoding.UTF8, "application/json")
        };
        using var handler = new MockHttpMessageHandler((_, _) => Task.FromResult(response));
        using var httpClient = new HttpClient(handler);
        var client = new VsCodeExtensionMarketplaceClient(httpClient, TimeProvider.System);

        await Assert.ThrowsAnyAsync<JsonException>(
            () => client.GetLatestVersionsAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetLatestVersionsAsync_ThrowsTimeoutException_WhenBoundedTimeoutExpires()
    {
        var timeProvider = new FakeTimeProvider();
        var requestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new MockHttpMessageHandler(async (_, cancellationToken) =>
        {
            requestStarted.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var httpClient = new HttpClient(handler);
        var timeout = TimeSpan.FromSeconds(5);
        var client = new VsCodeExtensionMarketplaceClient(httpClient, timeProvider, timeout);

        var requestTask = client.GetLatestVersionsAsync(TestContext.Current.CancellationToken);
        await requestStarted.Task;
        timeProvider.Advance(timeout);

        var exception = await Assert.ThrowsAsync<TimeoutException>(() => requestTask);
        Assert.Equal("The VS Code Marketplace request timed out after 5 seconds.", exception.Message);
    }

    [Fact]
    public async Task GetLatestVersionsAsync_ThrowsTimeoutException_WhenResponseBodyStallsAfterHeaders()
    {
        var timeProvider = new FakeTimeProvider();
        var bodyReadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // ResponseHeadersRead completes the send as soon as the headers arrive, so this stall happens
        // strictly after SendAsync returned. The private timeout still has to surface as a
        // TimeoutException, because doctor drops the check entirely on a bare cancellation.
        using var handler = new MockHttpMessageHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new StallingReadStream(bodyReadStarted))
            }));
        using var httpClient = new HttpClient(handler);
        var timeout = TimeSpan.FromSeconds(5);
        var client = new VsCodeExtensionMarketplaceClient(httpClient, timeProvider, timeout);

        var requestTask = client.GetLatestVersionsAsync(TestContext.Current.CancellationToken);
        await bodyReadStarted.Task;
        timeProvider.Advance(timeout);

        var exception = await Assert.ThrowsAsync<TimeoutException>(() => requestTask);
        Assert.Equal("The VS Code Marketplace request timed out after 5 seconds.", exception.Message);
    }

    [Fact]
    public async Task GetLatestVersionsAsync_PropagatesCallerCancellationDuringResponseBodyRead()
    {
        var timeProvider = new FakeTimeProvider();
        var bodyReadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new MockHttpMessageHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new StallingReadStream(bodyReadStarted))
            }));
        using var httpClient = new HttpClient(handler);
        var client = new VsCodeExtensionMarketplaceClient(httpClient, timeProvider, TimeSpan.FromHours(1));
        using var cancellationTokenSource = new CancellationTokenSource();

        var requestTask = client.GetLatestVersionsAsync(cancellationTokenSource.Token);
        await bodyReadStarted.Task;
        await cancellationTokenSource.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => requestTask);
    }

    [Fact]
    public async Task GetLatestVersionsAsync_PropagatesCallerCancellation()
    {
        var timeProvider = new FakeTimeProvider();
        var requestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new MockHttpMessageHandler(async (_, cancellationToken) =>
        {
            requestStarted.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var httpClient = new HttpClient(handler);
        var client = new VsCodeExtensionMarketplaceClient(httpClient, timeProvider, TimeSpan.FromHours(1));
        using var cancellationTokenSource = new CancellationTokenSource();

        var requestTask = client.GetLatestVersionsAsync(cancellationTokenSource.Token);
        await requestStarted.Task;
        await cancellationTokenSource.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => requestTask);
    }
}
