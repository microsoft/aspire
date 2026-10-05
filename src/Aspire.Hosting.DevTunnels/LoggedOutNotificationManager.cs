// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Utils;

namespace Aspire.Hosting.DevTunnels;

internal sealed class LoggedOutNotificationManager(IInteractionService interactionService) : CoalescingAsyncOperation
{
    public Task NotifyUserLoggedOutAsync(CancellationToken cancellationToken = default) => RunAsync(cancellationToken);

    protected override async Task ExecuteCoreAsync(CancellationToken cancellationToken)
    {
        if (interactionService.IsAvailable)
        {
            _ = await interactionService.PromptNotificationAsync(
                "Dev tunnels",
                Resources.MessageStrings.AuthenticationExpiredNotification,
                new() { Intent = MessageIntent.Warning },
                cancellationToken).ConfigureAwait(false);
        }
    }
}
