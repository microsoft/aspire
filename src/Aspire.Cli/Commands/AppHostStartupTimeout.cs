// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Cli.Interaction;
using Aspire.Cli.Resources;
using Microsoft.Extensions.Configuration;

namespace Aspire.Cli.Commands;

internal static class AppHostStartupTimeout
{
    public static bool TryGetTimeoutSeconds(IConfiguration configuration, IInteractionService interactionService, out int timeoutSeconds)
    {
        timeoutSeconds = WaitCommand.DefaultTimeoutSeconds;

        var configuredTimeout = configuration[CliConfigNames.AppHostStartupTimeoutSeconds];
        if (string.IsNullOrWhiteSpace(configuredTimeout))
        {
            return true;
        }

        if (int.TryParse(configuredTimeout, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedTimeout) &&
            parsedTimeout > 0)
        {
            timeoutSeconds = parsedTimeout;
            return true;
        }

        interactionService.DisplayError(string.Format(
            CultureInfo.CurrentCulture,
            RunCommandStrings.InvalidAppHostStartupTimeoutEnvironmentVariable,
            CliConfigNames.AppHostStartupTimeoutSeconds));
        return false;
    }
}
