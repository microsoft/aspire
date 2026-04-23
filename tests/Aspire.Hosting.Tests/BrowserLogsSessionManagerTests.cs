// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.WebSockets;
using Aspire.Hosting.Tests.Utils;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Hosting.Tests;

[Trait("Partition", "2")]
public class BrowserLogsSessionManagerTests
{
    [Fact]
    public void TryParseBrowserDebugEndpoint_ReturnsBrowserWebSocketUri()
    {
        var endpoint = BrowserLogsDebugEndpointParser.TryParseBrowserDebugEndpoint("""
            51943
            /devtools/browser/4c8404fb-06f8-45f0-9d89-112233445566
            """);

        Assert.NotNull(endpoint);
        Assert.Equal("ws://127.0.0.1:51943/devtools/browser/4c8404fb-06f8-45f0-9d89-112233445566", endpoint.AbsoluteUri);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-port")]
    [InlineData("51943")]
    public void TryParseBrowserDebugEndpoint_ReturnsNullForInvalidMetadata(string metadata)
    {
        var endpoint = BrowserLogsDebugEndpointParser.TryParseBrowserDebugEndpoint(metadata);

        Assert.Null(endpoint);
    }

    [Fact]
    public void TryResolveBrowserUserDataDirectory_ReturnsExpectedPathForKnownBrowser()
    {
        var expectedPath = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Google", "Chrome", "User Data")
            : OperatingSystem.IsMacOS()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "Google", "Chrome")
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "google-chrome");

        var browserExecutable = OperatingSystem.IsWindows()
            ? "chrome.exe"
            : OperatingSystem.IsMacOS()
                ? "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome"
                : "google-chrome";

        var userDataDirectory = BrowserLogsRunningSession.TryResolveBrowserUserDataDirectory("chrome", browserExecutable);

        Assert.Equal(expectedPath, userDataDirectory);
    }

    [Fact]
    public void TryResolveBrowserUserDataDirectory_ReturnsNullForUnknownBrowser()
    {
        var userDataDirectory = BrowserLogsRunningSession.TryResolveBrowserUserDataDirectory("custom-browser", "/opt/custom-browser");

        Assert.Null(userDataDirectory);
    }

    [Fact]
    public void TryResolveBrowserUserDataDirectory_UsesChromiumPathOnLinux()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var expectedPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "chromium");

        var userDataDirectory = BrowserLogsRunningSession.TryResolveBrowserUserDataDirectory("chrome", "/usr/bin/chromium");

        Assert.Equal(expectedPath, userDataDirectory);
    }

    [Fact]
    public void TrySelectTrackedTargetId_PrefersUnattachedBlankPage()
    {
        var targetId = BrowserLogsRunningSession.TrySelectTrackedTargetId(
        [
            new BrowserLogsTargetInfo { TargetId = "restored-page", Type = "page", Url = "https://example.com", Attached = false },
            new BrowserLogsTargetInfo { TargetId = "service-worker", Type = "service_worker", Url = "https://example.com/sw.js", Attached = false },
            new BrowserLogsTargetInfo { TargetId = "launcher-page", Type = "page", Url = "about:blank", Attached = false }
        ]);

        Assert.Equal("launcher-page", targetId);
    }

    [Fact]
    public void TrySelectTrackedTargetId_FallsBackToFirstUnattachedPage()
    {
        var targetId = BrowserLogsRunningSession.TrySelectTrackedTargetId(
        [
            new BrowserLogsTargetInfo { TargetId = "attached-page", Type = "page", Url = "about:blank", Attached = true },
            new BrowserLogsTargetInfo { TargetId = "fallback-page", Type = "page", Url = "chrome://newtab/", Attached = false }
        ]);

        Assert.Equal("fallback-page", targetId);
    }

    [Fact]
    public async Task BrowserConnectionDiagnosticsLogger_LogsConnectionProblems()
    {
        var resourceLoggerService = ConsoleLoggingTestHelpers.GetResourceLoggerService();
        var resourceName = "web-browser-logs";
        var diagnostics = new BrowserConnectionDiagnosticsLogger("session-0001", resourceLoggerService.GetLogger(resourceName));

        var logs = await CaptureLogsAsync(resourceLoggerService, resourceName, targetLogCount: 4, () =>
        {
            diagnostics.LogSetupFailure(
                "Setting up the tracked browser debug connection",
                new InvalidOperationException("Connecting to the tracked browser debug endpoint failed.", new TimeoutException("Timed out waiting for a tracked browser protocol response to 'Target.attachToTarget'.")));
            diagnostics.LogConnectionLost(
                new InvalidOperationException("Browser debug connection closed by the remote endpoint with status 'EndpointUnavailable' (1001): browser crashed"));
            diagnostics.LogReconnectAttemptFailed(
                2,
                new InvalidOperationException("Attaching to the tracked browser target failed.", new TimeoutException("Timed out waiting for a tracked browser protocol response to 'Target.attachToTarget'.")));
            diagnostics.LogReconnectFailed(
                new InvalidOperationException("Connecting to the tracked browser debug endpoint failed.", new WebSocketException("Connection refused")));
        });

        Assert.Collection(
            logs,
            log => Assert.Equal(
                "2000-12-29T20:59:59.0000000Z [session-0001] Setting up the tracked browser debug connection failed: InvalidOperationException: Connecting to the tracked browser debug endpoint failed. --> TimeoutException: Timed out waiting for a tracked browser protocol response to 'Target.attachToTarget'.",
                log.Content),
            log => Assert.Equal(
                "2000-12-29T20:59:59.0000000Z [session-0001] Tracked browser debug connection lost: InvalidOperationException: Browser debug connection closed by the remote endpoint with status 'EndpointUnavailable' (1001): browser crashed. Attempting to reconnect.",
                log.Content),
            log => Assert.Equal(
                "2000-12-29T20:59:59.0000000Z [session-0001] Reconnect attempt 2 failed: InvalidOperationException: Attaching to the tracked browser target failed. --> TimeoutException: Timed out waiting for a tracked browser protocol response to 'Target.attachToTarget'.",
                log.Content),
            log => Assert.Equal(
                 "2000-12-29T20:59:59.0000000Z [session-0001] Unable to reconnect tracked browser debug connection. Closing the tracked browser session. Last error: InvalidOperationException: Connecting to the tracked browser debug endpoint failed. --> WebSocketException: Connection refused",
                 log.Content));
    }

    [Fact]
    public async Task StartSessionAsync_ThrowsWhenManagerIsDisposing()
    {
        var sessionFactory = new ThrowIfCalledSessionFactory();
        var manager = new BrowserLogsSessionManager(
            ConsoleLoggingTestHelpers.GetResourceLoggerService(),
            ResourceNotificationServiceTestHelpers.Create(),
            TimeProvider.System,
            NullLogger<BrowserLogsSessionManager>.Instance,
            sessionFactory);
        var resource = new BrowserLogsResource(
            "web-browser-logs",
            new TestResourceWithEndpoints("web"),
            new BrowserLogsSettings("chrome", null),
            browserOverride: null,
            profileOverride: null);

        await manager.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => manager.StartSessionAsync(
            resource,
            new BrowserLogsSettings("chrome", null),
            resource.Name,
            new Uri("https://localhost"),
            CancellationToken.None));

        Assert.False(sessionFactory.WasCalled);
    }

    private static Task<IReadOnlyList<LogLine>> CaptureLogsAsync(ResourceLoggerService resourceLoggerService, string resourceName, int targetLogCount, Action writeLogs) =>
        ConsoleLoggingTestHelpers.CaptureLogsAsync(resourceLoggerService, resourceName, targetLogCount, writeLogs);

    private sealed class ThrowIfCalledSessionFactory : IBrowserLogsRunningSessionFactory
    {
        public bool WasCalled { get; private set; }

        public Task<IBrowserLogsRunningSession> StartSessionAsync(
            BrowserLogsSettings settings,
            string resourceName,
            Uri url,
            string sessionId,
            ILogger resourceLogger,
            CancellationToken cancellationToken)
        {
            WasCalled = true;
            throw new InvalidOperationException("StartSessionAsync should not be called after disposal.");
        }
    }

    private sealed class TestResourceWithEndpoints(string name) : Resource(name), IResourceWithEndpoints;
}
