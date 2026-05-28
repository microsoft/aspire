// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using Aspire.Cli.Packaging;
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
            (channelName.Equals(PackageChannelNames.Local, StringComparison.OrdinalIgnoreCase) ||
             channelName.StartsWith("pr-", StringComparison.OrdinalIgnoreCase) ||
             channelName.StartsWith("run-", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Finds the candidate that exactly matches the current CLI/SDK version when a channel has already been selected or local hives are present.
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

        if (!hasPrHives && string.IsNullOrWhiteSpace(channelName))
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
    /// Returns the first <paramref name="length"/> characters of the commit hash from the current
    /// assembly's <see cref="System.Reflection.AssemblyInformationalVersionAttribute"/> (e.g.
    /// <c>"13.4.0+48a11dae9c0..." -&gt; "48a11dae"</c>). Returns <see langword="null"/> when the
    /// running assembly has no <c>+sha</c> suffix (test hosts, locally-built bits, dev builds).
    /// </summary>
    /// <remarks>
    /// Two staging builds of the same release branch ship with an identical stable-shaped semver
    /// (e.g. <c>13.4.0</c>) but different commit SHAs. Callers that need a deterministic-but-build-
    /// specific cache key (for example, the <c>globalPackagesFolder</c> override used by the
    /// PrebuiltAppHostServer staging restore) use the truncated SHA so each staging build gets
    /// its own cache directory. The default length matches the darc feed URL convention used by
    /// <c>PackagingService</c> so the cache key and the feed key line up at 8 hex chars.
    /// </remarks>
    public static string? TryGetCurrentCommitHashShort(int length = 8)
    {
        if (length <= 0)
        {
            return null;
        }

        var version = PackageUpdateHelpers.GetCurrentAssemblyVersion();
        if (string.IsNullOrEmpty(version))
        {
            return null;
        }

        // Informational version shape: "<semver>+<commitHash>" (e.g. "13.4.0+48a11dae9c0a...").
        // No '+' means a clean release build with no build metadata — no SHA to surface.
        var plusIndex = version.IndexOf('+');
        if (plusIndex < 0 || plusIndex + 1 >= version.Length)
        {
            return null;
        }

        var commitHash = version[(plusIndex + 1)..];
        return commitHash.Length >= length ? commitHash[..length] : commitHash;
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
