// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Acquisition;

/// <summary>
/// Read-only enumeration of Aspire CLI package hives under
/// <c>$HOME/.aspire/hives/&lt;channel&gt;</c>. Hives are populated by install
/// scripts and read by <c>aspire --info</c> to surface orphan hives.
/// </summary>
/// <remarks>
/// Channel-keyed lookups (<see cref="HasHive"/>, <see cref="GetHivePath"/>)
/// only accept the channel shapes that the build pipeline ever bakes — see
/// <see cref="IdentityChannelReader.IsValidChannel(string)"/>. Channels can
/// reach this type via peer <c>--info --self</c> output (untrusted JSON
/// from a sibling CLI binary), so any value outside that allow-list is
/// treated as "no hive": it prevents directory-separator / <c>..</c> /
/// invalid-character inputs from being fed to <c>Path.Combine</c> +
/// <see cref="DirectoryInfo"/>, which would otherwise either escape the
/// hives root in the displayed path or throw on the <see cref="DirectoryInfo"/>
/// constructor.
/// </remarks>
internal sealed class HiveEnumerator(CliExecutionContext executionContext)
{
    public IReadOnlyList<HiveInfo> GetHives()
    {
        if (!executionContext.HivesDirectory.Exists)
        {
            return [];
        }

        return executionContext.HivesDirectory
            .EnumerateDirectories()
            .OrderBy(d => d.Name, StringComparer.Ordinal)
            .Select(d => new HiveInfo(d.Name, d.FullName))
            .ToList();
    }

    /// <summary>
    /// Returns the on-disk path of the hive for <paramref name="channel"/>,
    /// or <see langword="null"/> when <paramref name="channel"/> is not a
    /// recognized channel shape. Returning a path here makes no claim about
    /// whether the directory exists — use <see cref="HasHive"/> for that.
    /// </summary>
    public string? GetHivePath(string channel)
        => TryGetHiveDirectory(channel) is { } dir ? dir.FullName : null;

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="channel"/> is a
    /// recognized channel shape and the corresponding hive directory exists.
    /// Unrecognized channel shapes always return <see langword="false"/>.
    /// </summary>
    public bool HasHive(string channel)
        => TryGetHiveDirectory(channel) is { Exists: true };

    private DirectoryInfo? TryGetHiveDirectory(string channel)
    {
        if (!IdentityChannelReader.IsValidChannel(channel))
        {
            return null;
        }

        return new DirectoryInfo(Path.Combine(executionContext.HivesDirectory.FullName, channel));
    }
}

internal sealed record HiveInfo(string Name, string Path);
