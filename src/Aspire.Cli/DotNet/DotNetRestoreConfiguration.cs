// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text.Json;
using Aspire.Cli.Projects;
using Aspire.Cli.Resources;

namespace Aspire.Cli.DotNet;

/// <summary>
/// Inspects the current C# AppHost's MSBuild restore configuration without restoring its project graph.
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
                "NuGetRestoreTargets",
                "_OutputConfigFilePaths",
                "_OutputPackagesPath"
            ],
            targets: ["_GetRestoreProjectStyle", "_GetRestoreSettings"],
            new ProcessInvocationOptions
            {
                // File-based build otherwise performs implicit restore before the requested
                // targets. A global property prevents project imports from overriding NuGet's
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
            // MSBuild emits {"Properties":{"MSBuildProjectFullPath":".../apphost.cs.csproj",
            // "_OutputConfigFilePaths":".../NuGet.Config;..."}}. Keep the synthetic identity for
            // file-based apps; it is the identity used by later MSBuild restore targets.
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
                GetProperty(properties, "_OutputConfigFilePaths").Split(';', StringSplitOptions.RemoveEmptyEntries),
                GetProperty(properties, "_OutputPackagesPath"),
                string.IsNullOrEmpty(restoreTargets) ? defaultTargets : restoreTargets,
                overrides);
        }
    }

    private static string GetProperty(JsonElement properties, string name)
        => properties.GetProperty(name).GetString() ?? string.Empty;
}

/// <summary>
/// Holds the current project's resolved restore inputs and configuration override names.
/// </summary>
internal sealed record DotNetRestoreSettings(
    string ProjectIdentity,
    IReadOnlyList<string> ConfigPaths,
    string PackagesPath,
    string RestoreTargets,
    IReadOnlyList<string> ConfigurationOverrides)
{
    public bool UsesAmbientConfiguration => ConfigurationOverrides.Count == 0;
}
