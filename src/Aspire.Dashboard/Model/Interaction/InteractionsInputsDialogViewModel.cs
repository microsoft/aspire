// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.DashboardService.Proto.V1;

namespace Aspire.Dashboard.Model.Interaction;

public sealed class InteractionsInputsDialogViewModel
{
    private WatchInteractionsResponseUpdate _interaction = default!;

    public required WatchInteractionsResponseUpdate Interaction
    {
        get => _interaction;
        init => _interaction = value;
    }
    public required Func<WatchInteractionsResponseUpdate, bool, Task> OnSubmitCallback { get; init; }
    public required string Message { get; init; }
    public required IDashboardClient DashboardClient { get; init; }

    public List<InteractionInput> Inputs => Interaction.InputsDialog!.InputItems.ToList();

    public Func<Task>? OnInteractionUpdated { get; set; }

    internal async Task UpdateInteractionAsync(WatchInteractionsResponseUpdate item)
    {
        _interaction = item;
        if (OnInteractionUpdated is not null)
        {
            await OnInteractionUpdated().ConfigureAwait(false);
        }
    }
}
