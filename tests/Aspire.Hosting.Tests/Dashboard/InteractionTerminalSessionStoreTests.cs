// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO.Pipelines;
using Aspire.Hosting.Dashboard;
using Hex1b;
using Hex1b.Automation;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Hosting.Tests.Dashboard;

public class InteractionTerminalSessionStoreTests
{
    private const int InteractionId = 42;
    private const string InputName = "shell";

    [Fact]
    public async Task AttachAsync_UnknownInteraction_Throws()
    {
        using var store = CreateStore();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.AttachAsync(InteractionId, InputName, Stream.Null, CancellationToken.None));
        Assert.Contains("does not have a terminal input", ex.Message);
    }

    [Fact]
    public async Task AttachAsync_UnknownInput_Throws()
    {
        using var store = CreateStore();
        store.StartInteraction(InteractionId, [(InputName, CreateServerBuilder("true"))]);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.AttachAsync(InteractionId, "other", Stream.Null, CancellationToken.None));
        Assert.Contains("does not have a terminal input", ex.Message);
    }

    [Fact]
    public async Task AttachAsync_AfterInteractionCompleted_Throws()
    {
        using var store = CreateStore();
        store.StartInteraction(InteractionId, [(InputName, CreateServerBuilder("true"))]);
        store.CompleteInteraction(InteractionId);

        // The interaction is no longer tracked at all, so this fails the same way an unknown interaction does.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.AttachAsync(InteractionId, InputName, Stream.Null, CancellationToken.None));
    }

    [Fact]
    public void StartInteraction_NeverAttached_TearsDownWithoutStartingWorkload()
    {
        using var store = CreateStore();
        store.StartInteraction(InteractionId, [(InputName, CreateServerBuilder("exit 7"))]);

        // No client ever attached, so no terminal was built and teardown must not hang or throw.
        store.CancelInteraction(InteractionId);
    }

    [Fact]
    public async Task AttachAsync_ServesWorkloadOutputOverStream()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Uses /bin/sh to produce deterministic workload output.");

        using var store = CreateStore();
        store.StartInteraction(InteractionId, [(InputName, CreateServerBuilder("echo aspire-terminal-ok; read line"))]);

        var (serverSide, clientSide) = CreateDuplexPair();
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));

        var attachTask = store.AttachAsync(InteractionId, InputName, serverSide, cts.Token);

        await using var client = CreateClientTerminal(clientSide);
        var clientRunTask = client.RunAsync(cts.Token);

        var automator = new Hex1bTerminalAutomator(client, TimeSpan.FromSeconds(60));
        await automator.WaitUntilAsync(
            snapshot => snapshot.GetText().Contains("aspire-terminal-ok", StringComparison.Ordinal),
            description: "workload output rendered on the client terminal");

        // Tearing down the interaction must release the attached transport rather than stranding it.
        store.CompleteInteraction(InteractionId);
        await attachTask.WaitAsync(cts.Token);

        // Mirrors AttachTerminal, which disposes the tunnel stream once the attach completes. Without this the client
        // has no way to observe that the session is gone.
        serverSide.Dispose();

        await IgnoreShutdownAsync(clientRunTask);
    }

    [Fact]
    public async Task AttachAsync_WorkloadExit_ReleasesAttachedClient()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Uses /bin/sh to produce a workload that exits on its own.");

        using var store = CreateStore();
        store.StartInteraction(InteractionId, [(InputName, CreateServerBuilder("exit 0"))]);

        var (serverSide, clientSide) = CreateDuplexPair();
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));

        var attachTask = store.AttachAsync(InteractionId, InputName, serverSide, cts.Token);

        await using var client = CreateClientTerminal(clientSide);
        var clientRunTask = client.RunAsync(cts.Token);

        // The workload exits immediately, so the attach must complete without the interaction being torn down.
        await attachTask.WaitAsync(cts.Token);

        serverSide.Dispose();

        await IgnoreShutdownAsync(clientRunTask);
    }

    private static InteractionTerminalSessionStore CreateStore()
        => new(NullLogger<InteractionTerminalSessionStore>.Instance);

    /// <summary>
    /// Builds the AppHost-side terminal exactly as a caller would: workload only, no transport. The store attaches the
    /// HMP1 server itself, which is the split the interaction input depends on.
    /// </summary>
    private static Hex1bTerminalBuilder CreateServerBuilder(string shellCommand)
    {
        return Hex1bTerminal.CreateBuilder()
            .WithHeadless()
            .WithDimensions(80, 24)
            .WithPtyProcess("/bin/sh", ["-c", shellCommand]);
    }

    /// <summary>
    /// Builds a real HMP1 client terminal on the far end of the tunnel, standing in for the dashboard's xterm.js client.
    /// </summary>
    private static Hex1bTerminal CreateClientTerminal(Stream clientSide)
    {
        return Hex1bTerminal.CreateBuilder()
            .WithHeadless()
            .WithDimensions(80, 24)
            .WithHmp1Client(_ => Task.FromResult(clientSide))
            .Build();
    }

    /// <summary>
    /// Creates two streams wired back to back, standing in for the gRPC tunnel: what one end writes the other reads.
    /// </summary>
    private static (Stream ServerSide, Stream ClientSide) CreateDuplexPair()
    {
        var serverToClient = new Pipe();
        var clientToServer = new Pipe();

        var serverSide = new DuplexStream(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream());
        var clientSide = new DuplexStream(serverToClient.Reader.AsStream(), clientToServer.Writer.AsStream());
        return (serverSide, clientSide);
    }

    private static async Task IgnoreShutdownAsync(Task<int> clientRunTask)
    {
        try
        {
            await clientRunTask;
        }
        catch (Exception)
        {
            // The client terminal is torn down by the server closing the tunnel; how that surfaces is not under test.
        }
    }

    private sealed class DuplexStream(Stream reader, Stream writer) : Stream
    {
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => reader.ReadAsync(buffer, cancellationToken);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            => writer.WriteAsync(buffer, cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) => reader.Read(buffer, offset, count);

        public override void Write(byte[] buffer, int offset, int count) => writer.Write(buffer, offset, count);

        public override void Flush() => writer.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) => writer.FlushAsync(cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                reader.Dispose();
                writer.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
