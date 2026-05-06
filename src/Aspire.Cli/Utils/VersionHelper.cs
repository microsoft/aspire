// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using Aspire.Cli.Resources;
using Aspire.Shared;

namespace Aspire.Cli.Utils;

internal static class VersionHelper
{
    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="channelName"/> identifies a
    /// locally-built channel — a PR hive (<c>pr-*</c>), a workflow-run hive (<c>run-*</c>),
    /// or a local development build (<c>local</c>).
    /// </summary>
    public static bool IsLocalBuildChannel(string? channelName)
    {
        return channelName is not null &&
            (channelName.Equals("local", StringComparison.OrdinalIgnoreCase) ||
             channelName.StartsWith("pr-", StringComparison.OrdinalIgnoreCase) ||
             channelName.StartsWith("run-", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Finds the candidate that exactly matches the current CLI/SDK version when running against local build channels or hives.
    /// </summary>
    public static bool TryGetCurrentCliVersionMatch<T>(
        IEnumerable<T> candidates,
        Func<T, string?> versionSelector,
        [MaybeNullWhen(false)] out T match,
        string? channelName,
        bool hasPrHives)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(versionSelector);

        if (!hasPrHives && !IsLocalBuildChannel(channelName))
        {
            match = default;
            return false;
        }

        var cliVersion = GetDefaultSdkVersion();
        foreach (var candidate in candidates)
        {
            if (string.Equals(versionSelector(candidate), cliVersion, StringComparison.OrdinalIgnoreCase))
            {
                match = candidate;
                return true;
            }
        }

        match = default;
        return false;
    }

    public static string GetDefaultTemplateVersion()
    {
        return PackageUpdateHelpers.GetCurrentAssemblyVersion() ?? throw new InvalidOperationException(ErrorStrings.UnableToRetrieveAssemblyVersion);
    }

    /// <summary>
    /// Gets the default Aspire SDK version based on the CLI version.
    /// The CLI version is the SDK version — the bundled server and packages must match.
    /// </summary>
    public static string GetDefaultSdkVersion()
    {
        var version = GetDefaultTemplateVersion();

        // Strip the commit SHA suffix (e.g., "9.2.0+abc123" -> "9.2.0")
        var plusIndex = version.IndexOf('+');
        if (plusIndex > 0)
        {
            version = version[..plusIndex];
        }

        return version;
    }
}

