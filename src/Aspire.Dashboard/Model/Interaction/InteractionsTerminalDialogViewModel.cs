// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Dashboard.Model.Interaction;

public sealed class InteractionsTerminalDialogViewModel
{
    public required string TerminalId { get; init; }
    public required string Message { get; set; }
    public Func<Task>? OnInteractionUpdated { get; set; }

    internal async Task UpdateMessageAsync(string message)
    {
        Message = message;
        if (OnInteractionUpdated is not null)
        {
            await OnInteractionUpdated().ConfigureAwait(false);
        }
    }
}
