// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Terminals;
using Aspire.Hosting.Tests.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net.Sockets;

#pragma warning disable ASPIRETERMINAL002 // Test consumer of the experimental AppHost terminal API.

namespace Aspire.Hosting.Tests.Terminals;

/// <summary>
/// Drives <see cref="ResourceAspireTerminal"/> against a real HMP1 socket rather than a fake, because the
/// behaviour worth guarding here only exists once a client actually attaches: the AppHost has no controlling
/// terminal, so a client that tries to drive one fails at the presentation adapter with a native
/// <c>tcgetattr</c> error that no in-memory substitute reproduces.
/// </summary>
/// <remarks>
/// The stand-in for a replica's terminal host is an ordinary Hex1b terminal serving its own Unix domain
/// socket, which is the same shape the real terminal host exposes as its consumer socket.
/// </remarks>
[Trait("Partition", "2")]
public class ResourceAspireTerminalTests : IAsyncLifetime
{
    private readonly string _socketDirectory = Directory.CreateTempSubdirectory("aspire-resource-terminal-tests-").FullName;

    [Fact]
    public async Task AutomationTypesIntoAndReadsBackFromATerminalHost()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS(), "The workload is a POSIX shell.");

        // A shell is the workload because the round trip being proven is a human-shaped one: type a command,
        // have the workload run it, read the result off the replicated screen.
        await using var host = await TestResourceTerminalHost.StartAsync(CreateSocketPath());

        await using var terminal = new ResourceAspireTerminal("resource:test:0", "test", host.SocketPath, NullLogger.Instance).Handle;

        // The shell echoes the command line before its output, so a fixed marker would match the echo of the
        // input rather than the result. Splitting the literal across a quote means the typed line and the
        // output line differ, and only the output line contains the marker.
        await terminal.SendTextAsync("echo apphost-was\"\"-here").DefaultTimeout();
        await terminal.SendKeyAsync(AspireTerminalKey.Enter).DefaultTimeout();

        await terminal.WaitForTextAsync("apphost-was-here", TimeSpan.FromSeconds(30)).DefaultTimeout();

        Assert.Contains("apphost-was-here", terminal.GetScreenText());
    }

    [Fact]
    public async Task WaitForTextThrowsTimeoutWhenTheTextNeverAppears()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS(), "The workload is a POSIX shell.");

        await using var host = await TestResourceTerminalHost.StartAsync(CreateSocketPath());

        await using var terminal = new ResourceAspireTerminal("resource:test:0", "test", host.SocketPath, NullLogger.Instance).Handle;

        // Establish the connection first so the timeout under test is the wait, not the handshake.
        await terminal.SendTextAsync("\r").DefaultTimeout();

        await Assert.ThrowsAsync<TimeoutException>(
            () => terminal.WaitForTextAsync("text-the-workload-never-writes", TimeSpan.FromSeconds(1))).DefaultTimeout();
    }

    [Fact]
    public async Task AutomationFailsWhenNoTerminalHostIsListening()
    {
        var missingSocket = Path.Combine(_socketDirectory, "not-listening.sock");

        await using var terminal = new ResourceAspireTerminal("resource:test:0", "test", missingSocket, NullLogger.Instance).Handle;

        // A replica whose terminal host is gone must surface as a failed automation call rather than hanging
        // until the connect timeout expires on every subsequent call.
        await Assert.ThrowsAnyAsync<Exception>(() => terminal.SendTextAsync("hello")).DefaultTimeout();
    }

    [Fact]
    public async Task DisposeIsSafeWhenNothingEverConnected()
    {
        var terminal = new ResourceAspireTerminal("resource:test:0", "test", Path.Combine(_socketDirectory, "unused.sock"), NullLogger.Instance).Handle;

        // Listing terminals hands out handles that are never automated, so disposing an unconnected handle is
        // the common case rather than an edge case.
        await terminal.DisposeAsync().AsTask().DefaultTimeout();
    }

    [Fact]
    public async Task AutomationRetriesAfterTheTerminalHostStarts()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS(), "The workload is a POSIX shell.");

        var socketPath = CreateSocketPath();
        await using var terminal = new ResourceAspireTerminal("resource:test:0", "test", socketPath, NullLogger.Instance).Handle;

        await Assert.ThrowsAnyAsync<Exception>(() => terminal.SendTextAsync("not-delivered")).DefaultTimeout();

        await using var host = await TestResourceTerminalHost.StartAsync(socketPath);
        await terminal.SendTextAsync("echo recovered\"\"-connection\r").DefaultTimeout();
        await terminal.WaitForTextAsync("recovered-connection").DefaultTimeout();
        Assert.Contains("recovered-connection", terminal.GetScreenText());
    }

    [Fact]
    public async Task AutomationReconnectsAfterTheTerminalHostRestarts()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS(), "The workload is a POSIX shell.");

        var socketPath = CreateSocketPath();
        await using var firstHost = await TestResourceTerminalHost.StartAsync(socketPath);
        await using var terminal = new ResourceAspireTerminal("resource:test:0", "test", socketPath, NullLogger.Instance).Handle;

        await terminal.SendTextAsync("echo first\"\"-host\r").DefaultTimeout();
        await terminal.WaitForTextAsync("first-host").DefaultTimeout();
        await firstHost.DisposeAsync().AsTask().DefaultTimeout();
        await AsyncTestHelpers.AssertIsTrueRetryAsync(() => terminal.GetScreenText() == string.Empty, "The old automation screen was not invalidated.");

        await using var secondHost = await TestResourceTerminalHost.StartAsync(socketPath);
        await terminal.SendTextAsync("echo replacement\"\"-host\r").DefaultTimeout();
        await terminal.WaitForTextAsync("replacement-host").DefaultTimeout();
        Assert.Contains("replacement-host", terminal.GetScreenText());
    }

    [Fact]
    public async Task CancelingOneCallerDoesNotCancelTheSharedConnectionAttempt()
    {
        var socketPath = CreateSocketPath();
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(socketPath));
        listener.Listen();
        await using var terminal = new ResourceAspireTerminal("resource:test:0", "test", socketPath, NullLogger.Instance).Handle;
        using var cts = new CancellationTokenSource();

        // Accept the transport but withhold the HMP1 handshake so cancellation occurs during connection setup.
        var canceledCall = terminal.SendTextAsync("first", cts.Token);
        using var peer = await listener.AcceptAsync().DefaultTimeout();
        var otherCall = terminal.SendTextAsync("second");

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledCall).DefaultTimeout();
        Assert.False(otherCall.IsCompleted);

        await terminal.DisposeAsync().AsTask().DefaultTimeout();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => otherCall).DefaultTimeout();
    }

    private string CreateSocketPath()
        // Socket paths have a low length limit (around 104 bytes on macOS), so keep the file name short.
        => Path.Combine(_socketDirectory, $"{Guid.NewGuid().ToString("N")[..8]}.sock");

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync()
    {
        Directory.Delete(_socketDirectory, recursive: true);
        return ValueTask.CompletedTask;
    }
}
