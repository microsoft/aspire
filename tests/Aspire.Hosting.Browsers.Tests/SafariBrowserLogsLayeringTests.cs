// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREBROWSERLOGS001 // Type is for evaluation purposes only

using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Hosting.Browsers.Tests;

[Trait("Partition", "2")]
public class SafariBrowserLogsLayeringTests
{
    [Fact]
    public async Task SafariWebDriverHostProvider_RejectsProfileBeforeStartingDriver()
    {
        var startCalls = 0;
        var provider = new SafariWebDriverHostProvider(
            NullLogger<BrowserLogsSessionManager>.Instance,
            TimeProvider.System,
            (_, _) =>
            {
                startCalls++;
                throw new InvalidOperationException("Should not start safaridriver.");
            },
            static (_, _) => throw new InvalidOperationException("Should not create WebDriver client."),
            static (_, _, _, _) => throw new InvalidOperationException("Should not create BiDi connection."));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.AcquireAsync(
            new BrowserConfiguration("safari", Profile: "Default", BrowserUserDataMode.Shared, AppHostKey: null),
            CancellationToken.None));

        Assert.Contains(BrowserLogsBuilderExtensions.ProfileConfigurationKey, exception.Message);
        Assert.Equal(0, startCalls);
    }

    [Fact]
    public async Task SafariWebDriverHostProvider_AcquiresOwnedHostAndDeletesWebDriverSessionOnRelease()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var driverPath = Path.Combine(directory.FullName, "safaridriver");
            File.WriteAllText(driverPath, string.Empty);
            var process = new FakeSafariDriverProcess(processId: 42);
            var client = new FakeSafariWebDriverClient(new SafariWebDriverSession("webdriver-session-1", new Uri("ws://127.0.0.1:12345/session/webdriver-session-1")));
            var provider = new SafariWebDriverHostProvider(
                NullLogger<BrowserLogsSessionManager>.Instance,
                TimeProvider.System,
                (_, _) => process,
                (_, _) => client,
                static (_, _, _, _) => throw new InvalidOperationException("Should not create a page session."));

            await using var lease = await provider.AcquireAsync(
                new BrowserConfiguration(driverPath, Profile: null, BrowserUserDataMode.Shared, AppHostKey: null),
                CancellationToken.None);

            Assert.Equal(driverPath, lease.Host.Identity.ExecutablePath);
            Assert.Equal(BrowserHostOwnership.Owned, lease.Host.Ownership);
            Assert.Null(lease.Host.DebugEndpoint);
            Assert.Equal(42, lease.Host.ProcessId);
            Assert.Equal("Safari", lease.Host.BrowserDisplayName);

            await lease.DisposeAsync();

            Assert.Equal(["webdriver-session-1"], client.DeletedSessionIds);
            Assert.True(client.Disposed);
            Assert.True(process.Disposed);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task SafariWebDriverHost_RetriesWhenDriverExitsBeforeAcceptingWebDriverSession()
    {
        var firstProcess = new FakeSafariDriverProcess(processId: 1, exited: true);
        var secondProcess = new FakeSafariDriverProcess(processId: 2);
        var firstClient = new FakeSafariWebDriverClient(new SafariWebDriverSession("unused", new Uri("ws://127.0.0.1:1/session/unused")))
        {
            CreateSessionException = new InvalidOperationException("Port already in use.")
        };
        var secondClient = new FakeSafariWebDriverClient(new SafariWebDriverSession("webdriver-session-2", new Uri("ws://127.0.0.1:2/session/webdriver-session-2")));
        var processQueue = new Queue<FakeSafariDriverProcess>([firstProcess, secondProcess]);
        var clientQueue = new Queue<FakeSafariWebDriverClient>([firstClient, secondClient]);

        await using var host = await SafariWebDriverHost.StartAsync(
            new SafariBrowserDriver(Path.Combine(AppContext.BaseDirectory, "safaridriver"), "Safari"),
            NullLogger<BrowserLogsSessionManager>.Instance,
            TimeProvider.System,
            (_, _) => processQueue.Dequeue(),
            (_, _) => clientQueue.Dequeue(),
            static (_, _, _, _) => throw new InvalidOperationException("Should not create a page session."),
            CancellationToken.None);

        Assert.Equal(2, host.ProcessId);
        Assert.True(firstClient.Disposed);
        Assert.True(firstProcess.Disposed);
        Assert.Equal("webdriver-session-2", host.WebDriverSession.SessionId);
    }

    [Fact]
    public async Task SafariBidiPageSession_StartsContextSubscribesNavigatesAndRoutesContextEvents()
    {
        var connection = new FakeBrowserLogsBidiConnection();
        await using var host = await CreateSafariHostAsync(connection);
        var routedEvents = new List<BrowserDiagnosticEvent>();

        await using var pageSession = await host.CreatePageSessionAsync(
            "session-0001",
            new Uri("https://localhost:5001/"),
            new BrowserConnectionDiagnosticsLogger("session-0001", NullLogger.Instance),
            diagnosticEvent =>
            {
                routedEvents.Add(diagnosticEvent);
                return ValueTask.CompletedTask;
            },
            CancellationToken.None);

        Assert.Equal("context-1", pageSession.TargetId);
        Assert.Equal("context-1", pageSession.TargetSessionId);
        Assert.Equal(
            [
                "CreateBrowsingContext",
                "Subscribe:context-1",
                "Navigate:context-1:https://localhost:5001/"
            ],
            connection.Calls);

        await connection.RaiseEventAsync(CreateLogEvent("other-context", "ignored"));
        await connection.RaiseEventAsync(CreateLogEvent("context-1", "hello from safari"));

        var consoleEvent = Assert.IsType<BrowserConsoleDiagnosticEvent>(Assert.Single(routedEvents));
        Assert.Equal("log", consoleEvent.Level);
        Assert.Equal("hello from safari", consoleEvent.Message);
    }

    [Fact]
    public async Task SafariBidiPageSession_ContextDestroyedCompletesAsPageClosed()
    {
        var connection = new FakeBrowserLogsBidiConnection();
        await using var host = await CreateSafariHostAsync(connection);
        await using var pageSession = await host.CreatePageSessionAsync(
            "session-0001",
            new Uri("https://localhost:5001/"),
            new BrowserConnectionDiagnosticsLogger("session-0001", NullLogger.Instance),
            static _ => ValueTask.CompletedTask,
            CancellationToken.None);

        await connection.RaiseEventAsync(new BrowserLogsBidiBrowsingContextDestroyedEvent(new BrowserLogsBidiBrowsingContextDestroyedParameters { Context = "context-1" }));

        var result = await pageSession.Completion.DefaultTimeout();

        Assert.Equal(BrowserPageSessionCompletionKind.PageClosed, result.CompletionKind);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task SafariBidiPageSession_CapturesScreenshotAndClosesContextOnDispose()
    {
        var connection = new FakeBrowserLogsBidiConnection
        {
            ScreenshotData = "aW1hZ2UtZGF0YQ=="
        };
        await using var host = await CreateSafariHostAsync(connection);
        var pageSession = await host.CreatePageSessionAsync(
            "session-0001",
            new Uri("https://localhost:5001/"),
            new BrowserConnectionDiagnosticsLogger("session-0001", NullLogger.Instance),
            static _ => ValueTask.CompletedTask,
            CancellationToken.None);

        var screenshot = await pageSession.CaptureScreenshotAsync(CancellationToken.None);
        await pageSession.DisposeAsync();

        Assert.Equal("aW1hZ2UtZGF0YQ==", screenshot.Data);
        Assert.Contains("CaptureScreenshot:context-1", connection.Calls);
        Assert.Contains("CloseBrowsingContext:context-1", connection.Calls);
        Assert.True(connection.Disposed);
    }

    [Fact]
    public async Task SafariBidiPageSession_ConnectionFailureCompletesAsConnectionLost()
    {
        var connection = new FakeBrowserLogsBidiConnection();
        await using var host = await CreateSafariHostAsync(connection);
        await using var pageSession = await host.CreatePageSessionAsync(
            "session-0001",
            new Uri("https://localhost:5001/"),
            new BrowserConnectionDiagnosticsLogger("session-0001", NullLogger.Instance),
            static _ => ValueTask.CompletedTask,
            CancellationToken.None);

        connection.FailCompletion(new InvalidOperationException("Socket reset."));

        var result = await pageSession.Completion.DefaultTimeout();

        Assert.Equal(BrowserPageSessionCompletionKind.ConnectionLost, result.CompletionKind);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void BrowserLogsBidiEventMapper_MapsResponseCompletedToResponseAndCompletionEvents()
    {
        var mappedEvents = BrowserLogsBidiEventMapper.MapResponseCompleted(new BrowserLogsBidiNetworkResponseCompletedEvent(
            new BrowserLogsBidiNetworkEventParameters
            {
                Context = "context-1",
                Timestamp = 1714520000250,
                Request = new BrowserLogsBidiNetworkRequest
                {
                    Request = "request-1",
                    Method = "GET",
                    Url = "https://example.test/api"
                },
                Response = new BrowserLogsBidiNetworkResponse
                {
                    Url = "https://example.test/api",
                    Status = 200,
                    StatusText = "OK",
                    FromCache = false
                }
            }));

        var responseEvent = Assert.IsType<BrowserNetworkResponseReceivedDiagnosticEvent>(mappedEvents[0]);
        var completionEvent = Assert.IsType<BrowserNetworkRequestCompletedDiagnosticEvent>(mappedEvents[1]);
        Assert.Equal("request-1", responseEvent.RequestId);
        Assert.Equal(200, responseEvent.Response?.Status);
        Assert.Equal("request-1", completionEvent.RequestId);
        Assert.Equal(1714520000.25, completionEvent.Timestamp);
    }

    [Fact]
    public void BrowserLogsCdpEventMapper_MapsConsoleArgumentsToNormalizedConsoleEvent()
    {
        var diagnosticEvent = BrowserLogsCdpEventMapper.TryMap(new BrowserLogsConsoleApiCalledEvent(
            "target-session-1",
            new BrowserLogsRuntimeConsoleApiCalledParameters
            {
                Type = "warn",
                Args =
                [
                    new BrowserLogsCdpProtocolRemoteObject { Value = new BrowserLogsCdpProtocolStringValue("hello") },
                    new BrowserLogsCdpProtocolRemoteObject { Value = new BrowserLogsCdpProtocolNumberValue("42") }
                ]
            }));

        var consoleEvent = Assert.IsType<BrowserConsoleDiagnosticEvent>(diagnosticEvent);
        Assert.Equal("warn", consoleEvent.Level);
        Assert.Equal("hello 42", consoleEvent.Message);
    }

    private static async Task<SafariWebDriverHost> CreateSafariHostAsync(FakeBrowserLogsBidiConnection connection)
    {
        var driverPath = Path.Combine(AppContext.BaseDirectory, "safaridriver");
        return await SafariWebDriverHost.StartAsync(
            new SafariBrowserDriver(driverPath, "Safari"),
            NullLogger<BrowserLogsSessionManager>.Instance,
            TimeProvider.System,
            static (_, _) => new FakeSafariDriverProcess(processId: 42),
            static (_, _) => new FakeSafariWebDriverClient(new SafariWebDriverSession("webdriver-session-1", new Uri("ws://127.0.0.1:12345/session/webdriver-session-1"))),
            (webSocketUri, eventHandler, _, _) =>
            {
                Assert.Equal(new Uri("ws://127.0.0.1:12345/session/webdriver-session-1"), webSocketUri);
                connection.SetEventHandler(eventHandler);
                return Task.FromResult<IBrowserLogsBidiConnection>(connection);
            },
            CancellationToken.None);
    }

    private static BrowserLogsBidiLogEntryAddedEvent CreateLogEvent(string context, string text)
    {
        return new BrowserLogsBidiLogEntryAddedEvent(new BrowserLogsBidiLogEntryAddedParameters
        {
            Method = "log",
            Level = "info",
            Text = text,
            Type = "console",
            Source = new BrowserLogsBidiSource
            {
                Context = context
            }
        });
    }

    private sealed class FakeSafariDriverProcess : ISafariDriverProcess
    {
        private readonly TaskCompletionSource<BrowserLogsProcessResult> _completionSource = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public FakeSafariDriverProcess(int processId, bool exited = false)
        {
            ProcessId = processId;
            if (exited)
            {
                _completionSource.TrySetResult(new BrowserLogsProcessResult(exitCode: 1));
            }
        }

        public int ProcessId { get; }

        public Uri WebDriverEndpoint { get; } = new("http://127.0.0.1:9999/");

        public Task<BrowserLogsProcessResult> ProcessTask => _completionSource.Task;

        public bool Disposed { get; private set; }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            _completionSource.TrySetResult(new BrowserLogsProcessResult(exitCode: 0));
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeSafariWebDriverClient(SafariWebDriverSession session) : ISafariWebDriverClient
    {
        public Exception? CreateSessionException { get; init; }

        public List<string> DeletedSessionIds { get; } = [];

        public bool Disposed { get; private set; }

        public Task<SafariWebDriverSession> CreateSessionAsync(Task<BrowserLogsProcessResult> driverProcessTask, CancellationToken cancellationToken)
        {
            if (CreateSessionException is not null)
            {
                return Task.FromException<SafariWebDriverSession>(CreateSessionException);
            }

            return Task.FromResult(session);
        }

        public Task DeleteSessionAsync(string sessionId, CancellationToken cancellationToken)
        {
            DeletedSessionIds.Add(sessionId);
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            Disposed = true;
        }
    }

    private sealed class FakeBrowserLogsBidiConnection : IBrowserLogsBidiConnection
    {
        private readonly TaskCompletionSource _completionSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Func<BrowserLogsBidiProtocolEvent, ValueTask>? _eventHandler;

        public List<string> Calls { get; } = [];

        public string ScreenshotData { get; init; } = Convert.ToBase64String([0x89, 0x50, 0x4e, 0x47]);

        public bool Disposed { get; private set; }

        public Task Completion => _completionSource.Task;

        public Task<BrowserLogsBidiCreateContextResult> CreateBrowsingContextAsync(CancellationToken cancellationToken)
        {
            Calls.Add("CreateBrowsingContext");
            return Task.FromResult(new BrowserLogsBidiCreateContextResult { Context = "context-1" });
        }

        public Task SubscribeAsync(string context, CancellationToken cancellationToken)
        {
            Calls.Add($"Subscribe:{context}");
            return Task.FromResult(BrowserLogsBidiCommandAck.Instance);
        }

        public Task<BrowserLogsBidiCommandAck> NavigateAsync(string context, Uri url, CancellationToken cancellationToken)
        {
            Calls.Add($"Navigate:{context}:{url}");
            return Task.FromResult(BrowserLogsBidiCommandAck.Instance);
        }

        public Task<BrowserLogsBidiCaptureScreenshotResult> CaptureScreenshotAsync(string context, CancellationToken cancellationToken)
        {
            Calls.Add($"CaptureScreenshot:{context}");
            return Task.FromResult(new BrowserLogsBidiCaptureScreenshotResult { Data = ScreenshotData });
        }

        public Task<BrowserLogsBidiCommandAck> CloseBrowsingContextAsync(string context, CancellationToken cancellationToken)
        {
            Calls.Add($"CloseBrowsingContext:{context}");
            _completionSource.TrySetResult();
            return Task.FromResult(BrowserLogsBidiCommandAck.Instance);
        }

        public void SetEventHandler(Func<BrowserLogsBidiProtocolEvent, ValueTask> eventHandler)
        {
            _eventHandler = eventHandler;
        }

        public ValueTask RaiseEventAsync(BrowserLogsBidiProtocolEvent protocolEvent)
        {
            return _eventHandler is null
                ? throw new InvalidOperationException("The fake BiDi connection is not connected.")
                : _eventHandler(protocolEvent);
        }

        public void FailCompletion(Exception exception)
        {
            _completionSource.TrySetException(exception);
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            _completionSource.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }
}
