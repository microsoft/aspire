// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO.Pipelines;
using Aspire.Hosting.Terminals;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Hex1b;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

#pragma warning disable ASPIREINTERACTION001 // Regression coverage for the shared progress lifecycle.
#pragma warning disable ASPIRETERMINAL001 // Test consumer of the experimental AppHost terminal API.

namespace Aspire.Hosting.Tests.Terminals;

[Trait("Partition", "2")]
public class InteractionServiceTerminalTests
{
    [Fact]
    public async Task PromptTerminalAsync_NullArguments_ThrowsBeforePublishing()
    {
        await using var terminals = TestTerminalService.Create();
        var service = CreateInteractionService(terminals);
        await using var terminal = CreateTerminal(terminals);

        await Assert.ThrowsAsync<ArgumentNullException>("message", () => service.PromptTerminalAsync(null!, terminal));
        await Assert.ThrowsAsync<ArgumentNullException>("terminal", () => service.PromptTerminalAsync("Message", null!));
        Assert.Empty(service.GetCurrentInteractions());
    }

    [Fact]
    public async Task PromptTerminalAsync_Unavailable_ThrowsBeforePublishing()
    {
        await using var terminals = TestTerminalService.Create();
        var service = CreateInteractionService(terminals);
        await using var terminal = CreateTerminal(terminals);
        using var scope = InteractionService.StartNonInteractiveScope();

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.PromptTerminalAsync("Message", terminal));
        Assert.Empty(service.GetCurrentInteractions());
    }

    [Theory]
    [InlineData(TerminalPlacement.Dock)]
    [InlineData(TerminalPlacement.None)]
    public async Task PromptTerminalAsync_NonDialogPlacement_ThrowsBeforePublishing(TerminalPlacement placement)
    {
        await using var terminals = TestTerminalService.Create();
        var service = CreateInteractionService(terminals);
        await using var terminal = CreateTerminal(terminals, placement);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.PromptTerminalAsync("Message", terminal));
        Assert.Equal($"Terminals shown by an interaction must be created with TerminalPlacement.Dialog; the supplied terminal has placement {placement}.", ex.Message);
        Assert.Empty(service.GetCurrentInteractions());
        AssertRegistered(terminals, terminal);
    }

    [Fact]
    public async Task PromptTerminalAsync_TerminalFromAnotherService_ThrowsBeforePublishing()
    {
        await using var terminals = TestTerminalService.Create();
        await using var otherService = TestTerminalService.Create();
        var service = CreateInteractionService(terminals);
        await using var terminal = CreateTerminal(otherService);

        await AssertTerminalRejectedAsync(service, terminal);
        AssertRegistered(otherService, terminal);
        Assert.False(terminals.TryGetTerminal(terminal.Id, out _));
    }

    [Fact]
    public async Task PromptTerminalAsync_DisposedTerminal_ThrowsBeforePublishing()
    {
        await using var terminals = TestTerminalService.Create();
        var service = CreateInteractionService(terminals);
        await using var terminal = CreateTerminal(terminals);
        await terminal.DisposeAsync();

        await AssertTerminalRejectedAsync(service, terminal);
        Assert.False(terminals.TryGetTerminal(terminal.Id, out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PromptTerminalAsync_UnregisteredInstance_ThrowsBeforePublishing(bool useRegisteredId)
    {
        await using var terminals = TestTerminalService.Create();
        var service = CreateInteractionService(terminals);
        await using var registeredTerminal = CreateTerminal(terminals);
        var backend = new TestTerminalBackend(useRegisteredId ? registeredTerminal.Id : "unregistered");
        await using var unregisteredTerminal = new AspireTerminal(backend);
        Assert.Equal(useRegisteredId, backend.Equals(registeredTerminal.Backend));
        Assert.NotEqual(registeredTerminal, unregisteredTerminal);

        await AssertTerminalRejectedAsync(service, unregisteredTerminal);
        Assert.False(backend.IsDisposed);
        AssertRegistered(terminals, registeredTerminal);
    }

    [Fact]
    public async Task PromptTerminalAsync_NoTerminalService_ThrowsBeforePublishing()
    {
        await using var terminals = TestTerminalService.Create();
        var service = CreateInteractionService(null);
        await using var terminal = CreateTerminal(terminals);

        await AssertTerminalRejectedAsync(service, terminal);
        AssertRegistered(terminals, terminal);
    }

    [Fact]
    public async Task PromptInputsAsync_TextInput_DoesNotRequireTerminalService()
    {
        var service = CreateInteractionService(null);
        var input = new InteractionInput { Name = "text", InputType = InputType.Text, Required = true, Value = "value" };
        var prompt = service.PromptInputsAsync("Title", "Message", [input]);
        var interaction = Assert.Single(service.GetCurrentInteractions());
        await CompleteInteractionAsync(service, interaction.InteractionId, new[] { input });

        var result = await prompt.DefaultTimeout();
        Assert.False(result.Canceled);
        Assert.Same(input, Assert.Single(result.Data));
        Assert.Empty(service.GetCurrentInteractions());
    }

    [Fact]
    public async Task PromptTerminalAsync_DisposedAfterPublishing_AttachmentStillRejectsIt()
    {
        await using var terminals = TestTerminalService.Create();
        var service = CreateInteractionService(terminals);
        await using var terminal = CreateTerminal(terminals);
        using var cts = new CancellationTokenSource();

        var prompt = service.PromptTerminalAsync("Message", terminal, cancellationToken: cts.Token);
        Assert.Single(service.GetCurrentInteractions());
        await terminal.DisposeAsync();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => terminals.AttachAsync(terminal.Id, Stream.Null, _ => Task.CompletedTask, CancellationToken.None)).DefaultTimeout();
        Assert.Equal($"There is no terminal with id '{terminal.Id}'.", ex.Message);
        Assert.False(prompt.IsCompleted);
        cts.Cancel();
        Assert.True((await prompt.DefaultTimeout()).Canceled);
        Assert.Empty(service.GetCurrentInteractions());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [InlineData(null)]
    public async Task PromptTerminalAsync_WithoutWork_CompletesAndCanBeReused(bool? completion)
    {
        await using var terminals = TestTerminalService.Create();
        var service = CreateInteractionService(terminals);
        // An invalid executable proves that raising/completing a prompt does not start the terminal.
        await using var terminal = CreateTerminal(terminals);
        for (var i = 0; i < 2; i++)
        {
            var options = new TerminalInteractionOptions { Title = "Title", PrimaryButtonText = "Cancel" };
            var prompt = service.PromptTerminalAsync("Message", terminal, options);
            var interaction = Assert.Single(service.GetCurrentInteractions());
            Assert.Equal(terminal.Id, Assert.IsType<Interaction.TerminalInteractionInfo>(interaction.InteractionInfo).TerminalId);
            Assert.Equal("Title", interaction.Title);
            Assert.Equal("Message", interaction.Message);
            Assert.Same(options, interaction.Options);
            Assert.False(prompt.IsCompleted);

            await CompleteInteractionAsync(service, interaction.InteractionId, completion);
            var result = await prompt.DefaultTimeout();
            Assert.Equal(completion != true, result.Canceled);
            Assert.Equal(completion == true, result.Data);
            Assert.Empty(service.GetCurrentInteractions());
            AssertRegistered(terminals, terminal);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PromptTerminalAsync_PreCanceled_DoesNotPublishOrRunWork(bool withWork)
    {
        await using var terminals = TestTerminalService.Create();
        var service = CreateInteractionService(terminals);
        await using var terminal = CreateTerminal(terminals);
        var workCalled = false;
        var options = withWork ? new TerminalInteractionOptions { Work = _ => { workCalled = true; return Task.CompletedTask; } } : null;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.PromptTerminalAsync("Message", terminal, options, new CancellationToken(canceled: true)));

        Assert.False(workCalled);
        Assert.Empty(service.GetCurrentInteractions());
        AssertRegistered(terminals, terminal);
    }

    [Fact]
    public async Task PromptTerminalAsync_WithoutWork_ExternalCancellation()
    {
        await using var terminals = TestTerminalService.Create();
        var service = CreateInteractionService(terminals);
        await using var terminal = CreateTerminal(terminals);
        using var cts = new CancellationTokenSource();
        var prompt = service.PromptTerminalAsync("Message", terminal, cancellationToken: cts.Token);
        var interaction = Assert.Single(service.GetCurrentInteractions());
        Assert.Equal(string.Empty, interaction.Title);
        Assert.Null(interaction.Options.PrimaryButtonText);
        Assert.False(prompt.IsCompleted);

        cts.Cancel();
        Assert.True((await prompt.DefaultTimeout()).Canceled);
        Assert.Empty(service.GetCurrentInteractions());
        AssertRegistered(terminals, terminal);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task PromptTerminalAsync_WorkCancellation_SignalsTokenAndJoinsWork(bool external, bool handleCancellation)
    {
        await using var terminals = TestTerminalService.Create();
        var service = CreateInteractionService(terminals);
        await using var terminal = CreateTerminal(terminals);
        using var cts = new CancellationTokenSource();
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishWork = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var prompt = service.PromptTerminalAsync("Message", terminal, new TerminalInteractionOptions
        {
            Work = async context =>
            {
                using var registration = context.CancellationToken.Register(() => canceled.TrySetResult());
                await finishWork.Task;
                if (!handleCancellation)
                {
                    context.CancellationToken.ThrowIfCancellationRequested();
                }
            }
        }, cts.Token);
        var interaction = Assert.Single(service.GetCurrentInteractions());
        if (external)
        {
            cts.Cancel();
        }
        else
        {
            await CompleteInteractionAsync(service, interaction.InteractionId, false);
        }

        try
        {
            await canceled.Task.DefaultTimeout();
            Assert.False(prompt.IsCompleted);
            Assert.Empty(service.GetCurrentInteractions());
            AssertRegistered(terminals, terminal);
        }
        finally
        {
            finishWork.TrySetResult();
        }
        Assert.True((await prompt.DefaultTimeout()).Canceled);
    }

    [Theory]
    [InlineData(false, "success")]
    [InlineData(false, "fault")]
    [InlineData(false, "cancel")]
    [InlineData(false, "handled-cancel")]
    [InlineData(true, "success")]
    [InlineData(true, "fault")]
    [InlineData(true, "cancel")]
    [InlineData(true, "handled-cancel")]
    public async Task PromptWorkAsync_CompletesExactlyOnce(bool progress, string outcome)
    {
        await using var terminals = TestTerminalService.Create();
        var service = CreateInteractionService(terminals);
        await using var terminal = CreateTerminal(terminals);
        using var cts = new CancellationTokenSource();
        await using var updates = service.SubscribeInteractionUpdates(cts.Token).GetAsyncEnumerator();
        var firstUpdate = updates.MoveNextAsync().AsTask();
        var finishWork = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failure = new InvalidOperationException("work failed");
        Task Work(CancellationToken _) => finishWork.Task;
        var prompt = progress
            ? service.PromptProgressAsync("Message", new ProgressInteractionOptions { Work = context => Work(context.CancellationToken) }, cts.Token)
            : service.PromptTerminalAsync("Message", terminal, new TerminalInteractionOptions { Work = context => Work(context.CancellationToken) }, cts.Token);
        Assert.True(await firstUpdate.DefaultTimeout());
        var firstId = updates.Current.InteractionId;
        Assert.Equal(Interaction.InteractionState.InProgress, updates.Current.State);

        if (outcome == "fault")
        {
            finishWork.SetException(failure);
            Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => prompt).DefaultTimeout());
        }
        else if (outcome == "success")
        {
            finishWork.SetResult();
            Assert.True((await prompt.DefaultTimeout()).Data);
        }
        else
        {
            await CompleteInteractionAsync(service, firstId, false);
            // Awaiting the signal avoids relying on when CompletionTcs's asynchronous continuation runs.
            var interaction = updates.Current;
            var cancellation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = interaction.CancellationToken.Register(() => cancellation.TrySetResult());
            await cancellation.Task.DefaultTimeout();
            if (outcome == "cancel")
            {
                finishWork.SetCanceled(interaction.CancellationToken);
            }
            else
            {
                finishWork.SetResult();
            }
            Assert.True((await prompt.DefaultTimeout()).Canceled);
        }
        Assert.True(await updates.MoveNextAsync().AsTask().DefaultTimeout());
        Assert.Equal(firstId, updates.Current.InteractionId);
        Assert.Equal(Interaction.InteractionState.Complete, updates.Current.State);
        Assert.Empty(service.GetCurrentInteractions());
        AssertRegistered(terminals, terminal);

        // Late client cancellation must not emit a second removal. A new prompt is a deterministic stream barrier.
        await CompleteInteractionAsync(service, firstId, false);
        var second = service.PromptTerminalAsync("Again", terminal, cancellationToken: cts.Token);
        Assert.True(await updates.MoveNextAsync().AsTask().DefaultTimeout());
        Assert.NotEqual(firstId, updates.Current.InteractionId);
        Assert.Equal(Interaction.InteractionState.InProgress, updates.Current.State);
        cts.Cancel();
        await second.DefaultTimeout();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PromptTerminalAsync_WorkThrowsUnrelatedCancellation_Propagates(bool progress)
    {
        await using var terminals = TestTerminalService.Create();
        var service = CreateInteractionService(terminals);
        await using var terminal = CreateTerminal(terminals);
        var failure = new OperationCanceledException("not the interaction token");
        var prompt = progress
            ? service.PromptProgressAsync("Message", new ProgressInteractionOptions { Work = _ => throw failure })
            : service.PromptTerminalAsync("Message", terminal, new TerminalInteractionOptions { Work = _ => throw failure });

        Assert.Same(failure, await Assert.ThrowsAsync<OperationCanceledException>(() => prompt));
        Assert.Empty(service.GetCurrentInteractions());
        AssertRegistered(terminals, terminal);
    }

    [Fact]
    public async Task PromptTerminalAsync_CompletionAndViewerDisconnect_LeaveAutomationUsable()
    {
        await using var terminals = TestTerminalService.Create();
        var service = CreateInteractionService(terminals);
        var output = new Pipe();
        await using var outputReader = output.Reader.AsStream();
        await using var outputWriter = output.Writer.AsStream();
        await using var terminal = terminals.CreateTerminal("Reusable", TerminalPlacement.Dialog,
            Hex1bTerminal.CreateBuilder().WithWorkload(new StreamWorkloadAdapter(outputReader, Stream.Null)));
        terminal.Start();

        foreach (var cancel in new[] { false, true })
        {
            var prompt = service.PromptTerminalAsync("Message", terminal);
            var interaction = Assert.Single(service.GetCurrentInteractions());
            await using (var viewer = await TestAppHostTerminalViewer.ConnectAsync(terminals, terminal.Id))
            {
                await outputWriter.WriteAsync("viewer-ready\r\n"u8.ToArray());
                await viewer.WaitForTextAsync("viewer-ready").DefaultTimeout();
            }
            Assert.False(prompt.IsCompleted);
            await CompleteInteractionAsync(service, interaction.InteractionId, !cancel);
            Assert.Equal(cancel, (await prompt.DefaultTimeout()).Canceled);

            var marker = cancel ? "after-cancel" : "after-complete";
            await outputWriter.WriteAsync(System.Text.Encoding.UTF8.GetBytes(marker + "\r\n"));
            await terminal.WaitForTextAsync(marker).DefaultTimeout();
            await terminal.SendTextAsync("still usable");
            AssertRegistered(terminals, terminal);
        }
    }

    [Fact]
    public async Task PromptTerminalAsync_WorkloadExit_DoesNotCompleteDialog()
    {
        await using var terminals = TestTerminalService.Create();
        var service = CreateInteractionService(terminals);
        var output = new Pipe();
        await using var outputReader = output.Reader.AsStream();
        await using var outputWriter = output.Writer.AsStream();
        var workload = new StreamWorkloadAdapter(outputReader, Stream.Null);
        await using var terminal = terminals.CreateTerminal("Ending", TerminalPlacement.Dialog,
            Hex1bTerminal.CreateBuilder().WithWorkload(workload));
        terminal.Start();
        var prompt = service.PromptTerminalAsync("Message", terminal);
        var interaction = Assert.Single(service.GetCurrentInteractions());
        await outputWriter.WriteAsync("ready\r\n"u8.ToArray());
        await terminal.WaitForTextAsync("ready").DefaultTimeout();
        // Stream-backed workloads signal exit separately from EOF. Observe startup before signaling exit.
        workload.SignalDisconnected();
        await Assert.IsType<Hex1bAspireTerminal>(terminal.Backend).WorkloadEnded.DefaultTimeout();

        Assert.False(prompt.IsCompleted);
        Assert.Same(interaction, Assert.Single(service.GetCurrentInteractions()));
        await CompleteInteractionAsync(service, interaction.InteractionId, false);
        Assert.True((await prompt.DefaultTimeout()).Canceled);
    }

    private static Task CompleteInteractionAsync(InteractionService service, int interactionId, object? state)
        => service.ProcessInteractionFromClientAsync(interactionId,
            (_, _, _) => new InteractionCompletionState { Complete = true, State = state }, CancellationToken.None);

    private static AspireTerminal CreateTerminal(TerminalService service, TerminalPlacement placement = TerminalPlacement.Dialog)
        => service.CreateTerminal(new TerminalLaunchOptions { Title = "Terminal", Executable = "must-not-be-started", Placement = placement });

    private static async Task AssertTerminalRejectedAsync(InteractionService service, AspireTerminal terminal)
    {
        using var cts = new CancellationTokenSource();
        try
        {
            var prompt = service.PromptTerminalAsync("Message", terminal, cancellationToken: cts.Token);
            Assert.Empty(service.GetCurrentInteractions());
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => prompt).DefaultTimeout();
            Assert.Equal("The terminal must be the instance registered with this AppHost's TerminalService.", ex.Message);
        }
        finally
        {
            cts.Cancel();
        }
    }

    private static void AssertRegistered(TerminalService service, AspireTerminal terminal)
    {
        Assert.True(service.TryGetTerminal(terminal.Id, out var registered));
        Assert.Same(terminal, registered);
    }

    private static InteractionService CreateInteractionService(TerminalService? terminals)
    {
        var services = new ServiceCollection();
        if (terminals is not null)
        {
            services.AddSingleton(terminals);
        }
        return new InteractionService(
            NullLogger<InteractionService>.Instance, new DistributedApplicationOptions(), services.BuildServiceProvider(),
            new ConfigurationBuilder().Build(), new TestInteractionFileUploadStore());
    }
}
