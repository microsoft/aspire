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
    /// <param name="environmentVariables">The launch environment to update.</param>
    /// <param name="appHostFile">The AppHost entry point, such as <c>apphost.cs</c>, <c>Shop.AppHost.csproj</c> or <c>apphost.ts</c>.</param>
    /// <param name="appHostDirectory">The directory to resolve layered CLI configuration from.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    public async Task ApplyEnvironmentVariablesAsync(
        IDictionary<string, string> environmentVariables,
        FileInfo appHostFile,
        DirectoryInfo appHostDirectory,
        CancellationToken cancellationToken = default)
    {
        // appHost.name is per AppHost rather than a layered setting, so it is resolved with the
        // same rule the tray and `aspire ps` use instead of the configuration service's merge.
        if (!IsSet(environmentVariables, KnownConfigNames.AppHostName)
            && AppHostDisplayNameResolver.GetConfiguredName(appHostFile.FullName) is { } name)
        {
            environmentVariables[KnownConfigNames.AppHostName] = name;
        }

        foreach (var projection in s_environmentVariableProjections)
        {
            if (IsSet(environmentVariables, projection.EnvironmentVariableName))
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

    private bool IsSet(IDictionary<string, string> environmentVariables, string name)
        => environmentVariables.Keys.Contains(name, StringComparer.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(environment.GetEnvironmentVariable(name));

    private readonly record struct EnvironmentVariableProjection(
        string ConfigurationPath,
        string EnvironmentVariableName);
}
