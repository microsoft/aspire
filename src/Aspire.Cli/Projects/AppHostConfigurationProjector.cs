// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Configuration;
using Aspire.Hosting;

namespace Aspire.Cli.Projects;

/// <summary>
/// Projects Aspire CLI configuration into the environment of a launched AppHost.
/// </summary>
internal sealed class AppHostConfigurationProjector(IConfigurationService configurationService, IEnvironment environment)
{
    private static readonly EnvironmentVariableProjection[] s_environmentVariableProjections =
    [
        new(AspireConfigContainerTunnel.BaseImageConfigPath, KnownConfigNames.ContainerTunnelBaseImage)
    ];

    /// <summary>
    /// Applies configured AppHost environment variables without replacing explicit launch or ambient values.
    /// </summary>
    public async Task ApplyEnvironmentVariablesAsync(
        IDictionary<string, string> environmentVariables,
        DirectoryInfo appHostDirectory,
        CancellationToken cancellationToken = default)
    {
        foreach (var projection in s_environmentVariableProjections)
        {
            if (environmentVariables.Keys.Contains(projection.EnvironmentVariableName, StringComparer.OrdinalIgnoreCase)
                || !string.IsNullOrEmpty(environment.GetEnvironmentVariable(projection.EnvironmentVariableName)))
            {
                continue;
            }

            var value = await configurationService.GetConfigurationFromDirectoryAsync(
                projection.ConfigurationPath,
                appHostDirectory,
                cancellationToken: cancellationToken);

            if (!string.IsNullOrEmpty(value))
            {
                environmentVariables[projection.EnvironmentVariableName] = value;
            }
        }
    }

    private readonly record struct EnvironmentVariableProjection(
        string ConfigurationPath,
        string EnvironmentVariableName);
}
