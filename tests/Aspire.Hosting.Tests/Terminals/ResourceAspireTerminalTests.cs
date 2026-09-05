// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Terminals;
using Hex1b;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Logging.Abstractions;

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
        // A shell is the workload because the round trip being proven is a human-shaped one: type a command,
        // have the workload run it, read the result off the replicated screen.
        await using var host = await StartTerminalHostAsync("bash");

        await using var terminal = new ResourceAspireTerminal("resource:test:0", "test", host.SocketPath, NullLogger.Instance);

        // The shell echoes the command line before its output, so a fixed marker would match the echo of the
        // input rather than the result. Splitting the literal across a quote means the typed line and the
        // output line differ, and only the output line contains the marker.
        await terminal.SendTextAsync("echo apphost-was\"\"-here\r").DefaultTimeout();

        await terminal.WaitForTextAsync("apphost-was-here", TimeSpan.FromSeconds(30)).DefaultTimeout();

        Assert.Contains("apphost-was-here", terminal.GetScreenText());
    }

    [Fact]
    public async Task WaitForTextThrowsTimeoutWhenTheTextNeverAppears()
    {
        await using var host = await StartTerminalHostAsync("bash");

        await using var terminal = new ResourceAspireTerminal("resource:test:0", "test", host.SocketPath, NullLogger.Instance);

        // Establish the connection first so the timeout under test is the wait, not the handshake.
        await terminal.SendTextAsync("\r").DefaultTimeout();

        await Assert.ThrowsAsync<TimeoutException>(
            () => terminal.WaitForTextAsync("text-the-workload-never-writes", TimeSpan.FromSeconds(1))).DefaultTimeout();
    }

    [Fact]
    public async Task AutomationFailsWhenNoTerminalHostIsListening()
    {
        var missingSocket = Path.Combine(_socketDirectory, "not-listening.sock");

        await using var terminal = new ResourceAspireTerminal("resource:test:0", "test", missingSocket, NullLogger.Instance);

        // A replica whose terminal host is gone must surface as a failed automation call rather than hanging
        // until the connect timeout expires on every subsequent call.
        await Assert.ThrowsAnyAsync<Exception>(() => terminal.SendTextAsync("hello")).DefaultTimeout();
    }

    [Fact]
    public async Task DisposeIsSafeWhenNothingEverConnected()
    {
        var terminal = new ResourceAspireTerminal("resource:test:0", "test", Path.Combine(_socketDirectory, "unused.sock"), NullLogger.Instance);

        // Listing terminals hands out handles that are never automated, so disposing an unconnected handle is
        // the common case rather than an edge case.
        await terminal.DisposeAsync().AsTask().DefaultTimeout();
    }

    /// <summary>
    /// Stands up a terminal serving an HMP1 Unix domain socket, standing in for a replica's terminal host.
    /// </summary>
    private async Task<TerminalHostStub> StartTerminalHostAsync(string shell)
    {
        // Socket paths have a low length limit (around 104 bytes on macOS), so keep the file name short.
        var socketPath = Path.Combine(_socketDirectory, $"{Guid.NewGuid().ToString("N")[..8]}.sock");

        var terminal = Hex1bTerminal.CreateBuilder()
            // The test host has no controlling terminal either, so it is headless for the same reason the
            // AppHost's client is.
            .WithHeadless()
            .WithDimensions(120, 40)
            .WithPtyProcess(shell)
            .WithHmp1UdsServer(socketPath)
            .Build();

        var cts = new CancellationTokenSource();
        var runTask = terminal.RunAsync(cts.Token);

        // The socket file appears when the listener binds, which is what a client can dial.
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!File.Exists(socketPath) && DateTime.UtcNow < deadline)
        {
            if (runTask.IsFaulted)
            {
                await runTask;
            }

            await Task.Delay(25);
        }

        Assert.True(File.Exists(socketPath), $"The terminal host did not begin listening on '{socketPath}'.");

        return new TerminalHostStub(socketPath, terminal, cts, runTask);
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync()
    {
        try
        {
            Directory.Delete(_socketDirectory, recursive: true);
        }
        catch (IOException)
        {
            // A socket file that the runtime still holds open is not worth failing a test over.
        }

        return ValueTask.CompletedTask;
    }

    private sealed class TerminalHostStub(string socketPath, Hex1bTerminal terminal, CancellationTokenSource cts, Task runTask) : IAsyncDisposable
    {
        public string SocketPath { get; } = socketPath;

        public async ValueTask DisposeAsync()
        {
            await cts.CancelAsync();

            try
            {
                await runTask;
            }
            catch (OperationCanceledException)
            {
            }

            await terminal.DisposeAsync();
            cts.Dispose();
        }
    }
}
