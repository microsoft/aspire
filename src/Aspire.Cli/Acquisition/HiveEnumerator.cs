// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Acquisition;

/// <summary>
/// Read-only enumeration of Aspire CLI package hives under
/// <c>$HOME/.aspire/hives/&lt;channel&gt;</c>. Hives are populated by install
/// scripts and read by <c>aspire installs list</c> to surface orphan hives.
/// </summary>
internal sealed class HiveEnumerator(CliExecutionContext executionContext)
{
    public DirectoryInfo AspireHomeDirectory => executionContext.HivesDirectory.Parent ?? executionContext.AspireHomeDirectory;

    public IReadOnlyList<HiveInfo> GetHives()
    {
        if (!executionContext.HivesDirectory.Exists)
        {
            return [];
        }

        return executionContext.HivesDirectory
            .EnumerateDirectories()
            .OrderBy(d => d.Name, StringComparer.Ordinal)
            .Select(d => new HiveInfo(d.Name, d.FullName, GetDogfoodDirectory(d.Name).Exists))
            .ToList();
    }

    public string GetHivePath(string channel)
        => GetHiveDirectory(channel).FullName;

    public bool HasHive(string channel)
        => GetHiveDirectory(channel).Exists;

    private DirectoryInfo GetHiveDirectory(string channel)
        => new(Path.Combine(executionContext.HivesDirectory.FullName, channel));

    private DirectoryInfo GetDogfoodDirectory(string channel)
        => new(Path.Combine(AspireHomeDirectory.FullName, "dogfood", channel));
}

internal sealed record HiveInfo(string Name, string Path, bool HasMatchingDogfoodInstall);
