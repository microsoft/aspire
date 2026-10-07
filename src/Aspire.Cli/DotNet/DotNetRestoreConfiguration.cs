// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text.Json;
using Aspire.Cli.Projects;
using Aspire.Cli.Resources;

namespace Aspire.Cli.DotNet;

/// <summary>
/// Inspects the current C# AppHost's evaluated MSBuild restore policy without executing targets.
/// </summary>
internal static class DotNetRestoreConfiguration
{
    private static readonly string[] s_configurationProperties =
    [
        "RestoreConfigFile",
        "RestoreRootConfigDirectory",
        "RestoreSources",
        "RestoreAdditionalProjectSources",
        "_RestoreSourcesOverride"
    ];

    internal static async Task<DotNetRestoreSettings> ReadAsync(
        IDotNetCliRunner runner,
        FileInfo projectFile,
        CancellationToken cancellationToken)
    {
        var (exitCode, output) = await runner.GetProjectItemsAndPropertiesAsync(
            projectFile,
            items: [],
            properties:
            [
                .. s_configurationProperties,
                "MSBuildProjectFullPath",
                "MSBuildToolsPath",
                "NuGetRestoreTargets"
            ],
            // Restore targets collect package references and can run legacy SDK validation
            // before the updater has repaired the project. Inspect policy through evaluation only.
            targets: [],
            new ProcessInvocationOptions
            {
                // File-based build otherwise performs implicit restore before property evaluation.
                // A global property prevents project imports from overriding NuGet's
                // restore-graph evaluation mode and loading stale generated package imports.
                NoRestore = true,
                ExcludeRestorePackageImports = true,
                SuppressLogging = true
            },
            cancellationToken);
        if (exitCode != 0 || output is null)
        {
            throw new ProjectUpdaterException(
                string.Format(CultureInfo.InvariantCulture, UpdateCommandStrings.FailedFetchItemsAndPropertiesFormat, projectFile.FullName));
        }

        using (output)
        {
            // MSBuild emits {"Properties":{"MSBuildProjectFullPath":".../apphost.cs.csproj"}}.
            // Keep the synthetic identity for file-based apps; later MSBuild restore targets use it.
            var properties = output.RootElement.GetProperty("Properties");
            var overrides = s_configurationProperties
                .Where(name => !string.IsNullOrEmpty(GetProperty(properties, name)))
                .ToList();
            var restoreTargets = GetProperty(properties, "NuGetRestoreTargets");
            var defaultTargets = Path.Combine(GetProperty(properties, "MSBuildToolsPath"), "NuGet.targets");
            if (!string.IsNullOrEmpty(restoreTargets) &&
                !string.Equals(Path.GetFullPath(restoreTargets, projectFile.DirectoryName!),
                    Path.GetFullPath(defaultTargets), StringComparisons.FileSystemPath))
            {
                overrides.Add("NuGetRestoreTargets");
            }

            return new DotNetRestoreSettings(
                GetProperty(properties, "MSBuildProjectFullPath"),
                string.IsNullOrEmpty(restoreTargets) ? defaultTargets : restoreTargets,
                overrides);
        }
    }

    private static string GetProperty(JsonElement properties, string name)
        => properties.GetProperty(name).GetString() ?? string.Empty;
}

/// <summary>
/// Holds the current project's evaluated identity, restore targets, and configuration override names.
/// </summary>
internal sealed record DotNetRestoreSettings(
    string ProjectIdentity,
    string RestoreTargets,
    IReadOnlyList<string> ConfigurationOverrides)
{
    public bool UsesAmbientConfiguration => ConfigurationOverrides.Count == 0;
}
