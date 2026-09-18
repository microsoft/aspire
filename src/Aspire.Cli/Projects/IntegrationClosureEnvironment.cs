// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Projects;

internal static class IntegrationClosureEnvironment
{
    public static void Apply(
        Action<string, string> setEnvironmentVariable,
        Action<string> removeEnvironmentVariable,
        string? probeManifestPath,
        string? integrationLibsPath,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(setEnvironmentVariable);
        ArgumentNullException.ThrowIfNull(removeEnvironmentVariable);

        SetOrRemove(
            KnownConfigNames.IntegrationLibsPath,
            integrationLibsPath,
            setEnvironmentVariable,
            removeEnvironmentVariable,
            logger);
        SetOrRemove(
            KnownConfigNames.IntegrationProbeManifestPath,
            probeManifestPath,
            setEnvironmentVariable,
            removeEnvironmentVariable,
            logger);
    }

    private static void SetOrRemove(
        string environmentVariable,
        string? value,
        Action<string, string> setEnvironmentVariable,
        Action<string> removeEnvironmentVariable,
        ILogger? logger)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            logger?.LogDebug("Setting {EnvironmentVariable} to {Path}", environmentVariable, value);
            setEnvironmentVariable(environmentVariable, value);
        }
        else
        {
            removeEnvironmentVariable(environmentVariable);
        }
    }
}
