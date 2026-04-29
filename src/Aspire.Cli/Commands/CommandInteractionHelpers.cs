// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Cli.Interaction;
using Aspire.Cli.Resources;

namespace Aspire.Cli.Commands;

internal static class CommandInteractionHelpers
{
    internal static string FormatMessageWithOptionalLogFile(string messageWithLogFile, string messageWithoutLogFile, string? logFilePath)
    {
        return logFilePath is null
            ? messageWithoutLogFile
            : string.Format(CultureInfo.CurrentCulture, messageWithLogFile, logFilePath);
    }

    internal static string FormatMessageWithOptionalLogFile(string messageWithLogFile, string messageWithoutLogFile, string? logFilePath, params object?[] args)
    {
        if (logFilePath is null)
        {
            return string.Format(CultureInfo.CurrentCulture, messageWithoutLogFile, args);
        }

        var argsWithLogFile = new object?[args.Length + 1];
        args.CopyTo(argsWithLogFile, 0);
        argsWithLogFile[^1] = logFilePath;

        return string.Format(CultureInfo.CurrentCulture, messageWithLogFile, argsWithLogFile);
    }

    internal static void DisplaySeeLogsMessage(IInteractionService interactionService, string? logFilePath)
    {
        if (logFilePath is not null)
        {
            interactionService.DisplayMessage(KnownEmojis.PageFacingUp, string.Format(CultureInfo.CurrentCulture, InteractionServiceStrings.SeeLogsAt, logFilePath));
        }
    }
}
