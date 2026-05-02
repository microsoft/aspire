// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Aspire.Hosting.Browsers.Tests;

[Trait("Partition", "2")]
public class BrowserPageSessionTests
{
    [Fact]
    public void TrySelectReusableStartupPageTargetId_PrefersUnattachedBlankPage()
    {
        var targetId = BrowserPageSession.TrySelectReusableStartupPageTargetId(
        [
            new BrowserLogsTargetInfo { TargetId = "restored-page", Type = "page", Url = "https://example.com", Attached = false },
            new BrowserLogsTargetInfo { TargetId = "service-worker", Type = "service_worker", Url = "https://example.com/sw.js", Attached = false },
            new BrowserLogsTargetInfo { TargetId = "launcher-page", Type = "page", Url = "about:blank", Attached = false }
        ]);

        Assert.Equal("launcher-page", targetId);
    }

    [Fact]
    public void TrySelectReusableStartupPageTargetId_FallsBackToFirstUnattachedPage()
    {
        var targetId = BrowserPageSession.TrySelectReusableStartupPageTargetId(
        [
            new BrowserLogsTargetInfo { TargetId = "attached-page", Type = "page", Url = "about:blank", Attached = true },
            new BrowserLogsTargetInfo { TargetId = "fallback-page", Type = "page", Url = "chrome://newtab/", Attached = false }
        ]);

        Assert.Equal("fallback-page", targetId);
    }

    [Fact]
    public async Task StartAsync_ReusesStartupTargetAttachesInstrumentsNavigatesAndRoutesEvents()
    {
        var host = new TestBrowserHost();
        var connection = new FakeBrowserLogsCdpConnection
        {
            TargetInfos =
            [
                new BrowserLogsTargetInfo { TargetId = "startup-target", Type = "page", Url = "about:blank", Attached = false }
            ]
        };
        var routedEvents = new List<BrowserDiagnosticEvent>();

        var session = await BrowserPageSession.StartAsync(
            host,
            "session-0001",
            new Uri("https://localhost:5001/"),
            new BrowserConnectionDiagnosticsLogger("session-0001", NullLogger.Instance),
            CreateConnectionFactory(connection),
            diagnosticEvent =>
            {
                routedEvents.Add(diagnosticEvent);
                return ValueTask.CompletedTask;
            },
            NullLogger<BrowserLogsSessionManager>.Instance,
            TimeProvider.System,
            reuseInitialBlankTarget: true,
            CancellationToken.None);

        Assert.Equal("startup-target", session.TargetId);
        Assert.Equal("target-session-1", session.TargetSessionId);
        Assert.Equal(
            new[]
            {
                "EnableTargetDiscovery",
                "GetTargets",
                "Attach:startup-target",
                "EnablePageInstrumentation:target-session-1",
                "Navigate:target-session-1:https://localhost:5001/"
            },
            connection.Calls);

        var unrelatedEvent = new BrowserLogsConsoleApiCalledEvent("other-session", new BrowserLogsRuntimeConsoleApiCalledParameters { Type = "log" });
        var routedEvent = new BrowserLogsConsoleApiCalledEvent("target-session-1", new BrowserLogsRuntimeConsoleApiCalledParameters { Type = "log" });
        await connection.RaiseEventAsync(unrelatedEvent);
        await connection.RaiseEventAsync(routedEvent);

        var capturedEvent = Assert.IsType<BrowserConsoleDiagnosticEvent>(Assert.Single(routedEvents));
        Assert.Equal("log", capturedEvent.Level);

        await connection.RaiseEventAsync(new BrowserLogsTargetDestroyedEvent(
            SessionId: null,
            new BrowserLogsTargetDestroyedParameters { TargetId = "startup-target" }));

        var result = await session.Completion.DefaultTimeout();
        Assert.Equal(BrowserPageSessionCompletionKind.PageClosed, result.CompletionKind);
        Assert.Null(result.Error);
        Assert.True(connection.Disposed);

        await session.DisposeAsync();
    }

    [Fact]
    public async Task DisposeAsync_ClosesTrackedTarget()
    {
        var host = new TestBrowserHost();
        var connection = new FakeBrowserLogsCdpConnection
        {
            CreatedTargetId = "created-target"
        };

        var session = await BrowserPageSession.StartAsync(
            host,
            "session-0001",
            new Uri("https://localhost:5001/"),
            new BrowserConnectionDiagnosticsLogger("session-0001", NullLogger.Instance),
            CreateConnectionFactory(connection),
            static _ => ValueTask.CompletedTask,
            NullLogger<BrowserLogsSessionManager>.Instance,
            TimeProvider.System,
            reuseInitialBlankTarget: false,
            CancellationToken.None);

        await session.DisposeAsync();

        Assert.Equal(
            new[]
            {
                "EnableTargetDiscovery",
                "CreateTarget",
                "Attach:created-target",
                "EnablePageInstrumentation:target-session-1",
                "Navigate:target-session-1:https://localhost:5001/",
                "CloseTarget:created-target"
            },
            connection.Calls);
        Assert.True(connection.Disposed);
        var result = await session.Completion.DefaultTimeout();
        Assert.Equal(BrowserPageSessionCompletionKind.Stopped, result.CompletionKind);
    }

    [Fact]
    public async Task CaptureScreenshotAsync_UsesCurrentTargetSession()
    {
        var host = new TestBrowserHost();
        var connection = new FakeBrowserLogsCdpConnection
        {
            CreatedTargetId = "created-target",
            ScreenshotData = "image-data"
        };

        var session = await BrowserPageSession.StartAsync(
            host,
            "session-0001",
            new Uri("https://localhost:5001/"),
            new BrowserConnectionDiagnosticsLogger("session-0001", NullLogger.Instance),
            CreateConnectionFactory(connection),
            static _ => ValueTask.CompletedTask,
            NullLogger<BrowserLogsSessionManager>.Instance,
            TimeProvider.System,
            reuseInitialBlankTarget: false,
            CancellationToken.None);

        var result = await session.CaptureScreenshotAsync(CancellationToken.None);

        Assert.Equal("image-data", result.Data);
        Assert.Contains("CaptureScreenshot:target-session-1", connection.Calls);

        await session.DisposeAsync();
    }

    [Fact]
    public async Task CaptureScreenshotAsync_IsCanceledWhenSessionIsDisposed()
    {
        var host = new TestBrowserHost();
        var connection = new FakeBrowserLogsCdpConnection
        {
            CreatedTargetId = "created-target",
            WaitForScreenshotCancellation = true
        };

        var session = await BrowserPageSession.StartAsync(
            host,
            "session-0001",
            new Uri("https://localhost:5001/"),
            new BrowserConnectionDiagnosticsLogger("session-0001", NullLogger.Instance),
            CreateConnectionFactory(connection),
            static _ => ValueTask.CompletedTask,
            NullLogger<BrowserLogsSessionManager>.Instance,
            TimeProvider.System,
            reuseInitialBlankTarget: false,
            CancellationToken.None);

        var captureTask = session.CaptureScreenshotAsync(CancellationToken.None);
        await connection.ScreenshotCaptureStarted.Task.DefaultTimeout();

        var disposeTask = session.DisposeAsync().AsTask();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await captureTask.DefaultTimeout());
        await disposeTask.DefaultTimeout();
        Assert.True(connection.Disposed);
    }

    [Fact]
    public async Task MonitorAsync_ReconnectsToExistingTargetAfterConnectionLoss()
    {
        var host = new TestBrowserHost();
        var firstConnection = new FakeBrowserLogsCdpConnection
        {
            CreatedTargetId = "target-1",
            AttachSessionId = "target-session-1"
        };
        var secondConnection = new FakeBrowserLogsCdpConnection
        {
            AttachSessionId = "target-session-2"
        };
        var routedEvents = new List<BrowserDiagnosticEvent>();
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 4, 26, 0, 0, 0, TimeSpan.Zero));

        var session = await BrowserPageSession.StartAsync(
            host,
            "session-0001",
            new Uri("https://localhost:5001/"),
            new BrowserConnectionDiagnosticsLogger("session-0001", NullLogger.Instance),
            CreateConnectionFactory(firstConnection, secondConnection),
            diagnosticEvent =>
            {
                routedEvents.Add(diagnosticEvent);
                return ValueTask.CompletedTask;
            },
            NullLogger<BrowserLogsSessionManager>.Instance,
            timeProvider,
            reuseInitialBlankTarget: false,
            CancellationToken.None);

        Assert.Equal("target-1", session.TargetId);
        Assert.Equal("target-session-1", session.TargetSessionId);

        firstConnection.FailCompletion(new InvalidOperationException("Socket reset."));

        await secondConnection.InstrumentationEnabled.DefaultTimeout();

        Assert.True(firstConnection.Disposed);
        Assert.Equal("target-1", session.TargetId);
        Assert.Equal("target-session-2", session.TargetSessionId);
        Assert.Equal(
            new[]
            {
                "EnableTargetDiscovery",
                "Attach:target-1",
                "EnablePageInstrumentation:target-session-2"
            },
            secondConnection.Calls);

        var routedEvent = new BrowserLogsConsoleApiCalledEvent("target-session-2", new BrowserLogsRuntimeConsoleApiCalledParameters { Type = "log" });
        await secondConnection.RaiseEventAsync(routedEvent);

        var capturedEvent = Assert.IsType<BrowserConsoleDiagnosticEvent>(Assert.Single(routedEvents));
        Assert.Equal("log", capturedEvent.Level);

        await secondConnection.RaiseEventAsync(new BrowserLogsTargetDestroyedEvent(
            SessionId: null,
            new BrowserLogsTargetDestroyedParameters { TargetId = "target-1" }));

        var result = await session.Completion.DefaultTimeout();
        Assert.Equal(BrowserPageSessionCompletionKind.PageClosed, result.CompletionKind);
        Assert.Null(result.Error);
        Assert.True(secondConnection.Disposed);

        await session.DisposeAsync();
    }

    private static BrowserLogsCdpConnectionFactory CreateConnectionFactory(params FakeBrowserLogsCdpConnection[] connections)
    {
        var nextConnectionIndex = 0;
        return (eventHandler, _, _) =>
        {
            Assert.True(nextConnectionIndex < connections.Length);
            var connection = connections[nextConnectionIndex++];
            connection.SetEventHandler(eventHandler);
            return Task.FromResult<IBrowserLogsCdpConnection>(connection);
        };
    }

    private sealed class TestBrowserHost : IBrowserHost
    {
        private readonly TaskCompletionSource _terminationSource = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public BrowserHostIdentity Identity { get; } = new(
            Path.Combine(AppContext.BaseDirectory, "browser"),
            Path.Combine(AppContext.BaseDirectory, "user-data"));

        public BrowserHostOwnership Ownership => BrowserHostOwnership.Owned;

        public Uri DebugEndpoint { get; } = new("ws://127.0.0.1/devtools/browser/test");

        public int? ProcessId => 1;

        public string BrowserDisplayName => "Test";

        public Task Termination => _terminationSource.Task;

        public Task<IBrowserLogsCdpConnection> CreateCdpConnectionAsync(
            Func<BrowserLogsCdpProtocolEvent, ValueTask> eventHandler,
            ILogger<BrowserLogsSessionManager> logger,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IBrowserPageSession> CreatePageSessionAsync(
            string sessionId,
            Uri url,
            BrowserConnectionDiagnosticsLogger connectionDiagnostics,
            Func<BrowserDiagnosticEvent, ValueTask> eventHandler,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask DisposeAsync()
        {
            _terminationSource.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeBrowserLogsCdpConnection : IBrowserLogsCdpConnection
    {
        private readonly TaskCompletionSource _completionSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _instrumentationEnabled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Func<BrowserLogsCdpProtocolEvent, ValueTask>? _eventHandler;

        public List<string> Calls { get; } = [];

        public string CreatedTargetId { get; init; } = "target-1";

        public string AttachSessionId { get; init; } = "target-session-1";

        public string ScreenshotData { get; init; } = Convert.ToBase64String([0x89, 0x50, 0x4e, 0x47]);

        public bool WaitForScreenshotCancellation { get; init; }

        public TaskCompletionSource ScreenshotCaptureStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Disposed { get; private set; }

        public BrowserLogsTargetInfo[]? TargetInfos { get; init; }

        public Task Completion => _completionSource.Task;

        public Task InstrumentationEnabled => _instrumentationEnabled.Task;

        public Task<BrowserLogsCreateTargetResult> CreateTargetAsync(CancellationToken cancellationToken)
        {
            Calls.Add("CreateTarget");
            return Task.FromResult(new BrowserLogsCreateTargetResult { TargetId = CreatedTargetId });
        }

        public Task<BrowserLogsGetTargetsResult> GetTargetsAsync(CancellationToken cancellationToken)
        {
            Calls.Add("GetTargets");
            return Task.FromResult(new BrowserLogsGetTargetsResult { TargetInfos = TargetInfos });
        }

        public Task<BrowserLogsAttachToTargetResult> AttachToTargetAsync(string targetId, CancellationToken cancellationToken)
        {
            Calls.Add($"Attach:{targetId}");
            return Task.FromResult(new BrowserLogsAttachToTargetResult { SessionId = AttachSessionId });
        }

        public Task<BrowserLogsCommandAck> CloseTargetAsync(string targetId, CancellationToken cancellationToken)
        {
            Calls.Add($"CloseTarget:{targetId}");
            return Task.FromResult(BrowserLogsCommandAck.Instance);
        }

        public Task<BrowserLogsCommandAck> EnableTargetDiscoveryAsync(CancellationToken cancellationToken)
        {
            Calls.Add("EnableTargetDiscovery");
            return Task.FromResult(BrowserLogsCommandAck.Instance);
        }

        public Task EnablePageInstrumentationAsync(string sessionId, CancellationToken cancellationToken)
        {
            Calls.Add($"EnablePageInstrumentation:{sessionId}");
            _instrumentationEnabled.TrySetResult();
            return Task.CompletedTask;
        }

        public Task<BrowserLogsCommandAck> NavigateAsync(string sessionId, Uri url, CancellationToken cancellationToken)
        {
            Calls.Add($"Navigate:{sessionId}:{url}");
            return Task.FromResult(BrowserLogsCommandAck.Instance);
        }

        public async Task<BrowserLogsCaptureScreenshotResult> CaptureScreenshotAsync(string sessionId, CancellationToken cancellationToken)
        {
            Calls.Add($"CaptureScreenshot:{sessionId}");
            ScreenshotCaptureStarted.TrySetResult();

            if (WaitForScreenshotCancellation)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            return new BrowserLogsCaptureScreenshotResult { Data = ScreenshotData };
        }

        public void SetEventHandler(Func<BrowserLogsCdpProtocolEvent, ValueTask> eventHandler)
        {
            _eventHandler = eventHandler;
        }

        public ValueTask RaiseEventAsync(BrowserLogsCdpProtocolEvent protocolEvent)
        {
            return _eventHandler is null
                ? throw new InvalidOperationException("The fake connection is not connected.")
                : _eventHandler(protocolEvent);
        }

        public void FailCompletion(Exception exception)
        {
            _completionSource.TrySetException(exception);
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
