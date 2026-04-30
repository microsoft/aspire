// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Interaction;

namespace Aspire.Cli.Commands;

internal static class AppHostConnectionResultHandler
{
    public static int DisplayFailureAsError(AppHostConnectionResult result, IInteractionService interactionService, int fallbackExitCode)
    {
        var errorMessage = GetFailureMessage(result);
        interactionService.DisplayError(errorMessage);

        return result.IsProjectResolutionError
            ? result.ExitCode.Value
            : fallbackExitCode;
    }

    public static int DisplayFailureAsInformation(AppHostConnectionResult result, IInteractionService interactionService)
    {
        var errorMessage = GetFailureMessage(result);

        if (result.IsProjectResolutionError)
        {
            interactionService.DisplayError(errorMessage);
            return result.ExitCode.Value;
        }

        interactionService.DisplayMessage(KnownEmojis.Information, errorMessage);
        return ExitCodeConstants.Success;
    }

    private static string GetFailureMessage(AppHostConnectionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        Debug.Assert(!result.Success, "Cannot handle a successful AppHost connection result as a failure.");

        return result.ErrorMessage!;
    }
}
