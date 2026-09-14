// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Globalization;
using System.IO.Pipelines;
using System.Text;
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
    public async Task DisposeAsync_TerminatesPtyProcessIgnoringHangupAndTermination(bool disposeService)
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS(), "The workload uses POSIX signals.");

        await using var service = TestTerminalService.Create();
        await using var terminal = service.CreateTerminal(new TerminalLaunchOptions
        {
            Title = "Signal-resistant process",
            Placement = TerminalPlacement.None,
            Command = new TerminalCommand("/bin/sh")
            {
                // Ignored signals survive exec. The fixed sleep is a backstop if the test host is killed.
                Arguments = ["-c", "trap '' HUP TERM; printf 'pid:%s\\nprocess-ready\\n' \"$$\"; exec sleep 300"]
            }
        });
        terminal.Start();
        await terminal.WaitForTextAsync("process-ready").DefaultTimeout();

        // The workload emits "pid:12345\r\nprocess-ready\r\n"; terminal rows can have trailing spaces.
        var pidLine = Assert.Single(
            terminal.GetScreenText().Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
            line => line.StartsWith("pid:", StringComparison.Ordinal));
        var pid = int.Parse(pidLine["pid:".Length..], CultureInfo.InvariantCulture);
        using var process = Process.GetProcessById(pid);
        try
        {
            Assert.False(process.HasExited);
            var disposal = disposeService ? service.DisposeAsync().AsTask() : terminal.DisposeAsync().AsTask();
            await disposal.DefaultTimeout();

            Assert.True(process.HasExited);
            Assert.False(service.TryGetTerminal(terminal.Id, out _));
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().DefaultTimeout();
            }
        }
    }

    [Theory]
    [InlineData(TerminalPlacement.Dock)]
    [InlineData(TerminalPlacement.Dialog)]
    public async Task Resize_ReflowsMainScreenAndRetainsHistory(TerminalPlacement placement)
    {
        await using var service = TestTerminalService.Create();
        var output = new Pipe();
        await using var outputReader = output.Reader.AsStream();
        await using var outputWriter = output.Writer.AsStream();
        await using var terminal = service.CreateTerminal("Reflow", placement,
            Hex1bTerminal.CreateBuilder().WithWorkload(new StreamWorkloadAdapter(outputReader, Stream.Null)));
        await using var viewer = await TestAppHostTerminalViewer.ConnectAsync(service, terminal.Id);
        var lines = Enumerable.Range(0, 7).Select(i => $"{i}:" + new string('x', 63) + "-END").ToArray();
        var expected = string.Join('\n', lines.Select(line => line.PadRight(80)).Append("ready"));
        await outputWriter.WriteAsync(Encoding.UTF8.GetBytes(string.Join("\r\n", lines) + "\r\nready"));
        await terminal.WaitForTextAsync("ready").DefaultTimeout();
        Assert.Equal(expected, terminal.GetScreenText().TrimEnd());

        // Narrowing pushes wrapped rows into history. Widening must restore them without new output.
        await viewer.ResizeAsync(20, 4);
        await viewer.ResizeAsync(80, 24);
        Assert.Equal(expected, terminal.GetScreenText().TrimEnd());
    }

    [Fact]
    public async Task Resize_CropsAlternateScreenAndReflowsSavedMainScreen()
    {
        await using var service = TestTerminalService.Create();
        var output = new Pipe();
        await using var outputReader = output.Reader.AsStream();
        await using var outputWriter = output.Writer.AsStream();
        await using var terminal = service.CreateTerminal("Alternate", TerminalPlacement.Dock,
            Hex1bTerminal.CreateBuilder().WithWorkload(new StreamWorkloadAdapter(outputReader, Stream.Null)));
        await using var viewer = await TestAppHostTerminalViewer.ConnectAsync(service, terminal.Id);
        const string main = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz-END";
        await outputWriter.WriteAsync(Encoding.UTF8.GetBytes(main + "\r\nready"));
        await terminal.WaitForTextAsync("ready").DefaultTimeout();
        // DECSET 1049 enters the alternate screen; CUP positions a fixed-layout row.
        await outputWriter.WriteAsync("\u001b[?1049h\u001b[HALTERNATE-ABCDEFGHIJKLMNOPQRSTUVWXYZ\r\nalt-ready"u8.ToArray());
        await terminal.WaitForTextAsync("alt-ready").DefaultTimeout();

        await viewer.ResizeAsync(20, 24);
        Assert.Equal("ALTERNATE-ABCDEFGHIJ\nalt-ready", terminal.GetScreenText().TrimEnd());
        await outputWriter.WriteAsync("\u001b[?1049l"u8.ToArray());
        await terminal.WaitForTextAsync("ABCDEFGHIJKLMNOPQRST").DefaultTimeout();
        Assert.Equal(string.Join('\n', main.Chunk(20).Select(chunk => new string(chunk).PadRight(20)).Append("ready")),
            terminal.GetScreenText().TrimEnd());
        await viewer.ResizeAsync(80, 24);
        Assert.Equal(main.PadRight(80) + "\nready", terminal.GetScreenText().TrimEnd());
    }

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
