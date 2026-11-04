// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;
using Aspire.Dashboard.Model.Interaction;
using Aspire.Dashboard.Tests.Shared;
using Aspire.DashboardService.Proto.V1;
using Microsoft.AspNetCore.InternalTesting;
using Xunit;

namespace Aspire.Dashboard.Tests.Model;

public sealed class ParameterInteractionStateTests
{
    [Fact]
    public void TrySetCurrent_NoPanel_NotShownInline()
    {
        var state = new ParameterInteractionState();

        Assert.False(state.TrySetCurrent(CreateInputsInteraction(1, "first")));
        Assert.Null(state.Current);
    }

    [Fact]
    public void TrySetCurrent_PanelShowsEveryInput_ShownInline()
    {
        var state = new ParameterInteractionState();
        using var panel = state.RegisterPanel();
        panel.SetParameterNames(["first", "second"]);
        var interaction = CreateInputsInteraction(1, "first", "second", ParameterInteractionState.SaveToUserSecretsInputName);

        Assert.True(state.TrySetCurrent(interaction));
        Assert.Same(interaction, state.Current);
        Assert.True(panel.Shows(interaction));
    }

    [Fact]
    public void TrySetCurrent_PanelShowsSomeInputs_NotShownInline()
    {
        var state = new ParameterInteractionState();
        using var panel = state.RegisterPanel();
        panel.SetParameterNames(["first"]);

        Assert.False(state.TrySetCurrent(CreateInputsInteraction(1, "first", "second")));
        Assert.Null(state.Current);
    }

    [Fact]
    public void TrySetCurrent_InteractionWithoutParameterInputs_NotShownInline()
    {
        var state = new ParameterInteractionState();
        using var panel = state.RegisterPanel();
        panel.SetParameterNames(["first"]);

        Assert.False(state.TrySetCurrent(CreateInputsInteraction(1, ParameterInteractionState.SaveToUserSecretsInputName)));
    }

    [Fact]
    public void PanelDisposed_CurrentReleased()
    {
        var state = new ParameterInteractionState();
        var panel = state.RegisterPanel();
        panel.SetParameterNames(["first"]);
        var interaction = CreateInputsInteraction(1, "first");
        Assert.True(state.TrySetCurrent(interaction));

        var released = new List<InteractionsInputsDialogViewModel>();
        state.Released += released.Add;
        panel.Dispose();

        Assert.Null(state.Current);
        Assert.Same(interaction, Assert.Single(released));
    }

    [Fact]
    public void PanelParametersNarrowed_CurrentReleased()
    {
        var state = new ParameterInteractionState();
        using var panel = state.RegisterPanel();
        panel.SetParameterNames(["first", "second"]);
        var interaction = CreateInputsInteraction(1, "first", "second");
        Assert.True(state.TrySetCurrent(interaction));

        var released = new List<InteractionsInputsDialogViewModel>();
        state.Released += released.Add;
        panel.SetParameterNames(["first"]);

        Assert.Null(state.Current);
        Assert.Same(interaction, Assert.Single(released));
    }

    [Fact]
    public void PanelDisposed_AnotherPanelShowsCurrent_NotReleased()
    {
        var state = new ParameterInteractionState();
        var panel = state.RegisterPanel();
        panel.SetParameterNames(["first"]);
        using var otherPanel = state.RegisterPanel();
        otherPanel.SetParameterNames(["first"]);
        var interaction = CreateInputsInteraction(1, "first");
        Assert.True(state.TrySetCurrent(interaction));

        var released = new List<InteractionsInputsDialogViewModel>();
        state.Released += released.Add;
        panel.Dispose();

        Assert.Same(interaction, state.Current);
        Assert.Empty(released);
    }

    [Fact]
    public void Clear_CurrentCompleted_NotReleased()
    {
        var state = new ParameterInteractionState();
        var panel = state.RegisterPanel();
        panel.SetParameterNames(["first"]);
        Assert.True(state.TrySetCurrent(CreateInputsInteraction(1, "first")));

        var released = new List<InteractionsInputsDialogViewModel>();
        state.Released += released.Add;
        state.Clear(1);
        panel.Dispose();

        Assert.Null(state.Current);
        Assert.Empty(released);
    }

    [Fact]
    public void SetDialogInteraction_PanelMountedLater_TakesInteractionFromDialog()
    {
        var state = new ParameterInteractionState();
        var interaction = CreateInputsInteraction(1, "first", ParameterInteractionState.SaveToUserSecretsInputName);
        var closeCount = 0;
        state.SetDialogInteraction(interaction, () =>
        {
            closeCount++;
            return Task.CompletedTask;
        });
        Assert.Null(state.Current);

        // After a reload on the form, the prompt is replayed before the form is rendered.
        using var panel = state.RegisterPanel();
        panel.SetParameterNames(["first"]);

        Assert.Same(interaction, state.Current);
        Assert.Equal(1, closeCount);
    }

    [Fact]
    public void SetDialogInteraction_PanelShowsSomeInputs_KeepsDialog()
    {
        var state = new ParameterInteractionState();
        var closeCount = 0;
        state.SetDialogInteraction(CreateInputsInteraction(1, "first", "second"), () =>
        {
            closeCount++;
            return Task.CompletedTask;
        });

        using var panel = state.RegisterPanel();
        panel.SetParameterNames(["first"]);

        Assert.Null(state.Current);
        Assert.Equal(0, closeCount);
    }

    [Fact]
    public void ClearDialogInteraction_DialogClosed_PanelDoesNotTakeInteraction()
    {
        var state = new ParameterInteractionState();
        var closeCount = 0;
        state.SetDialogInteraction(CreateInputsInteraction(1, "first"), () =>
        {
            closeCount++;
            return Task.CompletedTask;
        });
        state.ClearDialogInteraction(1);

        using var panel = state.RegisterPanel();
        panel.SetParameterNames(["first"]);

        Assert.Null(state.Current);
        Assert.Equal(0, closeCount);
    }

    [Fact]
    public async Task BeginInteractionAsync_PanelShowsEveryParameter_AcceptsNotificationOnce()
    {
        var state = new ParameterInteractionState();
        var acceptCount = 0;
        state.SetNotification(1, ["first", "second"], () =>
        {
            acceptCount++;
            return Task.CompletedTask;
        });
        using var panel = state.RegisterPanel();
        panel.SetParameterNames(["first", "second"]);

        await state.BeginInteractionAsync(panel).DefaultTimeout();
        await state.BeginInteractionAsync(panel).DefaultTimeout();

        Assert.Equal(1, acceptCount);
    }

    [Fact]
    public async Task BeginInteractionAsync_PanelShowsSomeParameters_DoesNotAcceptNotification()
    {
        var state = new ParameterInteractionState();
        var acceptCount = 0;
        state.SetNotification(1, ["first", "second"], () =>
        {
            acceptCount++;
            return Task.CompletedTask;
        });
        using var panel = state.RegisterPanel();
        panel.SetParameterNames(["first"]);

        await state.BeginInteractionAsync(panel).DefaultTimeout();

        Assert.Equal(0, acceptCount);
    }

    [Fact]
    public async Task BeginInteractionAsync_NotificationCompleted_DoesNotAcceptNotification()
    {
        var state = new ParameterInteractionState();
        var acceptCount = 0;
        state.SetNotification(1, ["first"], () =>
        {
            acceptCount++;
            return Task.CompletedTask;
        });

        // The user dismissed the notification before opening the form.
        state.Clear(1);

        using var panel = state.RegisterPanel();
        panel.SetParameterNames(["first"]);
        await state.BeginInteractionAsync(panel).DefaultTimeout();

        Assert.Equal(0, acceptCount);
    }

    [Fact]
    public async Task BeginInteractionAsync_NextNotificationAfterCompletion_AcceptsNextNotification()
    {
        var state = new ParameterInteractionState();
        var acceptedNotifications = new List<int>();
        using var panel = state.RegisterPanel();
        panel.SetParameterNames(["first"]);

        state.SetNotification(1, ["first"], () =>
        {
            acceptedNotifications.Add(1);
            return Task.CompletedTask;
        });
        await state.BeginInteractionAsync(panel).DefaultTimeout();
        state.Clear(1);

        // The AppHost sends a new notification when a parameter is still missing after the previous prompt.
        state.SetNotification(2, ["first"], () =>
        {
            acceptedNotifications.Add(2);
            return Task.CompletedTask;
        });
        await state.BeginInteractionAsync(panel).DefaultTimeout();

        Assert.Equal([1, 2], acceptedNotifications);
    }

    private static InteractionsInputsDialogViewModel CreateInputsInteraction(int interactionId, params string[] inputNames)
    {
        var inputsDialog = new InteractionInputsDialog();
        inputsDialog.InputItems.AddRange(inputNames.Select(name => new InteractionInput { Name = name, InputType = InputType.Text }));

        return new InteractionsInputsDialogViewModel
        {
            Interaction = new WatchInteractionsResponseUpdate { InteractionId = interactionId, InputsDialog = inputsDialog },
            OnSubmitCallback = (_, _) => Task.CompletedTask,
            Message = string.Empty,
            DashboardClient = new TestDashboardClient()
        };
    }
}
