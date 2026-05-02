// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREFILESYSTEM001 // Type is for evaluation purposes only
#pragma warning disable ASPIREBROWSERLOGS001 // Type is for evaluation purposes only

using Aspire.Hosting.Tests.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Hosting.Browsers.Tests;

[Trait("Partition", "2")]
public class BrowserLogsRunningSessionTests
{
    [Fact]
    public async Task RunningSessionRoutesPageEventsToResourceLogsAndReleasesHostOnCompletion()
    {
        var userDataDirectory = Directory.CreateTempSubdirectory();
        try
        {
            var browserExecutable = Path.Combine(userDataDirectory.FullName, "browser");
            File.WriteAllText(browserExecutable, string.Empty);

            TestBrowserHost? host = null;
            await using var registry = new BrowserHostRegistry(

                NullLogger<BrowserLogsSessionManager>.Instance,
                TimeProvider.System,
                createUserDataDirectory: (configuration, _) => BrowserLogsUserDataDirectory.CreatePersistent(userDataDirectory.FullName, configuration.Profile),
                createHostAsync: (configuration, identity, _, _) =>
                {
                    host = new TestBrowserHost(identity);
                    return Task.FromResult<IBrowserHost>(host);
                });

            var resourceLoggerService = ConsoleLoggingTestHelpers.GetResourceLoggerService();
            var resourceName = "web-browser-logs";
            BrowserLogsRunningSession? session = null;
            var logs = await ConsoleLoggingTestHelpers.CaptureLogsAsync(resourceLoggerService, resourceName, targetLogCount: 5, () =>
            {
                session = BrowserLogsRunningSession.StartAsync(
                    new BrowserConfiguration(browserExecutable, Profile: null, BrowserUserDataMode.Shared, AppHostKey: null),
                    resourceName,
                    "session-0001",
                    new Uri("https://localhost:5001/"),
                    registry,
                    resourceLoggerService.GetLogger(resourceName),
                    NullLogger<BrowserLogsSessionManager>.Instance,
                    TimeProvider.System,
                    CancellationToken.None).GetAwaiter().GetResult();

                host!.PageSession!.RaiseEventAsync(new BrowserConsoleDiagnosticEvent("log", "hello from tracked browser")).AsTask().GetAwaiter().GetResult();
            });

            Assert.NotNull(session);
            Assert.NotNull(host);
            Assert.Equal(browserExecutable, session.BrowserExecutable);
            Assert.Equal(host.DebugEndpoint, session.BrowserDebugEndpoint);
            Assert.Equal(BrowserHostOwnership.Owned, session.BrowserHostOwnership);
            Assert.Equal(42, session.ProcessId);
            Assert.Equal("target-1", session.TargetId);
            Assert.Equal("session-0001", host.PageSession?.SessionId);
            Assert.Equal(new Uri("https://localhost:5001/"), host.PageSession?.Url);
            Assert.Contains(logs, log => log.Content.EndsWith("[session-0001] [console.log] hello from tracked browser", StringComparison.Ordinal));

            var completed = new TaskCompletionSource<(int? ExitCode, Exception? Error)>(TaskCreationOptions.RunContinuationsAsynchronously);
            var observerTask = session.StartCompletionObserver((exitCode, error) =>
            {
                completed.TrySetResult((exitCode, error));
                return Task.CompletedTask;
            });

            host.PageSession!.Complete(new BrowserPageSessionResult(BrowserPageSessionCompletionKind.PageClosed, Error: null));

            var result = await completed.Task.DefaultTimeout();
            await observerTask.DefaultTimeout();

            Assert.Null(result.ExitCode);
            Assert.Null(result.Error);
            Assert.True(host.Disposed);
            Assert.Equal(1, host.PageSession.DisposeCount);
        }
        finally
        {
            userDataDirectory.Delete(recursive: true);
        }
    }

    private sealed class TestBrowserHost(BrowserHostIdentity identity) : IBrowserHost
    {
        private readonly TaskCompletionSource _terminationSource = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public BrowserHostIdentity Identity { get; } = identity;

        public BrowserHostOwnership Ownership => BrowserHostOwnership.Owned;

        public Uri DebugEndpoint { get; } = new("ws://127.0.0.1/devtools/browser/test");

        public int? ProcessId => 42;

        public string BrowserDisplayName => "Test Browser";

        public Task Termination => _terminationSource.Task;

        public bool Disposed { get; private set; }

        public TestBrowserPageSession? PageSession { get; private set; }

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
            CancellationToken cancellationToken)
        {
            PageSession = new TestBrowserPageSession(sessionId, url, eventHandler);
            return Task.FromResult<IBrowserPageSession>(PageSession);
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            _terminationSource.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TestBrowserPageSession(
        string sessionId,
        Uri url,
        Func<BrowserDiagnosticEvent, ValueTask> eventHandler) : IBrowserPageSession
    {
        private readonly TaskCompletionSource<BrowserPageSessionResult> _completionSource = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string SessionId { get; } = sessionId;

        public Uri Url { get; } = url;

        public string TargetId => "target-1";

        public string TargetSessionId => "target-session-1";

        public Task<BrowserPageSessionResult> Completion => _completionSource.Task;

        public int DisposeCount { get; private set; }

        public Task<BrowserLogsCaptureScreenshotResult> CaptureScreenshotAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(new BrowserLogsCaptureScreenshotResult { Data = Convert.ToBase64String([0x89, 0x50, 0x4e, 0x47]) });
        }

        public ValueTask RaiseEventAsync(BrowserDiagnosticEvent diagnosticEvent)
        {
            return eventHandler(diagnosticEvent);
        }

        public void Complete(BrowserPageSessionResult result)
        {
            _completionSource.TrySetResult(result);
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }
}
