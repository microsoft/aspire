// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using Aspire.Hosting.Tests.Utils;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

#pragma warning disable ASPIREFILESYSTEM001 // Type is for evaluation purposes only

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
    public async Task BrowserHostRegistry_ReusesHostUntilFinalLeaseReleasesIt()
    {
        var userDataDirectory = Directory.CreateTempSubdirectory();
        try
        {
            var browserExecutable = Path.Combine(userDataDirectory.FullName, "browser");
            File.WriteAllText(browserExecutable, string.Empty);
            var createdHosts = new List<TestBrowserHost>();
            await using var registry = new BrowserHostRegistry(
                fileSystemService: null!,
                NullLogger<BrowserLogsSessionManager>.Instance,
                TimeProvider.System,
                createUserDataDirectory: (configuration, _) => BrowserLogsUserDataDirectory.CreatePersistent(userDataDirectory.FullName, configuration.Profile),
                createHostAsync: (configuration, identity, _, _) =>
                {
                    var host = new TestBrowserHost(identity, configuration.Profile);
                    createdHosts.Add(host);
                    return Task.FromResult<IBrowserHost>(host);
                });
            var configuration = new BrowserConfiguration(browserExecutable, Profile: null, BrowserUserDataMode.Shared);

            var firstLease = await registry.AcquireAsync(configuration, CancellationToken.None);
            var secondLease = await registry.AcquireAsync(configuration, CancellationToken.None);

            Assert.Single(createdHosts);
            Assert.Same(firstLease.Host, secondLease.Host);

            await firstLease.DisposeAsync();
            Assert.False(createdHosts[0].Disposed);

            await secondLease.DisposeAsync();
            Assert.True(createdHosts[0].Disposed);
        }
        finally
        {
            userDataDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task BrowserHostRegistry_LateLeaseReleaseAfterRegistryDisposeNoOps()
    {
        var userDataDirectory = Directory.CreateTempSubdirectory();
        try
        {
            var browserExecutable = Path.Combine(userDataDirectory.FullName, "browser");
            File.WriteAllText(browserExecutable, string.Empty);
            var createdHosts = new List<TestBrowserHost>();
            var registry = new BrowserHostRegistry(
                fileSystemService: null!,
                NullLogger<BrowserLogsSessionManager>.Instance,
                TimeProvider.System,
                createUserDataDirectory: (configuration, _) => BrowserLogsUserDataDirectory.CreatePersistent(userDataDirectory.FullName, configuration.Profile),
                createHostAsync: (configuration, identity, _, _) =>
                {
                    var host = new TestBrowserHost(identity, configuration.Profile);
                    createdHosts.Add(host);
                    return Task.FromResult<IBrowserHost>(host);
                });
            var configuration = new BrowserConfiguration(browserExecutable, Profile: null, BrowserUserDataMode.Shared);

            var lease = await registry.AcquireAsync(configuration, CancellationToken.None);

            await registry.DisposeAsync();
            await lease.DisposeAsync();

            Assert.True(createdHosts[0].Disposed);
        }
        finally
        {
            userDataDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task BrowserHostRegistry_RejectsDifferentProfileForSharedHost()
    {
        var userDataDirectory = Directory.CreateTempSubdirectory();
        try
        {
            var browserExecutable = Path.Combine(userDataDirectory.FullName, "browser");
            File.WriteAllText(browserExecutable, string.Empty);
            await using var registry = new BrowserHostRegistry(
                fileSystemService: null!,
                NullLogger<BrowserLogsSessionManager>.Instance,
                TimeProvider.System,
                createUserDataDirectory: (configuration, _) => BrowserLogsUserDataDirectory.CreatePersistent(userDataDirectory.FullName, configuration.Profile),
                createHostAsync: (configuration, identity, _, _) => Task.FromResult<IBrowserHost>(new TestBrowserHost(identity, configuration.Profile)));

            var firstLease = await registry.AcquireAsync(
                new BrowserConfiguration(browserExecutable, Profile: "Profile 1", BrowserUserDataMode.Shared),
                CancellationToken.None);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                registry.AcquireAsync(
                    new BrowserConfiguration(browserExecutable, Profile: "Profile 2", BrowserUserDataMode.Shared),
                    CancellationToken.None));

            await firstLease.DisposeAsync();
            Assert.Contains("with profile 'Profile 1'", exception.Message);
            Assert.Contains("The requested profile is 'Profile 2'", exception.Message);
        }
        finally
        {
            userDataDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public void BrowserPageSession_MapsTargetLifecycleEventsToCompletion()
    {
        var closed = BrowserPageSession.TryGetPageCompletion(
            new BrowserLogsDetachedFromTargetEvent(
                SessionId: null,
                new BrowserLogsDetachedFromTargetParameters
                {
                    SessionId = "target-session-1",
                    TargetId = "target-1"
                }),
            targetId: "target-1",
            targetSessionId: "target-session-1");
        var crashed = BrowserPageSession.TryGetPageCompletion(
            new BrowserLogsTargetCrashedEvent(
                SessionId: null,
                new BrowserLogsTargetCrashedParameters
                {
                    TargetId = "target-1",
                    Status = "crashed",
                    ErrorCode = 1337
                }),
            targetId: "target-1",
            targetSessionId: "target-session-1");
        var unrelated = BrowserPageSession.TryGetPageCompletion(
            new BrowserLogsInspectorDetachedEvent(
                SessionId: "other-session",
                new BrowserLogsInspectorDetachedParameters
                {
                    Reason = "target_closed"
                }),
            targetId: "target-1",
            targetSessionId: "target-session-1");

        Assert.Equal(BrowserPageSessionCompletionKind.PageClosed, closed?.CompletionKind);
        Assert.Null(closed?.Error);
        Assert.Equal(BrowserPageSessionCompletionKind.PageCrashed, crashed?.CompletionKind);
        Assert.Contains("1337", crashed?.Error?.Message);
        Assert.Null(unrelated);
    }

    [Fact]
    public async Task BrowserEndpointDiscovery_DeletesStaleMetadataBeforeProfileCompatibilityCheck()
    {
        var userDataDirectory = Directory.CreateTempSubdirectory();
        try
        {
            var identity = new BrowserHostIdentity(
                Path.Combine(userDataDirectory.FullName, "browser"),
                userDataDirectory.FullName);
            var metadataPath = BrowserEndpointDiscovery.GetEndpointMetadataFilePath(userDataDirectory.FullName);
            var discovery = new BrowserEndpointDiscovery(NullLogger<BrowserLogsSessionManager>.Instance);

            await BrowserEndpointDiscovery.WriteAsync(
                identity,
                profileDirectoryName: "Profile 1",
                new Uri("ws://127.0.0.1:9/devtools/browser/stale"),
                processId: int.MaxValue,
                CancellationToken.None);

            var metadata = await discovery.TryReadAndValidateAsync(identity, profileDirectoryName: "Default", CancellationToken.None);

            Assert.Null(metadata);
            Assert.False(File.Exists(metadataPath));
        }
        finally
        {
            userDataDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task BrowserEndpointDiscovery_DeletesMalformedEndpointMetadata()
    {
        var userDataDirectory = Directory.CreateTempSubdirectory();
        try
        {
            var identity = new BrowserHostIdentity(
                Path.Combine(userDataDirectory.FullName, "browser"),
                userDataDirectory.FullName);
            var metadataPath = BrowserEndpointDiscovery.GetEndpointMetadataFilePath(userDataDirectory.FullName);
            var discovery = new BrowserEndpointDiscovery(NullLogger<BrowserLogsSessionManager>.Instance);
            await File.WriteAllTextAsync(
                metadataPath,
                $$"""
                {
                  "schemaVersion": 1,
                  "endpoint": "ws://127.0.0.1:9/devtools/browser/stale",
                  "processId": {{Environment.ProcessId}},
                  "userDataRootPath": {{System.Text.Json.JsonSerializer.Serialize(userDataDirectory.FullName)}}
                }
                """);

            var metadata = await discovery.TryReadAndValidateAsync(identity, profileDirectoryName: null, CancellationToken.None);

            Assert.Null(metadata);
            Assert.False(File.Exists(metadataPath));
        }
        finally
        {
            userDataDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task BrowserEndpointDiscovery_ThrowsForLiveEndpointWithDifferentProfile()
    {
        var userDataDirectory = Directory.CreateTempSubdirectory();
        try
        {
            var identity = new BrowserHostIdentity(
                Path.Combine(userDataDirectory.FullName, "browser"),
                userDataDirectory.FullName);
            var discovery = new BrowserEndpointDiscovery(NullLogger<BrowserLogsSessionManager>.Instance);
            var browserEndpoint = StartBrowserVersionEndpoint(out var serverTask);

            await BrowserEndpointDiscovery.WriteAsync(
                identity,
                profileDirectoryName: "Profile 1",
                browserEndpoint,
                Environment.ProcessId,
                CancellationToken.None);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                discovery.TryReadAndValidateAsync(identity, profileDirectoryName: "Default", CancellationToken.None));

            await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Contains("with profile 'Profile 1'", exception.Message);
            Assert.Contains("The requested profile is 'Default'", exception.Message);
        }
        finally
        {
            userDataDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public void BrowserEndpointDiscovery_DetectsWindowsLockfileAsNonDebuggableBrowser()
    {
        var userDataDirectory = Directory.CreateTempSubdirectory();
        try
        {
            File.WriteAllText(Path.Combine(userDataDirectory.FullName, "lockfile"), string.Empty);

            Assert.True(BrowserEndpointDiscovery.IsNonDebuggableBrowserRunning(userDataDirectory.FullName, isWindows: true));
            Assert.False(BrowserEndpointDiscovery.IsNonDebuggableBrowserRunning(userDataDirectory.FullName, isWindows: false));
        }
        finally
        {
            userDataDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public void BrowserEndpointDiscovery_IgnoresPosixSingletonLockWithoutPidTarget()
    {
        var userDataDirectory = Directory.CreateTempSubdirectory();
        try
        {
            File.WriteAllText(Path.Combine(userDataDirectory.FullName, "SingletonLock"), string.Empty);

            Assert.False(BrowserEndpointDiscovery.IsNonDebuggableBrowserRunning(userDataDirectory.FullName, isWindows: false));
        }
        finally
        {
            userDataDirectory.Delete(recursive: true);
        }
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
            new BrowserConfiguration("chrome", null, BrowserUserDataMode.Isolated),
            browserOverride: null,
            profileOverride: null,
            userDataModeOverride: null);

        await manager.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => manager.StartSessionAsync(
            resource,
            new BrowserConfiguration("chrome", null, BrowserUserDataMode.Isolated),
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

    private static Uri StartBrowserVersionEndpoint(out Task serverTask)
    {
        var listener = new TcpListener(IPAddress.Loopback, port: 0);
        listener.Start();

        var browserEndpoint = new Uri($"ws://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/devtools/browser/test");
        serverTask = Task.Run(async () =>
        {
            try
            {
                using var client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
                await using var stream = client.GetStream();
                var buffer = new byte[4096];
                await stream.ReadAsync(buffer).ConfigureAwait(false);

                var body = $$"""{"webSocketDebuggerUrl":"{{browserEndpoint}}"}""";
                var response = Encoding.UTF8.GetBytes(
                    "HTTP/1.1 200 OK\r\n" +
                    "Content-Type: application/json\r\n" +
                    $"Content-Length: {Encoding.UTF8.GetByteCount(body)}\r\n" +
                    "Connection: close\r\n" +
                    "\r\n" +
                    body);
                await stream.WriteAsync(response).ConfigureAwait(false);
            }
            finally
            {
                listener.Stop();
            }
        });

        return browserEndpoint;
    }

    private sealed class ThrowIfCalledSessionFactory : IBrowserLogsRunningSessionFactory
    {
        public bool WasCalled { get; private set; }

        public Task<IBrowserLogsRunningSession> StartSessionAsync(
            BrowserConfiguration configuration,
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
        public TestBrowserHost()
            : this(
                new BrowserHostIdentity(
                    Path.Combine(AppContext.BaseDirectory, "browser"),
                    Path.Combine(AppContext.BaseDirectory, "user-data")),
                profileDirectoryName: null)
        {
        }

        public TestBrowserHost(BrowserHostIdentity identity, string? profileDirectoryName)
        {
            Identity = identity;
            ProfileDirectoryName = profileDirectoryName;
        }

        public BrowserHostIdentity Identity { get; }

        public string? ProfileDirectoryName { get; }

        public bool Disposed { get; private set; }

        public BrowserHostOwnership Ownership => BrowserHostOwnership.Owned;

        public Uri DebugEndpoint { get; } = new("ws://127.0.0.1/devtools/browser/test");

        public int? ProcessId => 1;

        public string BrowserDisplayName => "Test";

        public Task Termination { get; } = Task.CompletedTask;

        public Task<IBrowserPageSession> CreatePageSessionAsync(
            string sessionId,
            Uri url,
            BrowserConnectionDiagnosticsLogger connectionDiagnostics,
            Func<BrowserLogsCdpProtocolEvent, ValueTask> eventHandler,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TestResourceWithEndpoints(string name) : Resource(name), IResourceWithEndpoints;
}
