// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using Aspire.Dashboard.Configuration;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Utils;
using Aspire.Hosting;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Xunit;

namespace Aspire.Dashboard.Tests.Integration;

public partial class StaticAssetsTests(ITestOutputHelper testOutputHelper)
{
    [Fact]
    public async Task Html_ReferencesFingerprintedAssets_WithImmutableCaching()
    {
        // Build manifests (used under dotnet test) activate ASP.NET Core's development runtime handler,
        // which overrides Cache-Control to no-cache unless EnableStaticAssetsDevelopmentCaching is set.
        // Published builds emit a Publish manifest and serve the manifest's immutable headers by default.
        await using var app = IntegrationTestHelpers.CreateDashboardWebApplication(testOutputHelper, config =>
        {
            config["EnableStaticAssetsDevelopmentCaching"] = "true";
        });
        await app.StartAsync().DefaultTimeout();

        using var httpClientHandler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.None };
        using var client = new HttpClient(httpClientHandler) { BaseAddress = new Uri($"http://{app.FrontendSingleEndPointAccessor().EndPoint}") };

        var html = await client.GetStringAsync(DashboardUrls.StructuredLogsBasePath).DefaultTimeout();

        var blazorScriptPath = ExtractRequiredFingerprintedAsset(html, BlazorWebScriptRegex());
        var appScriptPath = ExtractRequiredFingerprintedAsset(html, AppScriptRegex());
        var themeScriptPath = ExtractRequiredFingerprintedAsset(html, AppThemeScriptRegex());
        var layoutCssPath = ExtractRequiredFingerprintedAsset(html, LayoutCssRegex());

        foreach (var assetPath in new[] { blazorScriptPath, appScriptPath, themeScriptPath, layoutCssPath })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"/{assetPath}");
            request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
            using var response = await client.SendAsync(request).DefaultTimeout();

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.NotNull(response.Headers.ETag);
            Assert.Equal(TimeSpan.FromDays(365), response.Headers.CacheControl?.MaxAge);
            Assert.Contains("immutable", response.Headers.CacheControl?.Extensions.Select(e => e.Name) ?? []);
            Assert.Contains("gzip", response.Content.Headers.ContentEncoding);
            Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        }
    }

    [Theory]
    [InlineData("/_framework/blazor.web.js")]
    [InlineData("/js/app.js")]
    [InlineData("/fonts/geist/Geist-Variable.woff2")]
    public async Task UnfingerprintedAssetRoutes_ReturnOkAndSupportConditionalRequests(string path)
    {
        await using var app = IntegrationTestHelpers.CreateDashboardWebApplication(testOutputHelper);
        await app.StartAsync().DefaultTimeout();

        using var client = new HttpClient { BaseAddress = new Uri($"http://{app.FrontendSingleEndPointAccessor().EndPoint}") };

        using var response = await client.GetAsync(path).DefaultTimeout();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(response.Headers.ETag);

        using var conditionalRequest = new HttpRequestMessage(HttpMethod.Get, path);
        conditionalRequest.Headers.IfNoneMatch.Add(response.Headers.ETag);
        using var conditionalResponse = await client.SendAsync(conditionalRequest).DefaultTimeout();
        Assert.Equal(HttpStatusCode.NotModified, conditionalResponse.StatusCode);
    }

    [Fact]
    public async Task StaticAssets_AllowAnonymousWhenBrowserTokenAuthEnabled()
    {
        await using var app = IntegrationTestHelpers.CreateDashboardWebApplication(testOutputHelper, config =>
        {
            config[DashboardConfigNames.DashboardFrontendAuthModeName.ConfigKey] = FrontendAuthMode.BrowserToken.ToString();
            config[DashboardConfigNames.DashboardFrontendBrowserTokenName.ConfigKey] = "TestKey123!";
        });
        await app.StartAsync().DefaultTimeout();

        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { BaseAddress = new Uri($"http://{app.FrontendSingleEndPointAccessor().EndPoint}") };

        var loginHtml = await client.GetStringAsync(DashboardUrls.LoginUrl()).DefaultTimeout();
        var layoutCssPath = ExtractRequiredFingerprintedAsset(loginHtml, LayoutCssRegex());

        using var cssResponse = await client.GetAsync($"/{layoutCssPath}").DefaultTimeout();
        Assert.Equal(HttpStatusCode.OK, cssResponse.StatusCode);

        using var blazorResponse = await client.GetAsync("/_framework/blazor.web.js").DefaultTimeout();
        Assert.Equal(HttpStatusCode.OK, blazorResponse.StatusCode);
    }

    [Fact]
    public async Task BrowserThemeResolver_ImportsFingerprintedThemeModule()
    {
        await using var app = IntegrationTestHelpers.CreateDashboardWebApplication(testOutputHelper);
        await app.StartAsync().DefaultTimeout();

        using var client = new HttpClient { BaseAddress = new Uri($"http://{app.FrontendSingleEndPointAccessor().EndPoint}") };
        var html = await client.GetStringAsync(DashboardUrls.StructuredLogsBasePath).DefaultTimeout();
        var expectedThemeScriptPath = $"/{ExtractRequiredFingerprintedAsset(html, AppThemeScriptRegex())}";

        var endpointDataSource = app.Services.GetRequiredService<EndpointDataSource>();
        var jsRuntime = new RecordingThemeJsRuntime();
        await using var resolver = new BrowserThemeResolver(jsRuntime, endpointDataSource);

        var settings = await resolver.GetThemeSettingsAsync(CancellationToken.None).DefaultTimeout();

        Assert.Equal(expectedThemeScriptPath, jsRuntime.ImportedModulePath);
        Assert.Equal(ThemeManager.ThemeSettingDark, settings.EffectiveTheme);
        Assert.Equal(ThemeManager.ThemeSettingDark, settings.SelectedTheme);
    }

    [Fact]
    public async Task BrowserThemeResolver_WithoutEndpointDataSource_FallsBackToUnfingerprintedPath()
    {
        var jsRuntime = new RecordingThemeJsRuntime();
        await using var resolver = new BrowserThemeResolver(jsRuntime);

        await resolver.GetThemeSettingsAsync(CancellationToken.None).DefaultTimeout();

        Assert.Equal("/js/app-theme.js", jsRuntime.ImportedModulePath);
    }

    private static string ExtractRequiredFingerprintedAsset(string html, Regex regex)
    {
        var match = regex.Match(html);
        Assert.True(match.Success, $"Expected HTML to reference fingerprinted asset matching '{regex}'.");
        return match.Groups["path"].Value;
    }

    [GeneratedRegex(@"src=""(?<path>_framework/blazor\.web\.[a-z0-9]+\.js)""")]
    private static partial Regex BlazorWebScriptRegex();

    [GeneratedRegex(@"src=""(?<path>js/app\.[a-z0-9]+\.js)""")]
    private static partial Regex AppScriptRegex();

    [GeneratedRegex(@"src=""(?<path>js/app-theme\.[a-z0-9]+\.js)""")]
    private static partial Regex AppThemeScriptRegex();

    [GeneratedRegex(@"href=""(?<path>css/layout\.[a-z0-9]+\.css)""")]
    private static partial Regex LayoutCssRegex();

    private sealed class RecordingThemeJsRuntime : IJSRuntime
    {
        public string? ImportedModulePath { get; private set; }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        {
            Assert.Equal("import", identifier);
            ImportedModulePath = Assert.Single(args!) as string;
            return ValueTask.FromResult((TValue)(object)new FakeThemeJsModule());
        }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            Assert.Equal("import", identifier);
            ImportedModulePath = Assert.Single(args!) as string;
            return ValueTask.FromResult((TValue)(object)new FakeThemeJsModule());
        }
    }

    private sealed class FakeThemeJsModule : IJSObjectReference
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
            => identifier switch
            {
                "getCurrentTheme" => ValueTask.FromResult((TValue)(object)ThemeManager.ThemeSettingDark),
                "getThemeCookieValue" => ValueTask.FromResult((TValue)(object)ThemeManager.ThemeSettingDark),
                _ => throw new InvalidOperationException($"Unexpected JS call '{identifier}'.")
            };

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
            => identifier switch
            {
                "getCurrentTheme" => ValueTask.FromResult((TValue)(object)ThemeManager.ThemeSettingDark),
                "getThemeCookieValue" => ValueTask.FromResult((TValue)(object)ThemeManager.ThemeSettingDark),
                _ => throw new InvalidOperationException($"Unexpected JS call '{identifier}'.")
            };

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
