// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using Aspire.Dashboard.Configuration;
using Aspire.Dashboard.Model;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Options;
using Microsoft.JSInterop;
using Xunit;

namespace Aspire.Dashboard.Tests.Terminal;

public class TerminalWindowLauncherTests
{
    private const string TerminalPath = "/terminal-window/apphost/abc123";

    [Fact]
    public async Task OpenAsync_BrowserToken_RoutesThroughLoginSoTheWindowAuthenticatesItself()
    {
        var js = new TestJSRuntime();
        var launcher = CreateLauncher(js, CreateOptions(FrontendAuthMode.BrowserToken, "s3cret token"));

        await launcher.OpenAsync(key: "terminal:abc123", path: TerminalPath).DefaultTimeout();

        // The token and the return path are both escaped, so a token containing URL-significant characters cannot
        // truncate the query or smuggle in extra parameters.
        Assert.Equal(
            "http://localhost/login?t=s3cret%20token&returnUrl=%2Fterminal-window%2Fapphost%2Fabc123",
            js.LastWindowUrl);
    }

    [Fact]
    public async Task OpenAsync_TokenAndPathContainQueryDelimiters_EscapingSurvivesUriResolution()
    {
        var js = new TestJSRuntime();
        var launcher = CreateLauncher(js, CreateOptions(FrontendAuthMode.BrowserToken, "a&b=c#d"));

        await launcher.OpenAsync(key: "terminal:abc123", path: "/terminal-window/resource/my&app/0").DefaultTimeout();

        // Resolving against the base URI must not decode the escapes, or the '&' and '#' would split the query and
        // the login middleware would see a truncated token and a truncated returnUrl.
        Assert.Equal(
            "http://localhost/login?t=a%26b%3Dc%23d&returnUrl=%2Fterminal-window%2Fresource%2Fmy%26app%2F0",
            js.LastWindowUrl);
    }

    [Theory]
    [InlineData(FrontendAuthMode.Unsecured, null)]
    [InlineData(FrontendAuthMode.OpenIdConnect, null)]
    // OIDC re-runs the authorization code flow in the new window, so a token would be meaningless even if configured.
    [InlineData(FrontendAuthMode.OpenIdConnect, "ignored-token")]
    // A BrowserToken frontend without a token cannot hand anything over; the empty token must not reach the URL.
    [InlineData(FrontendAuthMode.BrowserToken, "")]
    public async Task OpenAsync_NoTokenToHandOver_OpensTheTerminalDirectly(FrontendAuthMode authMode, string? token)
    {
        var js = new TestJSRuntime();
        var launcher = CreateLauncher(js, CreateOptions(authMode, token));

        await launcher.OpenAsync(key: "terminal:abc123", path: TerminalPath).DefaultTimeout();

        Assert.Equal("http://localhost/terminal-window/apphost/abc123", js.LastWindowUrl);
    }

    [Theory]
    [InlineData("opened", TerminalWindowOpenResult.Opened)]
    [InlineData("focused", TerminalWindowOpenResult.Focused)]
    [InlineData("blocked", TerminalWindowOpenResult.Blocked)]
    public async Task OpenAsync_MapsJavaScriptOutcome(string jsResult, TerminalWindowOpenResult expected)
    {
        var js = new TestJSRuntime { OpenResult = jsResult };
        var launcher = CreateLauncher(js, CreateOptions(FrontendAuthMode.Unsecured, token: null));

        var result = await launcher.OpenAsync(key: "terminal:abc123", path: TerminalPath).DefaultTimeout();

        Assert.Equal(expected, result);
    }

    private static TerminalWindowLauncher CreateLauncher(IJSRuntime js, IOptionsMonitor<DashboardOptions> options)
        => new(js, new TestNavigationManager(), options, _ => Task.CompletedTask);

    private static IOptionsMonitor<DashboardOptions> CreateOptions(FrontendAuthMode authMode, string? token)
    {
        var options = new DashboardOptions
        {
            Frontend = { AuthMode = authMode, BrowserToken = token }
        };

        return new TestOptionsMonitor(options);
    }

    private sealed class TestNavigationManager : NavigationManager
    {
        public TestNavigationManager()
        {
            Initialize("http://localhost/", "http://localhost/");
        }
    }

    private sealed class TestOptionsMonitor(DashboardOptions options) : IOptionsMonitor<DashboardOptions>
    {
        public DashboardOptions CurrentValue { get; } = options;

        public DashboardOptions Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<DashboardOptions, string?> listener) => null;
    }

    /// <summary>
    /// Stands in for the browser, capturing the URL the launcher asked <c>app-terminalwindow.js</c> to open.
    /// </summary>
    private sealed class TestJSRuntime : IJSRuntime, IJSObjectReference
    {
        public string OpenResult { get; init; } = "opened";

        public string? LastWindowUrl { get; private set; }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        {
            // The launcher first imports the module, then calls into it. Both land here because this type doubles as
            // the module reference it hands back.
            if (identifier is "import")
            {
                return ValueTask.FromResult((TValue)(object)this);
            }

            if (identifier is "openTerminalWindow")
            {
                Assert.NotNull(args);
                LastWindowUrl = (string?)args[1];
                return ValueTask.FromResult((TValue)(object)OpenResult);
            }

            return ValueTask.FromResult(default(TValue)!);
        }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
            => InvokeAsync<TValue>(identifier, args);

        [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(TestJSRuntime))]
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
