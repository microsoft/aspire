// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Aspire.Hosting.Native.Cli;
using Aspire.Hosting.Native.Core.Tests.TestServices;
using Aspire.Hosting.Native.Diagnostics;
using Aspire.Hosting.Native.Rpc;
using Aspire.Hosting.Native.Server;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;
using static Aspire.Hosting.Native.Core.Tests.TestServices.RpcTestClient;

namespace Aspire.Hosting.Native.Core.Tests;

public class NativeSocketIsolationTests
{
    [Fact]
    public async Task UnauthenticatedPeerDeadlineDoesNotDisconnectAuthenticatedClients()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var clock = new FakeTimeProvider();
        var options = new NativeServerOptions { TimeProvider = clock };
        using var listener = new NativeRpcListener(new IPEndPoint(IPAddress.Loopback, 0), Token, NullLogger.Instance,
            new Aspire.Hosting.Native.Api.NativeApplicationServer(), null, options);
        var run = listener.RunAsync(timeout.Token);
        try
        {
            using var unauthenticated = new NativeSocketTestClient(listener.EndPoint);
            var error = (await unauthenticated.CallAsync("getApplicationServer", null, timeout.Token))["error"]!;
            Assert.Equal(-32000, error["code"]!.GetValue<int>());
            Assert.Equal("Authenticate before using capabilities.", error["message"]!.GetValue<string>());
            using var authenticated = new NativeSocketTestClient(listener.EndPoint);
            Assert.True((await authenticated.CallAsync("authenticate", new JsonArray(Token), timeout.Token))["result"]!.GetValue<bool>());
            var disconnected = unauthenticated.ReadAsync(timeout.Token);
            clock.Advance(options.AuthenticationTimeout);
            Assert.Null(await disconnected);
            Assert.NotNull((await authenticated.CallAsync("getCapabilities", null, timeout.Token))["result"]);
        }
        finally
        {
            await timeout.CancelAsync();
            await run.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task UnexpectedBackchannelDispatchFailureIsSanitizedAndDoesNotStopOtherRequests(int failure)
    {
        using var capture = new DiagnosticCapture(sampleActivities: true);
        var exception = failure switch
        {
            0 => (Exception)new HttpRequestException("secret-controller-token"),
            1 => new ObjectDisposedException("secret-controller-token"),
            _ => new OperationCanceledException("secret-controller-token")
        };
        await using var host = new NativeSocketTestHost((request, _) => request.Method == "Fail"
            ? Task.FromException<NativeBackchannelReply>(exception)
            : Task.FromResult(NativeBackchannelReply.Value("ok", NativeCliJsonContext.Default.String)));
        using (var client = new NativeSocketTestClient(host.EndPoint))
        {
            var response = await client.CallAsync("Fail", null, TestContext.Current.CancellationToken);
            var error = response["error"]!.AsObject();
            Assert.Equal(["code", "message"], error.Select(property => property.Key));
            Assert.Equal(-32603, error["code"]!.GetValue<int>());
            Assert.Equal("The backchannel request failed internally.", error["message"]!.GetValue<string>());
            Assert.Equal("ok", (await client.CallAsync("Succeed", null, TestContext.Current.CancellationToken))["result"]!.GetValue<string>());
        }
        using var subsequent = new NativeSocketTestClient(host.EndPoint);
        Assert.Equal("ok", (await subsequent.CallAsync("Succeed", null, TestContext.Current.CancellationToken))["result"]!.GetValue<string>());
        var diagnostic = Assert.Single(capture.Events, value => value.Operation == "backchannel.request" && value.Outcome == OperationOutcome.Error);
        Assert.Equal(OperationOutcome.Error, diagnostic.Outcome);
        Assert.Equal(exception.GetType().FullName, diagnostic.ErrorType);
        Assert.False(host.Completion.IsCompleted);
    }

    [Theory]
    [InlineData("Content-Length: invalid\r\n\r\n")]
    [InlineData("Content-Length: 262145\r\n\r\n")]
    [InlineData("Content-Length: 1\r\nContent-Length: 1\r\n\r\n")]
    [InlineData("Content-Length: 10\r\n\r\n{}")]
    public async Task MalformedRpcPeerDoesNotStopExistingOrSubsequentConnections(string frame)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var diagnostics = new TestSink();
        using var listener = new NativeRpcListener(new IPEndPoint(IPAddress.Loopback, 0), Token,
            new TestLogger(nameof(NativeRpcListener), diagnostics, enabled: true));
        var run = listener.RunAsync(timeout.Token);
        try
        {
            using var existing = new NativeSocketTestClient(listener.EndPoint);
            Assert.True((await existing.CallAsync("authenticate", new JsonArray(Token), timeout.Token))["result"]!.GetValue<bool>());
            using (var malformed = new NativeSocketTestClient(listener.EndPoint))
            {
                await malformed.SendRawAsync(frame, timeout.Token);
                malformed.Socket.Shutdown(SocketShutdown.Send);
                Assert.Null(await malformed.ReadAsync(timeout.Token));
            }
            Assert.NotNull((await existing.CallAsync("getCapabilities", null, timeout.Token))["result"]);
            using var subsequent = new NativeSocketTestClient(listener.EndPoint);
            Assert.True((await subsequent.CallAsync("authenticate", new JsonArray(Token), timeout.Token))["result"]!.GetValue<bool>());
            Assert.Contains(diagnostics.Writes, value => value.Message is "Native connection closed: InvalidDataException." or
                "Native connection closed: EndOfStreamException.");
        }
        finally
        {
            await timeout.CancelAsync();
            await run.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task UnexpectedExecutorFailureIsSanitizedAndDoesNotStopTheListener()
    {
        using var capture = new DiagnosticCapture(sampleActivities: true);
        using var context = new RuntimeRpcTestContext(new WorkloadTestExecutor
        {
            StartFailure = new HttpRequestException("secret-token-and-controller-address")
        });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var logSink = new TestSink();
        using var listener = new NativeRpcListener(new IPEndPoint(IPAddress.Loopback, 0), Token,
            new TestLogger(nameof(NativeRpcListener), logSink, enabled: true), context.Server);
        var run = listener.RunAsync(timeout.Token);
        try
        {
            using var failing = new NativeSocketTestClient(listener.EndPoint);
            await failing.CallAsync("authenticate", new JsonArray(Token), timeout.Token);
            var server = Result(await failing.CallAsync("getApplicationServer", null, timeout.Token));
            var authorServer = Result(context.Author.Call("getApplicationServer", null));
            var session = Result(context.Author.Invoke("openApplication", Arguments(("context", authorServer))));
            var composition = context.Author.StartGeneration(session);
            var resource = context.Author.AddResource(composition, "failure");
            var execution = Result(context.Author.Invoke("createExecution", Arguments(("context", composition))));
            var invitation = Result(context.Author.Invoke("inviteResourceExecution",
                Arguments(("context", execution), ("resource", resource))));
            var writer = Result(await failing.CallAsync("invokeCapability", new JsonArray("Aspire.Hosting.Native.Api/joinResourceExecution",
                Arguments(("context", server), ("invitation", invitation))), timeout.Token));
            var response = await failing.CallAsync("invokeCapability", new JsonArray("Aspire.Hosting.Native.Api/startContainer",
                Arguments(("context", writer), ("container", new JsonObject
                {
                    ["image"] = "test-image", ["targetPort"] = 0,
                    ["environment"] = new JsonArray(), ["arguments"] = new JsonArray()
                }))), timeout.Token);
            var error = response["result"]!["$error"]!.AsObject();
            Assert.Equal(["code", "message"], error.Select(property => property.Key));
            Assert.Equal("INTERNAL_ERROR", error["code"]!.GetValue<string>());
            Assert.Equal("The request failed internally.", error["message"]!.GetValue<string>());
            Assert.NotNull((await failing.CallAsync("getCapabilities", null, timeout.Token))["result"]);
            using var subsequent = new NativeSocketTestClient(listener.EndPoint);
            Assert.True((await subsequent.CallAsync("authenticate", new JsonArray(Token), timeout.Token))["result"]!.GetValue<bool>());
            var failure = Assert.Single(capture.Events, value => value.Operation == "rpc.request" &&
                value.ErrorType == typeof(HttpRequestException).FullName);
            Assert.Equal(OperationOutcome.Error, failure.Outcome);
            var activity = Assert.Single(capture.Activities, value => value.OperationName == "rpc.request" &&
                value.GetTagItem("error.type") as string == typeof(HttpRequestException).FullName);
            Assert.Null(activity.StatusDescription);
            var loggedFailure = Assert.Single(logSink.Writes, value => value.LogLevel == LogLevel.Error);
            Assert.Equal("Native request failed: HttpRequestException.", loggedFailure.Message);
            Assert.Null(loggedFailure.Exception);
        }
        finally
        {
            await timeout.CancelAsync();
            await run.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Theory]
    [InlineData("Content-Length: invalid\r\n\r\n")]
    [InlineData("Content-Length: 1\r\nContent-Length: 1\r\n\r\n")]
    [InlineData("Content-Length: 262145\r\n\r\n")]
    [InlineData("Content-Length: 10\r\n\r\n{}")]
    [InlineData("Content-Length: 51\r\n\r\n{\"jsonrpc\":\"1.0\",\"id\":1,\"method\":\"GetCapabilities\"}")]
    public async Task MalformedBackchannelPeerCanReconnect(string frame)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var host = new NativeSocketTestHost((_, _) => Task.FromResult(
            NativeBackchannelReply.Value(new[] { "baseline.v2" }, NativeCliJsonContext.Default.StringArray)));
        using (var malformed = new NativeSocketTestClient(host.EndPoint))
        {
            await malformed.SendRawAsync(frame, timeout.Token);
            malformed.Socket.Shutdown(SocketShutdown.Send);
            Assert.Null(await malformed.ReadAsync(timeout.Token));
        }
        using var next = new NativeSocketTestClient(host.EndPoint);
        var response = await next.CallAsync("GetCapabilitiesAsync", null, timeout.Token);
        Assert.Equal(["baseline.v2"], response["result"]!.AsArray().Select(value => value!.GetValue<string>()));
        Assert.False(host.Completion.IsCompleted);
    }

    [Fact]
    public async Task BackchannelTransportFailureDuringDisconnectDrainAllowsReconnect()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = new NativeSocketTestHost(async (request, cancellationToken) =>
        {
            if (request.Method == "pending")
            {
                started.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    drained.TrySetResult();
                    // A response transport can report a direct SocketException while
                    // cancellation drains work after the client has already disconnected.
                    throw new SocketException((int)SocketError.ConnectionReset);
                }
            }
            return NativeBackchannelReply.Value("ready", NativeCliJsonContext.Default.String);
        });
        using (var client = new NativeSocketTestClient(host.EndPoint))
        {
            await client.SendAsync("pending", null, timeout.Token);
            await started.Task.WaitAsync(timeout.Token);
            client.Socket.LingerState = new LingerOption(true, 0);
        }
        await drained.Task.WaitAsync(timeout.Token);
        using var next = new NativeSocketTestClient(host.EndPoint);
        Assert.Equal("ready", (await next.CallAsync("ready", null, timeout.Token))["result"]!.GetValue<string>());
        Assert.False(host.Completion.IsCompleted);
    }

    [Fact]
    public async Task BackchannelDisconnectDuringActualResponseWriteAllowsReconnect()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var host = new NativeSocketTestHost((request, _) => Task.FromResult(
            NativeBackchannelReply.Value(request.Method == "large" ? new string('x', 4 * 1024 * 1024) : "ready",
                NativeCliJsonContext.Default.String)));
        using (var client = new NativeSocketTestClient(host.EndPoint))
        {
            await client.SendAsync("large", null, timeout.Token);
            // Receiving the first header byte proves a real response write has begun.
            Assert.Equal(1, await client.Socket.ReceiveAsync(new byte[1], SocketFlags.None, timeout.Token));
            client.Socket.LingerState = new LingerOption(true, 0);
        }
        using var next = new NativeSocketTestClient(host.EndPoint);
        Assert.Equal("ready", (await next.CallAsync("ready", null, timeout.Token))["result"]!.GetValue<string>());
        Assert.False(host.Completion.IsCompleted);
    }

    [Fact]
    public void UnixListenerRequiresPrivateParentWithoutRepairingSharedPermissions()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        using var directory = new NativeSocketTestDirectory();
        var parent = Path.GetDirectoryName(directory.SocketPath)!;
        var permissive = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
        File.SetUnixFileMode(parent, permissive);
        Assert.Throws<IOException>(() => new NativeRpcListener(new UnixDomainSocketEndPoint(directory.SocketPath), Token, NullLogger.Instance));
        Assert.Equal(permissive, File.GetUnixFileMode(parent));
        Assert.False(File.Exists(directory.SocketPath));
    }

    [Fact]
    public void UnixListenerSecuresSocketAndUnlinksOnlyItsOwnedEndpointOnce()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        using var directory = new NativeSocketTestDirectory();
        var endPoint = new UnixDomainSocketEndPoint(directory.SocketPath);
        var first = new NativeRpcListener(endPoint, Token, NullLogger.Instance);
        try
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(directory.SocketPath));
            Assert.Throws<SocketException>(() => new NativeRpcListener(endPoint, Token, NullLogger.Instance));
            Assert.True(File.Exists(directory.SocketPath));
        }
        finally
        {
            first.Dispose();
        }
        Assert.False(File.Exists(directory.SocketPath));
        using var replacement = new NativeRpcListener(endPoint, Token, NullLogger.Instance);
        first.Dispose();
        Assert.True(File.Exists(directory.SocketPath));
        using var client = new NativeSocketTestClient(endPoint);
    }

    [Fact]
    public async Task UnixListenerDisposalDrainsActiveConnectionsAndAllowsRebind()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        using var directory = new NativeSocketTestDirectory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var endPoint = new UnixDomainSocketEndPoint(directory.SocketPath);
        using var listener = new NativeRpcListener(endPoint, Token, NullLogger.Instance);
        var run = listener.RunAsync(timeout.Token);
        using var client = new NativeSocketTestClient(endPoint);
        try
        {
            Assert.True((await client.CallAsync("authenticate", new JsonArray(Token), timeout.Token))["result"]!.GetValue<bool>());
            listener.Dispose();
            await run.WaitAsync(timeout.Token);
            Assert.Null(await client.ReadAsync(timeout.Token));
            using var replacement = new NativeRpcListener(endPoint, Token, NullLogger.Instance);
            using var next = new NativeSocketTestClient(endPoint);
        }
        finally
        {
            await timeout.CancelAsync();
            await run.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task ErrorOutcomeWithoutErrorTypeHasErrorLogSeverity()
    {
        var logs = new NativeCliLogBuffer(2);
        logs.Append(new OperationEvent(Guid.NewGuid(), "rpc.request", OperationOutcome.Error, null, null, null, null, null,
            DateTimeOffset.UtcNow, 0, null));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var entries = logs.ReadAsync(timeout.Token).GetAsyncEnumerator();
        Assert.True(await entries.MoveNextAsync());
        Assert.Equal(LogLevel.Error, entries.Current.LogLevel);
        Assert.Equal("rpc.request: error", entries.Current.Message);
    }
}
