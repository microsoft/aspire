// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model.Interaction;

namespace Aspire.Dashboard.Model;

public sealed class ParameterInteractionState
{
    private Func<Task>? _beginInteraction;
    private bool _beginRequested;

    public InteractionsInputsDialogViewModel? Current { get; private set; }

    public event Action? Changed;

    public void SetBeginInteraction(Func<Task> beginInteraction)
    {
        _beginInteraction = beginInteraction;
        Changed?.Invoke();
    }

    public async Task BeginInteractionAsync()
    {
        if (_beginRequested || Current is not null || _beginInteraction is null)
        {
            return;
        }

        _beginRequested = true;
        await _beginInteraction().ConfigureAwait(false);
    }

    public void SetCurrent(InteractionsInputsDialogViewModel current)
    {
        Current = current;
        current.OnInteractionUpdated = () =>
        {
            Changed?.Invoke();
            return Task.CompletedTask;
        };
        Changed?.Invoke();
    }

    public void Clear(int interactionId)
    {
        if (Current?.Interaction.InteractionId != interactionId)
        {
            return;
        }

        Current = null;
        _beginInteraction = null;
        _beginRequested = false;
        Changed?.Invoke();
    }
}
