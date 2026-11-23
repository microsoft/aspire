// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Sockets;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Aspire.Hosting.Native.Api;
using Aspire.Hosting.Native.Cli;
using Aspire.Hosting.Native.Core.Tests.TestServices;
using Aspire.Hosting.Native.Diagnostics;
using Aspire.Hosting.Native.Server;
using Aspire.Hosting.Native.Rpc;
using Aspire.Shared;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using StreamJsonRpc;

namespace Aspire.Hosting.Native.Core.Tests;

public class NativeCliProtocolTests
{
    [Fact]
    public void ListenerRejectsInsecureDirectoryWithoutChangingItsPermissions()
    {
        using var directory = new NativeSocketTestDirectory();
        var parent = new DirectoryInfo(Path.GetDirectoryName(directory.SocketPath)!);
        SocketPermissionHelper.CreateDirectory(parent.FullName, repairExisting: true);
        if (OperatingSystem.IsWindows())
        {
            var security = parent.GetAccessControl();
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.ReadAndExecute,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
            parent.SetAccessControl(security);
            var expected = parent.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.All);
            Assert.Throws<IOException>(() => CreateProtocol(directory.SocketPath));
            Assert.Equal(expected, parent.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.All));
        }
        else
        {
            var permissions = File.GetUnixFileMode(parent.FullName) | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
            File.SetUnixFileMode(parent.FullName, permissions);
            Assert.Throws<IOException>(() => CreateProtocol(directory.SocketPath));
            Assert.Equal(permissions, File.GetUnixFileMode(parent.FullName));
        }
        Assert.False(File.Exists(directory.SocketPath));
    }

    [Fact]
    public void ListenerRejectsSymbolicDirectoryWithoutChangingTargetPermissions()
    {
        if (OperatingSystem.IsWindows())
        {
            // Creating symlinks requires developer mode or elevation on Windows.
            return;
        }
        using var directory = new NativeSocketTestDirectory();
        var parent = Path.GetDirectoryName(directory.SocketPath)!;
        var target = Directory.CreateDirectory(Path.Combine(parent, "target"));
        var original = File.GetUnixFileMode(target.FullName);
        var link = Path.Combine(parent, "link");
        Directory.CreateSymbolicLink(link, target.FullName);
        Assert.Throws<IOException>(() => CreateProtocol(Path.Combine(link, "cli.sock")));
        Assert.Equal(original, File.GetUnixFileMode(target.FullName));
        Assert.False(File.Exists(Path.Combine(target.FullName, "cli.sock")));
    }

    [Fact]
    public void ExistingEndpointCannotBeReplacedAndRepeatedDisposalPreservesTheNextListener()
    {
        using var directory = new NativeSocketTestDirectory();
        SocketPermissionHelper.CreateDirectory(Path.GetDirectoryName(directory.SocketPath)!, repairExisting: true);
        var first = CreateProtocol(directory.SocketPath);
        try
        {
            Assert.Throws<ArgumentException>(() => CreateProtocol(directory.SocketPath));
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(directory.SocketPath));
            }
        }
        finally
        {
            first.Dispose();
        }
        using var replacement = CreateProtocol(directory.SocketPath);
        first.Dispose();
        using var client = new NativeSocketTestClient(new UnixDomainSocketEndPoint(directory.SocketPath));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Content-Length: 100\r\n\r\n{")]
    public async Task SilentAndPartialFramesExpireWithoutBlockingOtherCliClients(string frame)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var clock = new FakeTimeProvider();
        var options = new NativeServerOptions { TimeProvider = clock, MaximumConnections = 2 };
        var rpc = new RecordingBackchannelRpc(new NativeBackchannelRpc(options, NullLogger.Instance));
        await using var host = NativeCliTestHost.Start(new NativeApplicationServer(), options, () => new(), rpc);
        await rpc.WaitForConnectionAsync(timeout.Token);
        Assert.Equal(["baseline.v2", "pipeline-steps.v2"], await host.Rpc.InvokeAsync<string[]>("GetCapabilitiesAsync"));
        using var stalled = new NativeSocketTestClient(new UnixDomainSocketEndPoint(host.SocketPath));
        await stalled.SendRawAsync(frame, timeout.Token);
        await rpc.WaitForConnectionAsync(timeout.Token);
        var disconnected = stalled.ReadAsync(timeout.Token);
        Assert.Equal(["baseline.v2", "pipeline-steps.v2"], await host.Rpc.InvokeAsync<string[]>("GetCapabilitiesAsync"));
        using (var excess = new NativeSocketTestClient(new UnixDomainSocketEndPoint(host.SocketPath)))
        {
            Assert.Null(await excess.ReadAsync(timeout.Token));
        }
        clock.Advance(options.BackchannelFirstRequestTimeout - TimeSpan.FromTicks(1));
        Assert.False(disconnected.IsCompleted);
        clock.Advance(TimeSpan.FromTicks(1));
        Assert.Null(await disconnected);
        using var replacement = new NativeSocketTestClient(new UnixDomainSocketEndPoint(host.SocketPath));
        await rpc.WaitForConnectionAsync(timeout.Token);
        Assert.Equal(["baseline.v2", "pipeline-steps.v2"],
            (await replacement.CallAsync("GetCapabilitiesAsync", null, timeout.Token))["result"]!
                .AsArray().Select(value => value!.GetValue<string>()));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"id":null}""")]
    [InlineData("""{"id":true}""")]
    [InlineData("""{"id":[]}""")]
    [InlineData("""{"id":{}}""")]
    public async Task MalformedCancellationDisconnectsWithoutEscapingProtocolHandlers(string parameters)
    {
        using var stream = new MemoryStream();
        var notification = JsonSerializer.SerializeToUtf8Bytes(new
        {
            jsonrpc = "2.0", method = "$/cancelRequest", @params = JsonSerializer.Deserialize<JsonElement>(parameters)
        });
        await NativeRpcFraming.WriteAsync(stream, notification, TestContext.Current.CancellationToken);
        stream.Position = 0;
        var rpc = new NativeBackchannelRpc(new(), NullLogger.Instance);
        await rpc.ServeAsync(stream, (_, _) => throw new InvalidOperationException("Cancellation must not reach request dispatch."),
            TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData("""{"id":"pending"}""", "\"pending\"")]
    [InlineData("""{"id":42}""", "42")]
    public void CancellationPreservesStringAndNumberRequestIds(string parameters, string expected)
    {
        using var document = JsonDocument.Parse(parameters);
        var request = new NativeCliRequest { Jsonrpc = "2.0", Method = "$/cancelRequest", Params = document.RootElement };

        Assert.Equal(expected, NativeBackchannelProtocol.ReadCancellationId(request));
    }

    [Fact]
    public async Task RpcImplementationCanBeReplacedWithoutChangingProtocolDependencies()
    {
        var options = new NativeServerOptions();
        var rpc = new RecordingBackchannelRpc(new NativeBackchannelRpc(options, NullLogger.Instance));
        await using var host = NativeCliTestHost.Start(new NativeApplicationServer(), options, () => new(), rpc);
        Assert.Equal(["baseline.v2", "pipeline-steps.v2"], await host.Rpc.InvokeAsync<string[]>("GetCapabilitiesAsync"));
        await host.Rpc.InvokeAsync("NotifyAppHostReadyAsync");
        Assert.True(host.IsReady);
        Assert.Equal(["GetCapabilitiesAsync", "NotifyAppHostReadyAsync"], rpc.Methods);
    }

    [Fact]
    public async Task ExistingRpcNamesNegotiateBeforeCompositionAndReportRealReadinessAndDashboardState()
    {
        var urls = new NativeCliDashboardUrls { BaseUrlWithLoginToken = "http://localhost:48123/login?t=test" };
        await using var host = NativeCliTestHost.Start(new NativeApplicationServer(), new(), () => urls);
        Assert.Equal(["baseline.v2", "pipeline-steps.v2"], await host.Rpc.InvokeAsync<string[]>("GetCapabilitiesAsync"));
        Assert.False(host.IsReady);
        await host.Rpc.InvokeAsync("NotifyAppHostReadyAsync");
        Assert.True(host.IsReady);
        var dashboard = await host.Rpc.InvokeAsync<NativeCliDashboardUrls>("GetDashboardUrlsAsync");
        Assert.Equal(urls, dashboard);
        await Assert.ThrowsAsync<RemoteMethodNotFoundException>(() => host.Rpc.InvokeAsync("UnknownMethod"));
        // The stop response must arrive before the listener is cancelled.
        await host.Rpc.InvokeAsync("RequestStopAsync");
    }

    [Fact]
    public async Task ResourceStreamFollowsArbitraryTypesAndGenerationReplacement()
    {
        var server = new NativeApplicationServer();
        await using var host = NativeCliTestHost.Start(server, new(), () => new());
        var stream = await host.Rpc.InvokeAsync<IAsyncEnumerable<NativeCliResourceState>>("GetResourceStatesAsync");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var enumerator = stream.GetAsyncEnumerator(timeout.Token);
        var session = server.OpenApplication();
        var composition = session.StartGeneration();
        composition.AddResource("arbitrary-job", "third-party/BackgroundWorker");
        composition.CreateExecution();
        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal("arbitrary-job", enumerator.Current.Resource);
        Assert.Equal("third-party/BackgroundWorker", enumerator.Current.Type);
        await session.RetireGeneration(composition);
        var replacement = session.StartGeneration();
        replacement.AddResource("another-job", "custom/Resource");
        replacement.CreateExecution();
        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal("another-job", enumerator.Current.Resource);
        Assert.Equal("custom/Resource", enumerator.Current.Type);
        session.Close();
    }

    [Fact]
    public async Task DiagnosticLogStreamUsesTypedPayloadsAndConfiguredRetention()
    {
        var options = new NativeServerOptions { RetainedAppHostLogEntries = 2 };
        await using var host = NativeCliTestHost.Start(new NativeApplicationServer(), options, () => new());
        var generation = Guid.NewGuid();
        for (var i = 0; i < 3; i++)
        {
            host.Logs.Append(new OperationEvent(Guid.NewGuid(), "test.operation", OperationOutcome.Error, null, generation,
                null, null, null, DateTimeOffset.UtcNow, 0, "TestFailure"));
        }
        var stream = await host.Rpc.InvokeAsync<IAsyncEnumerable<NativeCliLogEntry>>("GetAppHostLogEntriesAsync");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var enumerator = stream.GetAsyncEnumerator(timeout.Token);
        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal(2, enumerator.Current.SequenceNumber);
        Assert.Equal(LogLevel.Error, enumerator.Current.LogLevel);
        Assert.Equal("test.operation: error (TestFailure)", enumerator.Current.Message);
        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal(3, enumerator.Current.SequenceNumber);
        var pending = enumerator.MoveNextAsync().AsTask();
        await timeout.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(["baseline.v2", "pipeline-steps.v2"], await host.Rpc.InvokeAsync<string[]>("GetCapabilitiesAsync"));
    }

    [Fact]
    public async Task StreamCapacityIsConfiguredAndAbortReleasesItsSlot()
    {
        await using var host = NativeCliTestHost.Start(new NativeApplicationServer(),
            new NativeServerOptions { MaximumCliStreams = 1 }, () => new());
        var resources = await host.Rpc.InvokeAsync<IAsyncEnumerable<NativeCliResourceState>>("GetResourceStatesAsync");
        await using (var enumerator = resources.GetAsyncEnumerator())
        {
            await Assert.ThrowsAsync<RemoteInvocationException>(() =>
                host.Rpc.InvokeAsync<IAsyncEnumerable<NativeCliLogEntry>>("GetAppHostLogEntriesAsync"));
        }
        var logs = await host.Rpc.InvokeAsync<IAsyncEnumerable<NativeCliLogEntry>>("GetAppHostLogEntriesAsync");
        await using var logEnumerator = logs.GetAsyncEnumerator();
    }

    private static NativeCliProtocol CreateProtocol(string path)
    {
        var options = new NativeServerOptions();

        return new(path, new NativeApplicationServer(), () => { }, _ => Task.FromResult(new NativeCliDashboardUrls()),
            new NativeCliLogBuffer(options.RetainedAppHostLogEntries), options, new NativeBackchannelRpc(options, NullLogger.Instance));
    }
}
