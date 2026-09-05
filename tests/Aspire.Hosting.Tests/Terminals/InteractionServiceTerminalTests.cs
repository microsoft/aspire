// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Terminals;
using Aspire.Hosting.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

#pragma warning disable ASPIREINTERACTION001 // Type is for evaluation purposes only and is subject to change or removal in future updates.
#pragma warning disable ASPIRETERMINAL002 // Test consumer of the experimental AppHost terminal API.

namespace Aspire.Hosting.Tests.Terminals;

/// <summary>
/// Guards how <see cref="InteractionService"/> validates and owns terminal-typed inputs. The interaction owns
/// teardown for every terminal it shows, so the tests here are as much about the terminal not outliving the
/// dialog as they are about the validation messages.
/// </summary>
[Trait("Partition", "2")]
public class InteractionServiceTerminalTests
{
    [Fact]
    public async Task PromptInputsAsync_TerminalInputWithNeitherCommandNorSession_Throws()
    {
        var (interactionService, _) = CreateInteractionService();

        var input = new InteractionInput { Name = "shell", InputType = InputType.Terminal };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => interactionService.PromptInputsAsync("Title", "Message", [input])).DefaultTimeout();

        Assert.Contains("exactly one of", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PromptInputsAsync_TerminalInputWithBothCommandAndSession_Throws()
    {
        var (interactionService, terminalService) = CreateInteractionService();

        var session = CreateTerminal(terminalService, TerminalSurface.Interaction);
        var input = new InteractionInput
        {
            Name = "shell",
            InputType = InputType.Terminal,
            Terminal = new TerminalCommand("bash"),
            TerminalSession = session
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => interactionService.PromptInputsAsync("Title", "Message", [input])).DefaultTimeout();

        Assert.Contains("exactly one of", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PromptInputsAsync_TerminalSessionOnTheDockSurface_Throws()
    {
        var (interactionService, terminalService) = CreateInteractionService();

        // A dock terminal is listed as a tab and outlives whatever created it. The dialog disposes the terminal it
        // shows, so accepting one here would rip a tab out from under the dock when the dialog closed.
        var dockTerminal = CreateTerminal(terminalService, TerminalSurface.Dock);
        var input = new InteractionInput
        {
            Name = "shell",
            InputType = InputType.Terminal,
            TerminalSession = dockTerminal
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => interactionService.PromptInputsAsync("Title", "Message", [input])).DefaultTimeout();

        Assert.Contains(nameof(TerminalSurface.Dock), ex.Message, StringComparison.Ordinal);

        // The dock tab must survive the rejected prompt.
        Assert.True(terminalService.TryGetTerminal(dockTerminal.Id, out _));
    }

    [Fact]
    public async Task PromptInputsAsync_TerminalInputWithCommand_CreatesTerminalBeforeTheDialogIsShown()
    {
        var (interactionService, terminalService) = CreateInteractionService();

        var input = new InteractionInput
        {
            Name = "shell",
            Label = "Shell",
            InputType = InputType.Terminal,
            Terminal = new TerminalCommand("bash")
        };

        var resultTask = interactionService.PromptInputsAsync("Title", "Message", [input]);

        // The dialog carries a terminal id, so the terminal has to exist by the time the interaction is published.
        Assert.NotNull(input.TerminalId);
        Assert.True(terminalService.TryGetTerminal(input.TerminalId, out var terminal));
        Assert.Equal("Shell", terminal.Title);
        Assert.Equal(TerminalSurface.Interaction, terminal.Surface);

        var interaction = Assert.Single(interactionService.GetCurrentInteractions());
        await CancelInteractionAsync(interactionService, interaction.InteractionId);

        await resultTask.DefaultTimeout();
    }

    [Fact]
    public async Task PromptInputsAsync_Cancelled_DisposesTheTerminalItCreated()
    {
        var (interactionService, terminalService) = CreateInteractionService();

        var input = new InteractionInput
        {
            Name = "shell",
            InputType = InputType.Terminal,
            Terminal = new TerminalCommand("bash")
        };

        var resultTask = interactionService.PromptInputsAsync("Title", "Message", [input]);
        var terminalId = input.TerminalId;
        Assert.NotNull(terminalId);

        var interaction = Assert.Single(interactionService.GetCurrentInteractions());
        await CancelInteractionAsync(interactionService, interaction.InteractionId);

        var result = await resultTask.DefaultTimeout();

        // Unlike an uploaded file, nothing about a terminal survives the dialog for the caller to consume, so a
        // dismissed dialog must still stop the workload rather than leaving it registered for the AppHost's life.
        Assert.True(result.Canceled);
        Assert.False(terminalService.TryGetTerminal(terminalId, out _));
        Assert.Null(input.TerminalId);
    }

    [Fact]
    public async Task PromptInputsAsync_CallerTokenCancelled_DisposesTheTerminalItCreated()
    {
        var (interactionService, terminalService) = CreateInteractionService();

        var terminalInput = new InteractionInput
        {
            Name = "shell",
            InputType = InputType.Terminal,
            Terminal = new TerminalCommand("bash")
        };

        using var cts = new CancellationTokenSource();
        var resultTask = interactionService.PromptInputsAsync("Title", "Message", [terminalInput], cancellationToken: cts.Token);

        var terminalId = terminalInput.TerminalId;
        Assert.NotNull(terminalId);

        // Cancelling the caller's token unwinds the prompt through OnInteractionCancellation rather than through a
        // dashboard-driven completion. Both routes end in CompleteInteractionCore, and the finally in
        // PromptInputsAsync then runs over inputs whose TerminalId has already been cleared -- so this also covers
        // the backstop being idempotent rather than tearing a terminal down twice.
        cts.Cancel();

        var result = await resultTask.DefaultTimeout();

        Assert.True(result.Canceled);
        Assert.False(terminalService.TryGetTerminal(terminalId, out _));
        Assert.Null(terminalInput.TerminalId);
    }

    /// <summary>
    /// Dismisses the dialog the way the dashboard does when the user closes it without submitting.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>Complete = true</c> with a null <c>State</c> is the dismiss signal, not <c>Complete = false</c>:
    /// "not complete" means the dialog stays open, which is how a validation failure is reported.
    /// <c>PromptInputsAsync</c> maps a completion whose state is not an input list onto a cancelled result.
    /// </para>
    /// <para>
    /// The callback returns the state directly instead of routing through
    /// <c>DashboardServiceData.ProcessInputs</c>. These tests are about the terminal's lifetime rather than
    /// input marshalling, and the terminal teardown they assert on happens in <c>CompleteInteractionCore</c>
    /// regardless of how the input values were produced.
    /// </para>
    /// </remarks>
    private static Task CancelInteractionAsync(InteractionService interactionService, int interactionId)
        => interactionService.ProcessInteractionFromClientAsync(
            interactionId,
            (_, _, _) => new InteractionCompletionState { Complete = true },
            CancellationToken.None);

    private static IAspireTerminal CreateTerminal(TerminalService service, TerminalSurface surface)
        => service.CreateTerminal(new TerminalLaunchOptions
        {
            Title = "Terminal",
            Command = new TerminalCommand("bash"),
            Surface = surface
        });

    private static (InteractionService InteractionService, TerminalService TerminalService) CreateInteractionService()
    {
        var terminalService = TestTerminalService.Create();
        var interactionService = new InteractionService(
            NullLogger<InteractionService>.Instance,
            new DistributedApplicationOptions(),
            new ServiceCollection().BuildServiceProvider(),
            new ConfigurationBuilder().Build(),
            new TestInteractionFileUploadStore(),
            terminalService);

        return (interactionService, terminalService);
    }
}
