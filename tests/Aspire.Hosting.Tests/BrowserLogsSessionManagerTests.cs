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
    public void BrowserHostIdentity_NormalizesTrailingDirectorySeparators()
    {
        WithTempUserDataDirectory(userDataDirectory =>
        {
            var executablePath = Path.Combine(userDataDirectory, "browser");
            var identity = new BrowserHostIdentity(executablePath, userDataDirectory);
            var identityWithTrailingSeparator = new BrowserHostIdentity(executablePath, userDataDirectory + Path.DirectorySeparatorChar);

            Assert.Equal(identity, identityWithTrailingSeparator);
            Assert.Equal(identity.GetHashCode(), identityWithTrailingSeparator.GetHashCode());
        });
    }

    [Fact]
    public void BrowserHostIdentity_DefaultValueDoesNotThrowWhenHashed()
    {
        var exception = Record.Exception(() => default(BrowserHostIdentity).GetHashCode());

        Assert.Null(exception);
    }

    [Fact]
    public async Task BrowserHostLease_ReleasesOnlyOnce()
    {
        var releaseCount = 0;
        var lease = new BrowserHostLease(new TestBrowserHost(), _ =>
        {
            releaseCount++;
            return ValueTask.CompletedTask;
        });

        await lease.DisposeAsync();
        await lease.DisposeAsync();

        Assert.Equal(1, releaseCount);
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
    public void IsGoogleChromeDefaultUserDataDirectory_ReturnsTrueForGoogleChromeDefaultPath()
    {
        var browserExecutable = OperatingSystem.IsWindows()
            ? @"C:\Program Files\Google\Chrome\Application\chrome.exe"
            : OperatingSystem.IsMacOS()
                ? "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome"
                : "/usr/bin/google-chrome";
        var userDataDirectory = BrowserLogsRunningSession.TryResolveBrowserUserDataDirectory("chrome", browserExecutable);

        Assert.NotNull(userDataDirectory);
        Assert.True(BrowserLogsRunningSession.IsGoogleChromeDefaultUserDataDirectory("chrome", browserExecutable, userDataDirectory));
    }

    [Fact]
    public void IsGoogleChromeDefaultUserDataDirectory_ReturnsFalseForChromium()
    {
        var browserExecutable = OperatingSystem.IsWindows()
            ? @"C:\Program Files\Chromium\Application\chrome.exe"
            : OperatingSystem.IsMacOS()
                ? "/Applications/Chromium.app/Contents/MacOS/Chromium"
                : "/usr/bin/chromium";
        var userDataDirectory = BrowserLogsRunningSession.TryResolveBrowserUserDataDirectory("chromium", browserExecutable);

        Assert.NotNull(userDataDirectory);
        Assert.False(BrowserLogsRunningSession.IsGoogleChromeDefaultUserDataDirectory("chromium", browserExecutable, userDataDirectory));
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
    public void ResolveBrowserProfileDirectory_MatchesDirectoryNameCaseInsensitively()
    {
        WithTempUserDataDirectory(userDataDirectory =>
        {
            Directory.CreateDirectory(Path.Combine(userDataDirectory, "Profile 1"));

            var profileDirectory = BrowserLogsRunningSession.ResolveBrowserProfileDirectory(userDataDirectory, "profile 1");

            Assert.Equal("Profile 1", profileDirectory);
        });
    }

    [Fact]
    public void ResolveBrowserProfileDirectory_MatchesProfileDisplayNameFromLocalState()
    {
        WithTempUserDataDirectory(userDataDirectory =>
        {
            Directory.CreateDirectory(Path.Combine(userDataDirectory, "Default"));
            Directory.CreateDirectory(Path.Combine(userDataDirectory, "Profile 1"));
            File.WriteAllText(
                Path.Combine(userDataDirectory, "Local State"),
                """
                {
                  "profile": {
                    "info_cache": {
                      "Default": {
                        "name": "Profile 1"
                      },
                      "Profile 1": {
                        "name": "Profile 2"
                      }
                    }
                  }
                }
                """);

            var profileDirectory = BrowserLogsRunningSession.ResolveBrowserProfileDirectory(userDataDirectory, "Profile 2");

            Assert.Equal("Profile 1", profileDirectory);
        });
    }

    [Fact]
    public void ResolveBrowserProfileDirectory_ThrowsWhenDisplayNameIsAmbiguous()
    {
        WithTempUserDataDirectory(userDataDirectory =>
        {
            Directory.CreateDirectory(Path.Combine(userDataDirectory, "Default"));
            Directory.CreateDirectory(Path.Combine(userDataDirectory, "Profile 1"));
            File.WriteAllText(
                Path.Combine(userDataDirectory, "Local State"),
                """
                {
                  "profile": {
                    "info_cache": {
                      "Default": {
                        "name": "Shared profile"
                      },
                      "Profile 1": {
                        "name": "Shared profile"
                      }
                    }
                  }
                }
                """);

            var exception = Assert.Throws<InvalidOperationException>(() => BrowserLogsRunningSession.ResolveBrowserProfileDirectory(userDataDirectory, "Shared profile"));

            Assert.Contains("matched multiple Chromium profiles", exception.Message);
        });
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
            new BrowserLogsSettings("chrome", null, BrowserUserDataMode.Isolated),
            browserOverride: null,
            profileOverride: null,
            userDataModeOverride: null);

        await manager.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => manager.StartSessionAsync(
            resource,
            new BrowserLogsSettings("chrome", null, BrowserUserDataMode.Isolated),
            resource.Name,
            new Uri("https://localhost"),
            CancellationToken.None));

        Assert.False(sessionFactory.WasCalled);
    }

    private static Task<IReadOnlyList<LogLine>> CaptureLogsAsync(ResourceLoggerService resourceLoggerService, string resourceName, int targetLogCount, Action writeLogs) =>
        ConsoleLoggingTestHelpers.CaptureLogsAsync(resourceLoggerService, resourceName, targetLogCount, writeLogs);

    private static void WithTempUserDataDirectory(Action<string> action)
    {
        var userDataDirectory = Directory.CreateTempSubdirectory();
        try
        {
            action(userDataDirectory.FullName);
        }
        finally
        {
            userDataDirectory.Delete(recursive: true);
        }
    }

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

    private sealed class TestBrowserHost : IBrowserHost
    {
        public BrowserHostIdentity Identity { get; } = new(
            Path.Combine(AppContext.BaseDirectory, "browser"),
            Path.Combine(AppContext.BaseDirectory, "user-data"));

        public BrowserHostOwnership Ownership => BrowserHostOwnership.Owned;

        public Uri DebugEndpoint { get; } = new("ws://127.0.0.1/devtools/browser/test");

        public int? ProcessId => 1;

        public string BrowserDisplayName => "Test";

        public Task Termination { get; } = Task.CompletedTask;

        public Task<IBrowserTargetSession> CreateTargetSessionAsync(
            string sessionId,
            Uri url,
            Func<BrowserLogsProtocolEvent, ValueTask> eventHandler,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TestResourceWithEndpoints(string name) : Resource(name), IResourceWithEndpoints;
}
