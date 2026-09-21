// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Threading.Channels;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Model.Interaction;
using Aspire.Dashboard.Telemetry;
using Aspire.Dashboard.Tests.Shared;
using Aspire.DashboardService.Proto.V1;
using Aspire.Tests.Shared;
using Bunit;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.FluentUI.AspNetCore.Components;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Interactions;

public partial class InteractionsProviderTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("Cancel work", false)]
    [InlineData("Cancel work", true)]
    public async Task ReceiveData_TerminalDialog_UsesDedicatedPresentationAndCancelResponse(string? buttonText, bool dismiss)
    {
        var updates = Channel.CreateUnbounded<WatchInteractionsResponseUpdate>();
        var requests = Channel.CreateUnbounded<WatchInteractionsRequestUpdate>();
        var shown = Channel.CreateUnbounded<(object? Content, DialogParameters Parameters)>();
        var client = new TestDashboardClient(isEnabled: true, interactionChannelProvider: () => updates,
            sendInteractionUpdateChannel: requests);
        var dialogs = new TestDialogService((content, parameters) =>
        {
            shown.Writer.TryWrite((content, parameters));
            return Task.CompletedTask;
        });
        SetupInteractionProviderServices(client, dialogs);
        var cut = RenderComponent<Components.Interactions.InteractionsProvider>();
        await updates.Writer.WriteAsync(new WatchInteractionsResponseUpdate
        {
            InteractionId = 1,
            Title = "Shell",
            Message = "<message>",
            PrimaryButtonText = buttonText ?? string.Empty,
            SecondaryButtonText = "Not used",
            ShowSecondaryButton = true,
            ShowDismiss = true,
            PromptTerminal = new InteractionPromptTerminal { TerminalId = "terminal #1/?%+" }
        });
        var (content, parameters) = await shown.Reader.ReadAsync().AsTask().DefaultTimeout();
        var vm = Assert.IsType<InteractionsTerminalDialogViewModel>(content);
        Assert.Equal("terminal #1/?%+", vm.TerminalId);
        Assert.Equal("&lt;message&gt;", vm.Message);
        Assert.Equal("Shell", parameters.Title);
        Assert.Equal("interactions-terminal-dialog", parameters.Id);
        Assert.Equal("75vw", parameters.Width);
        Assert.False(parameters.ShowDismiss);
        Assert.Null(parameters.SecondaryAction);
        Assert.Equal(string.IsNullOrEmpty(buttonText) ? null : buttonText, parameters.PrimaryAction);
        Assert.True(parameters.UseCustomFooter);
        Assert.True(parameters.PreventDismissOnOverlayClick);
        await AsyncTestHelpers.AssertIsTrueRetryAsync(() => cut.Instance._interactionDialogReference?.InteractionId == 1, "Terminal dialog opened.");
        Assert.Equal(TelemetryComponentIds.InteractionTerminalDialog,
            cut.Instance._interactionDialogReference!.TelemetryContext.Properties[TelemetryPropertyKeys.DashboardComponentId].Value);

        await cut.InvokeAsync(() => cut.Instance._interactionDialogReference!.Dialog.CloseAsync(
            dismiss ? DialogResult.Cancel() : DialogResult.Ok("cancel"))).DefaultTimeout();
        var request = await requests.Reader.ReadAsync().AsTask().DefaultTimeout();
        Assert.Equal(1, request.InteractionId);
        Assert.Equal(dismiss ? WatchInteractionsRequestUpdate.KindOneofCase.Complete : WatchInteractionsRequestUpdate.KindOneofCase.PromptTerminal,
            request.KindCase);
        if (!dismiss)
        {
            Assert.Equal(vm.TerminalId, request.PromptTerminal.TerminalId);
            Assert.True(request.PromptTerminal.HasResult);
            Assert.False(request.PromptTerminal.Result);
        }
        await AsyncTestHelpers.AssertIsTrueRetryAsync(() => cut.Instance._interactionDialogReference is null, "Canceled dialog removed.");
        Assert.False(requests.Reader.TryRead(out _));
        await cut.Instance.DisposeAsync().DefaultTimeout();
    }

    [Fact]
    public async Task ReceiveData_TerminalDialogs_QueueUpdateReplayAndRemoveWithoutEchoingCompletion()
    {
        var updates = Channel.CreateUnbounded<WatchInteractionsResponseUpdate>();
        var requests = Channel.CreateUnbounded<WatchInteractionsRequestUpdate>();
        var shown = Channel.CreateUnbounded<(object? Content, DialogParameters Parameters)>();
        var client = new TestDashboardClient(isEnabled: true, interactionChannelProvider: () => updates,
            sendInteractionUpdateChannel: requests);
        var dialogs = new TestDialogService((content, parameters) =>
        {
            shown.Writer.TryWrite((content, parameters));
            return Task.CompletedTask;
        });
        SetupInteractionProviderServices(client, dialogs);
        var cut = RenderComponent<Components.Interactions.InteractionsProvider>();
        await updates.Writer.WriteAsync(new WatchInteractionsResponseUpdate
        {
            InteractionId = 1, PromptProgress = new InteractionPromptProgress()
        });
        Assert.IsType<InteractionsProgressDialogViewModel>((await shown.Reader.ReadAsync().AsTask().DefaultTimeout()).Content);
        await AsyncTestHelpers.AssertIsTrueRetryAsync(() => cut.Instance._interactionDialogReference?.InteractionId == 1, "Progress dialog opened.");

        var terminal = new WatchInteractionsResponseUpdate
        {
            InteractionId = 2, Message = "Queued", PromptTerminal = new InteractionPromptTerminal { TerminalId = "terminal" }
        };
        await updates.Writer.WriteAsync(terminal);
        await updates.Writer.WriteAsync(new WatchInteractionsResponseUpdate
        {
            InteractionId = 3, PromptTerminal = new InteractionPromptTerminal { TerminalId = "never-opened" }
        });
        await updates.Writer.WriteAsync(new WatchInteractionsResponseUpdate
        {
            InteractionId = 4,
            InputsDialog = new InteractionInputsDialog
            {
                InputItems = { new InteractionInput { Name = "normal", InputType = InputType.Text, Required = true } }
            }
        });
        var queuedUpdate = terminal.Clone();
        queuedUpdate.Message = "Updated while queued";
        await updates.Writer.WriteAsync(queuedUpdate);
        await updates.Writer.WriteAsync(new WatchInteractionsResponseUpdate { InteractionId = 3, Complete = new InteractionComplete() });
        await AsyncTestHelpers.AssertIsTrueRetryAsync(async () => await cut.Instance.GetMessagesProcessedAsync() == 6, "Queued updates processed.");
        Assert.False(shown.Reader.TryRead(out _));

        await updates.Writer.WriteAsync(new WatchInteractionsResponseUpdate { InteractionId = 1, Complete = new InteractionComplete() });
        var (content, _) = await shown.Reader.ReadAsync().AsTask().DefaultTimeout();
        var terminalVM = Assert.IsType<InteractionsTerminalDialogViewModel>(content);
        Assert.Equal("Updated while queued", terminalVM.Message);
        await AsyncTestHelpers.AssertIsTrueRetryAsync(() => cut.Instance._interactionDialogReference?.InteractionId == 2, "Queued terminal dialog opened.");
        var reference = cut.Instance._interactionDialogReference;

        var replay = terminal.Clone();
        replay.EnableMessageMarkdown = true;
        replay.Message = "**Replayed**";
        await updates.Writer.WriteAsync(replay);
        await AsyncTestHelpers.AssertIsTrueRetryAsync(async () => await cut.Instance.GetMessagesProcessedAsync() == 8, "Replay processed.");
        Assert.Same(reference, cut.Instance._interactionDialogReference);
        Assert.Contains("<strong>Replayed</strong>", terminalVM.Message, StringComparison.Ordinal);
        Assert.False(shown.Reader.TryRead(out _));

        await updates.Writer.WriteAsync(new WatchInteractionsResponseUpdate { InteractionId = 2, Complete = new InteractionComplete() });
        var (inputContent, inputParameters) = await shown.Reader.ReadAsync().AsTask().DefaultTimeout();
        var inputsVM = Assert.IsType<InteractionsInputsDialogViewModel>(inputContent);
        Assert.Equal("normal", Assert.Single(inputsVM.Inputs).Name);
        Assert.Equal("min(650px, 75vw)", inputParameters.Width);
        await AsyncTestHelpers.AssertIsTrueRetryAsync(() => cut.Instance._interactionDialogReference?.InteractionId == 4, "Ordinary inputs dialog opened.");
        Assert.False(requests.Reader.TryRead(out _));
        Assert.False(shown.Reader.TryRead(out _));

        await cut.Instance.DisposeAsync().DefaultTimeout();
    }
}
