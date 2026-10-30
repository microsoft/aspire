// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using Aspire.Cli.Mcp.Tools;
using Aspire.Cli.Tests.Utils;
using Aspire.Dashboard.Utils;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;

namespace Aspire.Cli.Tests.Mcp;

public class McpToolHelpersTests
{
    [Fact]
    public void GetBoundedExceptionDiagnostic_ReturnsOnlySafeTypeAndStatus()
    {
        var exception = new HttpRequestException(
            HttpRequestError.ConnectionError,
            "Request failed at https://request-user:request-password@example.com?token=request-secret",
            inner: null,
            HttpStatusCode.ServiceUnavailable);

        var diagnostic = McpToolHelpers.GetBoundedExceptionDiagnostic(exception);

        Assert.Equal(
            "HttpRequestException; HTTP 503 (ServiceUnavailable)",
            diagnostic);
    }

    [Fact]
    public async Task StaticDashboardInfoProvider_PreservesExplicitRequestAuthenticationAndSanitizesDisplayUrl()
    {
        var provider = new StaticDashboardInfoProvider(
            "https://request-user:request-password@example.localhost:8443/base/login" +
            "?t=dashboard-secret&accessKey=request-secret&view=resources#request-fragment",
            apiKey: "api-key");

        var (_, apiBaseUrl, dashboardBaseUrl) = await provider.GetDashboardInfoAsync(TestContext.Current.CancellationToken);

        AssertDashboardRequestUrlEqual(
            "https://request-user:request-password@localhost:8443/base" +
            "?t=dashboard-secret&accessKey=request-secret&view=resources#request-fragment",
            apiBaseUrl);
        Assert.Equal("https://example.localhost:8443/base?view=resources", dashboardBaseUrl);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("http://localhost:18888", "http://localhost:18888")]
    [InlineData("http://localhost:18888/", "http://localhost:18888")]
    [InlineData("http://localhost:18888/login", "http://localhost:18888")]
    [InlineData("http://localhost:18888/login?t=authtoken123", "http://localhost:18888")]
    [InlineData("https://localhost:16319/login?t=d8d8255df4c79aebcb5b7325828ccb20", "https://localhost:16319")]
    [InlineData("https://example.com:8080/path/to/resource?param=value", "https://example.com:8080/path/to/resource?param=value")]
    [InlineData("https://example.com:8080/dashboard", "https://example.com:8080/dashboard")]
    [InlineData("http://localhost/base/login", "http://localhost/base")]
    [InlineData("http://localhost/base/login?t=token123", "http://localhost/base")]
    [InlineData("https://example.com:8080/app/deep/login?t=abc", "https://example.com:8080/app/deep")]
    [InlineData("invalid-url", null)]
    public void StripLoginPath_RemovesOnlyLoginSegment(string? input, string? expected)
    {
        var result = McpToolHelpers.StripLoginPath(input);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("http://localhost:18888/login?t=authtoken123&view=resources", "http://localhost:18888?view=resources")]
    [InlineData("http://localhost/base/login?T=authtoken123&view=resources", "http://localhost/base?view=resources")]
    public void StripLoginPath_RemovesLoginTokenAndPreservesNonSensitiveQuery(string input, string expected)
    {
        var result = McpToolHelpers.StripLoginPath(input);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void NormalizeDashboardUrl_RemovesUriUserInfo()
    {
        var result = McpToolHelpers.NormalizeDashboardUrl("https://user:password@example.com:8443/dashboard?view=resources");

        Assert.Equal("https://example.com:8443/dashboard?view=resources", result);
    }

    [Fact]
    public void NormalizeDashboardUrl_RemovesSensitiveQueryKeysAndPreservesOtherQuery()
    {
        var result = McpToolHelpers.NormalizeDashboardUrl(
            "https://example.com/dashboard?view=resources&TOKEN=one&access_token=two&accessToken=two-b&Credential=three&PASSWORD=four&key=five&API_KEY=six&apiKey=six-b&sharedAccessKey=six-c&secret=seven&sig=eight&code=nine&client_secret=ten&clientSecret=ten-b&X-Amz-Credential=eleven&X-Goog-Signature=twelve&limit=20");

        Assert.Equal("https://example.com/dashboard?view=resources&limit=20", result);
    }

    [Fact]
    public void SanitizeUrl_RemovesSensitiveAliasesSuffixesAndSemicolonSeparatedValues()
    {
        var result = McpToolHelpers.SanitizeUrl(
            "https://example.com/dashboard?view=resources&monkey=banana&accessKey=one;AUTHKEY=two&deviceKey=three" +
            "&callbackSig=four&PWD=five&passwd=six&auth=seven&authorization=eight" +
            "&jwt=nine&bearer=ten&sessionid=eleven&sas=twelve&limit=20");

        Assert.Equal("https://example.com/dashboard?view=resources&monkey=banana&limit=20", result);
    }

    [Fact]
    public void SanitizeUrl_PreservesSemicolonInsideSafeRawValue()
    {
        var result = McpToolHelpers.SanitizeUrl("https://example.com/dashboard?q=a;b");

        Assert.Equal("https://example.com/dashboard?q=a;b", result);
    }

    [Fact]
    public void SanitizeUrl_RemovesSemicolonDelimitedSensitiveKeyWithoutChangingSafeValue()
    {
        var result = McpToolHelpers.SanitizeUrl(
            "https://example.com/dashboard?q=a;b;token=secret-value&view=resources");

        Assert.Equal("https://example.com/dashboard?view=resources", result);
    }

    [Theory]
    [InlineData("apikey=AB;cd=ef&view=resources")]
    [InlineData("q=visible;apikey=AB;cd=ef&view=resources")]
    public void SanitizeUrl_DropsWholeAmpersandGroupContainingSensitiveSemicolonSegment(string query)
    {
        var result = McpToolHelpers.SanitizeUrl($"https://example.com/dashboard?{query}");

        Assert.Equal("https://example.com/dashboard?view=resources", result);
    }

    [Theory]
    [InlineData("tcp://cache.example.com:6379", "tcp://cache.example.com:6379")]
    [InlineData("udp://dns.example.com:53", "udp://dns.example.com:53")]
    [InlineData("ws://events.example.com/socket", "ws://events.example.com/socket")]
    [InlineData("wss://events.example.com/socket", "wss://events.example.com/socket")]
    [InlineData("postgresql://db.example.com:5432/catalog", "postgresql://db.example.com:5432/catalog")]
    public void SanitizeResourceUrl_PreservesHostBearingAbsoluteUris(string input, string expected)
    {
        Assert.Equal(expected, McpToolHelpers.SanitizeResourceUrl(input));
    }

    [Fact]
    public void SanitizeResourceUrl_RemovesCredentialsFragmentAndSensitiveQuery()
    {
        var input =
            "tcp://" + "resource-user" + ":" + "resource-password" +
            "@cache.example.com:6379?view=summary&accessKey=resource-secret#resource-fragment";

        var result = McpToolHelpers.SanitizeResourceUrl(input);

        Assert.Equal("tcp://cache.example.com:6379?view=summary", result);
    }

    [Theory]
    [InlineData("file:///Users/example/private.txt")]
    [InlineData("file://server/private/share")]
    [InlineData("localhost:18888")]
    [InlineData("/repo/private.txt")]
    [InlineData(@"C:\repo\secret.txt")]
    [InlineData("mailto:user@example.com")]
    [InlineData("urn:example:resource")]
    [InlineData("not a URL")]
    public void SanitizeResourceUrl_ReturnsNullForFileMalformedAndHostlessValues(string input)
    {
        Assert.Null(McpToolHelpers.SanitizeResourceUrl(input));
    }

    [Theory]
    [InlineData("file:///Users/example/private.txt")]
    [InlineData("ftp://example.com/private")]
    [InlineData("tcp://cache.example.com:6379")]
    [InlineData("localhost:18888")]
    [InlineData(@"C:\repo\secret.txt")]
    public void SanitizeUrl_ReturnsNullForNonHttpUrls(string input)
    {
        Assert.Null(McpToolHelpers.SanitizeUrl(input));
    }

    [Fact]
    public void NormalizeDashboardUrl_DoesNotEchoInvalidUrl()
    {
        const string secretUrl = "not a URL user:password token=secret-value";

        var result = McpToolHelpers.NormalizeDashboardUrl(secretUrl);

        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void NormalizeDashboardUrl_RemovesFragment()
    {
        var result = McpToolHelpers.NormalizeDashboardUrl(
            "https://example.com/dashboard?view=resources#access_token=fragment-secret");

        Assert.Equal("https://example.com/dashboard?view=resources", result);
    }

    [Fact]
    public async Task StaticDashboardInfoProvider_SanitizesDisplayUrl()
    {
        var provider = new StaticDashboardInfoProvider(
            "https://dashboard-user:dashboard-password@example.com:8443/login?t=dashboard-secret&view=resources",
            apiKey: "api-key");

        var (_, apiBaseUrl, dashboardBaseUrl) = await provider.GetDashboardInfoAsync(TestContext.Current.CancellationToken);

        var apiBaseUri = new Uri(apiBaseUrl);
        Assert.Equal("example.com", apiBaseUri.Host);
        Assert.NotEmpty(apiBaseUri.UserInfo);
        Assert.Equal("?view=resources", apiBaseUri.Query);
        Assert.Equal("https://example.com:8443?view=resources", dashboardBaseUrl);
        var resourcesUri = new Uri(DashboardUrls.TelemetryResourcesApiUrl(apiBaseUrl));
        Assert.Equal("/api/telemetry/resources", resourcesUri.AbsolutePath);
        Assert.Equal("?view=resources", resourcesUri.Query);
    }

    [Fact]
    public async Task StaticDashboardInfoProvider_RejectsInvalidDashboardUrl()
    {
        var provider = new StaticDashboardInfoProvider("not a dashboard URL", apiKey: null);

        var exception = await Assert.ThrowsAsync<McpProtocolException>(
            () => provider.GetDashboardInfoAsync(TestContext.Current.CancellationToken));

        Assert.Equal(McpErrorCode.InvalidParams, exception.ErrorCode);
        Assert.Equal("The dashboard URL must be an absolute HTTP or HTTPS URL.", exception.Message);
    }

    [Fact]
    public async Task StaticDashboardInfoProvider_ExchangesBrowserTokenForApiKey()
    {
        using var handler = new MockHttpMessageHandler(request =>
        {
            Assert.Equal(
                "https://localhost:18888/api/telemetry/validateToken?view=resources",
                request.RequestUri?.AbsoluteUri);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"apiKey":"exchanged-api-key"}""", System.Text.Encoding.UTF8, "application/json")
            };
        });
        var provider = new StaticDashboardInfoProvider(
            "https://localhost:18888/login?t=browser-token&view=resources",
            apiKey: null,
            new MockHttpClientFactory(handler),
            NullLogger.Instance);

        var (apiToken, apiBaseUrl, dashboardBaseUrl) = await provider.GetDashboardInfoAsync(TestContext.Current.CancellationToken);

        Assert.Equal("exchanged-api-key", apiToken);
        Assert.Equal("https://localhost:18888?view=resources", apiBaseUrl);
        Assert.Equal("https://localhost:18888?view=resources", dashboardBaseUrl);
    }

    private static void AssertDashboardRequestUrlEqual(string? expected, string? actual)
    {
        Assert.Equal(
            DashboardUrls.RemoveDashboardLoginToken(expected),
            DashboardUrls.RemoveDashboardLoginToken(actual));
    }

}
