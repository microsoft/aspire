// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO.Pipelines;
using Aspire.Hosting.Terminals;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Hex1b;
using Microsoft.AspNetCore.InternalTesting;

#pragma warning disable ASPIRETERMINAL002 // Test consumer of the experimental AppHost terminal API.

namespace Aspire.Hosting.Tests.Terminals;

[Trait("Partition", "2")]
public class Hex1bAspireTerminalTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WorkloadExit_EndsAutomationAndKeepsTabUntilDisposed(bool attachViewer)
    {
        await using var service = TestTerminalService.Create();
        var output = new Pipe();
        await using var outputReader = output.Reader.AsStream();
        await using var outputWriter = output.Writer.AsStream();
        var workload = new StreamWorkloadAdapter(outputReader, Stream.Null);
        await using var terminal = service.CreateTerminal("Ended", TerminalPlacement.Dock,
            Hex1bTerminal.CreateBuilder().WithWorkload(workload));
        await using var viewer = attachViewer ? await TestAppHostTerminalViewer.ConnectAsync(service, terminal.Id) : null;
        terminal.Start();
        await outputWriter.WriteAsync("ready\r\n"u8.ToArray());
        await terminal.WaitForTextAsync("ready").DefaultTimeout();

        // Raw stream workloads report disconnection explicitly. Observe output first; Hex1b's completion is
        // not an output-drain barrier, and this test makes no claim about preserving the final screen.
        workload.SignalDisconnected();
        await Assert.IsType<Hex1bAspireTerminal>(terminal.Backend).WorkloadEnded.DefaultTimeout();

        Assert.True(service.TryGetTerminal(terminal.Id, out var registered));
        Assert.Same(terminal, registered);
        using var subscription = service.SubscribeDockTerminals();
        Assert.Equal(terminal.Id, Assert.Single(subscription.InitialState).Id);
        Assert.Throws<InvalidOperationException>(terminal.Start);
        Assert.Throws<InvalidOperationException>(terminal.GetScreenText);
        await Assert.ThrowsAsync<InvalidOperationException>(() => terminal.SendTextAsync("input"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => terminal.SendKeyAsync(AspireTerminalKey.Enter));
        await Assert.ThrowsAsync<InvalidOperationException>(() => terminal.WaitForTextAsync("never"));

        // Reopening an ended tab must report completion without queuing a client for Hex1b's disposed server.
        // In particular, it must not need a ClientHello or restart the workload.
        using var reconnected = new MemoryStream();
        var endedNotifications = 0;
        await service.AttachAsync(terminal.Id, reconnected, _ =>
        {
            endedNotifications++;
            return Task.CompletedTask;
        }, CancellationToken.None).DefaultTimeout();
        Assert.Equal(1, endedNotifications);
        Assert.Equal(0, reconnected.Length);

        await terminal.DisposeAsync().AsTask().DefaultTimeout();
        Assert.False(service.TryGetTerminal(terminal.Id, out _));
        using var afterClose = service.SubscribeDockTerminals();
        Assert.Empty(afterClose.InitialState);
    }

    [Fact]
    public async Task AttachAsync_MultipleViewersCanDisconnectAndReconnectWithoutStoppingTheWorkload()
    {
        await using var service = TestTerminalService.Create();
        var output = new Pipe();
        var input = new Pipe();
        await using var outputReader = output.Reader.AsStream();
        await using var outputWriter = output.Writer.AsStream();
        await using var inputReader = input.Reader.AsStream();
        await using var inputWriter = input.Writer.AsStream();
        var workload = new StreamWorkloadAdapter(outputReader, inputWriter);
        await using var terminal = service.CreateTerminal("Shared", TerminalPlacement.Dialog,
            Hex1bTerminal.CreateBuilder().WithDimensions(80, 24).WithWorkload(workload));

        // Attach starts the previously idle workload. The other viewer must survive the first peer's EOF,
        // and a later viewer must receive the same terminal's existing screen rather than a fresh process.
        await using var first = await TestAppHostTerminalViewer.ConnectAsync(service, terminal.Id);
        await using var second = await TestAppHostTerminalViewer.ConnectAsync(service, terminal.Id);
        await outputWriter.WriteAsync("before-disconnect\r\n"u8.ToArray());
        await Task.WhenAll(
            first.WaitForTextAsync("before-disconnect"),
            second.WaitForTextAsync("before-disconnect")).DefaultTimeout();

        await first.DisconnectPeerAsync().DefaultTimeout();
        Assert.True(service.TryGetTerminal(terminal.Id, out var registered));
        Assert.Same(terminal, registered);

        await second.SendTextAsync("viewer-input").DefaultTimeout();
        var bytes = new byte["viewer-input"u8.Length];
        await inputReader.ReadExactlyAsync(bytes).AsTask().DefaultTimeout();
        Assert.Equal("viewer-input"u8.ToArray(), bytes);

        await terminal.SendTextAsync("automation-input").DefaultTimeout();
        bytes = new byte["automation-input"u8.Length];
        await inputReader.ReadExactlyAsync(bytes).AsTask().DefaultTimeout();
        Assert.Equal("automation-input"u8.ToArray(), bytes);

        await terminal.SendKeyAsync(AspireTerminalKey.Enter).DefaultTimeout();
        bytes = new byte[1];
        await inputReader.ReadExactlyAsync(bytes).AsTask().DefaultTimeout();
        Assert.Equal("\r"u8.ToArray(), bytes);

        await outputWriter.WriteAsync("after-disconnect\r\n"u8.ToArray());
        await Task.WhenAll(
            second.WaitForTextAsync("after-disconnect"),
            terminal.WaitForTextAsync("after-disconnect")).DefaultTimeout();
        Assert.Contains("after-disconnect", terminal.GetScreenText());

        await using var reconnected = await TestAppHostTerminalViewer.ConnectAsync(service, terminal.Id);
        await reconnected.WaitForTextAsync("after-disconnect").DefaultTimeout();
    }

    [Fact]
    public async Task AttachAsync_CancellationDuringHandshakeWaitsForTheOutstandingWrite()
    {
        await using var service = TestTerminalService.Create();
        var output = new Pipe();
        await using var outputReader = output.Reader.AsStream();
        await using var outputWriter = output.Writer.AsStream();
        var workload = new StreamWorkloadAdapter(outputReader, Stream.Null);
        await using var terminal = service.CreateTerminal("Handshake", TerminalPlacement.Dialog,
            Hex1bTerminal.CreateBuilder().WithWorkload(workload));

        var (serverStream, clientStream) = TestDuplexStream.CreatePair();
        using var serverOwner = serverStream;
        using var clientOwner = clientStream;
        using var gated = new GatedTerminalWriteStream(serverStream);
        using var attachmentCts = new CancellationTokenSource();
        using var clientCts = new CancellationTokenSource();
        await using var client = Hex1bTerminal.CreateBuilder().WithHeadless().WithHmp1Stream(clientStream).Build();
        var attachment = service.AttachAsync(terminal.Id, gated, _ => Task.CompletedTask, attachmentCts.Token);
        var run = client.RunAsync(clientCts.Token);

        try
        {
            await gated.WriteStarted.DefaultTimeout();
            await attachmentCts.CancelAsync();
            await gated.WriteCancelled.DefaultTimeout();
            Assert.False(attachment.IsCompleted);
        }
        finally
        {
            gated.ReleaseWrite();
            await attachmentCts.CancelAsync();
            await attachment.DefaultTimeout();
            await clientCts.CancelAsync();
            try
            {
                await run.DefaultTimeout();
            }
            catch (OperationCanceledException) when (clientCts.IsCancellationRequested)
            {
            }

            serverStream.Dispose();
        }

        // The cancelled viewer did not cancel the AppHost terminal or poison subsequent attachments.
        await using var replacement = await TestAppHostTerminalViewer.ConnectAsync(service, terminal.Id);
        await outputWriter.WriteAsync("replacement-ready\r\n"u8.ToArray());
        await replacement.WaitForTextAsync("replacement-ready").DefaultTimeout();
    }
}
