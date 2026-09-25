// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Cli.Interaction;
using Aspire.Cli.Resources;
using Aspire.Hosting;
using Microsoft.Extensions.Configuration;

namespace Aspire.Cli.Commands;

internal static class AppHostStartupTimeout
{
    public static bool TryGetTimeoutSeconds(IConfiguration configuration, IInteractionService interactionService, out int timeoutSeconds)
    {
        timeoutSeconds = WaitCommand.DefaultTimeoutSeconds;

        var configuredTimeout = configuration[CliConfigNames.AppHostStartupTimeout];
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
            CliConfigNames.AppHostStartupTimeout));
        return false;
    }

    public static TimeSpan GetBackchannelConnectionTimeout(IConfiguration configuration)
    {
        var configuredValue = configuration[KnownConfigNames.CliBackchannelConnectTimeoutSeconds];
        if (double.TryParse(configuredValue, CultureInfo.InvariantCulture, out var seconds)
            && double.IsFinite(seconds)
            && seconds >= 0
            && seconds <= TimeSpan.MaxValue.TotalSeconds)
        {
            return TimeSpan.FromSeconds(seconds);
        }

        var timeout = TimeSpan.FromSeconds(WaitCommand.DefaultTimeoutSeconds);
        var configuredStartupTimeout = configuration[CliConfigNames.AppHostStartupTimeout];
        if (int.TryParse(configuredStartupTimeout, CultureInfo.InvariantCulture, out var startupTimeoutSeconds) && startupTimeoutSeconds > timeout.TotalSeconds)
        {
            timeout = TimeSpan.FromSeconds(startupTimeoutSeconds);
        }

        return timeout;
    }
}
