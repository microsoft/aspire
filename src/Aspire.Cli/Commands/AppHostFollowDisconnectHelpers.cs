// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Interaction;
using Aspire.Cli.Resources;
using StreamJsonRpc;

namespace Aspire.Cli.Commands;

/// <summary>
/// Classifies expected follow disconnects from the AppHost auxiliary backchannel.
/// </summary>
internal static class AppHostFollowDisconnectHelpers
{
    /// <summary>
    /// Determines whether an exception represents an expected disconnect.
    /// </summary>
    internal static bool IsExpectedDisconnect(Exception ex)
    {
        return ex is ConnectionLostException
            || ex is ObjectDisposedException
            || ex is OperationCanceledException { InnerException: ConnectionLostException };
    }

    /// <summary>
    /// Determines whether the AppHost process has exited.
    /// </summary>
    internal static bool HasAppHostExited(IAppHostAuxiliaryBackchannel connection)
    {
        if (connection.AppHostInfo?.ProcessId is not int pid)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            return process.HasExited;
        }
        catch (ArgumentException)
        {
            return true;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
        catch (NotSupportedException)
        {
            return true;
        }
    }

    /// <summary>
    /// Writes the follow termination message to stderr.
    /// </summary>
    internal static void WriteStatusMessage(IInteractionService interactionService, IAppHostAuxiliaryBackchannel connection)
    {
        interactionService.DisplayRawText(
            HasAppHostExited(connection)
                ? InteractionServiceStrings.AppHostShutDown
                : InteractionServiceStrings.AppHostConnectionLostGeneric,
            ConsoleOutput.Error);
    }
}
