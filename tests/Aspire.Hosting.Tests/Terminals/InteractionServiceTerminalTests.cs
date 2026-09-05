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
/// Guards how <see cref="InteractionService"/> validates terminal-typed inputs and, above all, that it keeps its
/// hands off the terminal's lifetime. The caller creates the terminal and the caller disposes it, so the dialog is
/// only ever a view onto a terminal that already exists.
/// </summary>
[Trait("Partition", "2")]
public class InteractionServiceTerminalTests
{
    [Fact]
    public async Task PromptInputsAsync_TerminalInputWithoutATerminal_Throws()
    {
        var (interactionService, _) = CreateInteractionService();

        var input = new InteractionInput { Name = "shell", InputType = InputType.Terminal };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => interactionService.PromptInputsAsync("Title", "Message", [input])).DefaultTimeout();

        Assert.Contains(nameof(InteractionInput.Terminal), ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PromptInputsAsync_TerminalOnTheDockSurface_Throws()
    {
        var (interactionService, terminalService) = CreateInteractionService();

        // A dock terminal is already presented as a dock tab, so showing it in a dialog as well would render one
        // terminal through two competing presentations.
        await using var dockTerminal = CreateTerminal(terminalService, TerminalSurface.Dock);
        var input = new InteractionInput
        {
            Name = "shell",
            InputType = InputType.Terminal,
            Terminal = dockTerminal
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => interactionService.PromptInputsAsync("Title", "Message", [input])).DefaultTimeout();

        Assert.Contains(nameof(TerminalSurface.Dock), ex.Message, StringComparison.Ordinal);

        // The rejected prompt must not take the caller's dock tab with it.
        Assert.True(terminalService.TryGetTerminal(dockTerminal.Id, out _));
    }

    [Fact]
    public async Task PromptInputsAsync_TerminalInput_CarriesTheCallersTerminalIdIntoTheDialog()
    {
        var (interactionService, terminalService) = CreateInteractionService();

        await using var terminal = CreateTerminal(terminalService, TerminalSurface.Interaction);
        var input = new InteractionInput
        {
            Name = "shell",
            Label = "Shell",
            InputType = InputType.Terminal,
            Terminal = terminal
        };

        var resultTask = interactionService.PromptInputsAsync("Title", "Message", [input]);

        // The dashboard addresses terminals by id, so the id of the caller's terminal is what the dialog has to
        // carry -- the interaction does not stand up a terminal of its own.
        Assert.Equal(terminal.Id, input.TerminalId);

        var interaction = Assert.Single(interactionService.GetCurrentInteractions());
        await CancelInteractionAsync(interactionService, interaction.InteractionId);

        await resultTask.DefaultTimeout();
    }

    [Fact]
    public async Task PromptInputsAsync_Cancelled_LeavesTheCallersTerminalAlone()
    {
        var (interactionService, terminalService) = CreateInteractionService();

        await using var terminal = CreateTerminal(terminalService, TerminalSurface.Interaction);
        var input = new InteractionInput
        {
            Name = "shell",
            InputType = InputType.Terminal,
            Terminal = terminal
        };

        var resultTask = interactionService.PromptInputsAsync("Title", "Message", [input]);

        var interaction = Assert.Single(interactionService.GetCurrentInteractions());
        await CancelInteractionAsync(interactionService, interaction.InteractionId);

        var result = await resultTask.DefaultTimeout();

        // The terminal outlives the dialog. A caller may show the same terminal in a second prompt, or keep
        // driving it through the automation API after the user dismisses this one, so a dismissed dialog must not
        // stop the workload.
        Assert.True(result.Canceled);
        Assert.True(terminalService.TryGetTerminal(terminal.Id, out _));
    }

    [Fact]
    public async Task PromptInputsAsync_CallerTokenCancelled_LeavesTheCallersTerminalAlone()
    {
        var (interactionService, terminalService) = CreateInteractionService();

        await using var terminal = CreateTerminal(terminalService, TerminalSurface.Interaction);
        var terminalInput = new InteractionInput
        {
            Name = "shell",
            InputType = InputType.Terminal,
            Terminal = terminal
        };

        using var cts = new CancellationTokenSource();
        var resultTask = interactionService.PromptInputsAsync("Title", "Message", [terminalInput], cancellationToken: cts.Token);

        // Cancelling the caller's token unwinds the prompt through OnInteractionCancellation rather than through a
        // dashboard-driven completion. Both routes end in CompleteInteractionCore, so this covers the second of the
        // two paths that used to tear the terminal down.
        cts.Cancel();

        var result = await resultTask.DefaultTimeout();

        Assert.True(result.Canceled);
        Assert.True(terminalService.TryGetTerminal(terminal.Id, out _));
    }

    [Fact]
    public async Task PromptInputsAsync_TerminalShownTwice_Succeeds()
    {
        var (interactionService, terminalService) = CreateInteractionService();

        // Caller-owned lifetime is what makes this legal: the terminal survives the first dialog, so the same
        // session can be surfaced again rather than the caller having to start a second workload.
        await using var terminal = CreateTerminal(terminalService, TerminalSurface.Interaction);

        for (var i = 0; i < 2; i++)
        {
            var input = new InteractionInput
            {
                Name = "shell",
                InputType = InputType.Terminal,
                Terminal = terminal
            };

            var resultTask = interactionService.PromptInputsAsync("Title", "Message", [input]);

            Assert.Equal(terminal.Id, input.TerminalId);

            var interaction = Assert.Single(interactionService.GetCurrentInteractions());
            await CancelInteractionAsync(interactionService, interaction.InteractionId);

            await resultTask.DefaultTimeout();
        }

        Assert.True(terminalService.TryGetTerminal(terminal.Id, out _));
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
    /// input marshalling.
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
            new TestInteractionFileUploadStore());

        return (interactionService, terminalService);
    }
}
