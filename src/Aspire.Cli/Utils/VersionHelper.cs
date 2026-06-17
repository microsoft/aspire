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
    /// Finds the candidate that exactly matches the running CLI's identity version when a channel
    /// has already been selected or local hives are present.
    /// </summary>
    /// <remarks>
    /// Pass <see cref="CliExecutionContext.IdentitySdkVersion"/> as <paramref name="cliVersion"/>
    /// so the comparison honors <c>ASPIRE_CLI_VERSION</c> / sidecar overrides rather than reading
    /// the assembly directly.
    /// </remarks>
    public static bool TryGetCurrentCliVersionMatch<T>(
        IEnumerable<T> candidates,
        Func<T, string?> versionSelector,
        string cliVersion,
        [MaybeNullWhen(false)] out T match,
        string? channelName,
        bool hasPrHives)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(versionSelector);
        ArgumentException.ThrowIfNullOrWhiteSpace(cliVersion);

        if (!hasPrHives && string.IsNullOrWhiteSpace(channelName))
        {
            match = default;
            return false;
        }

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

    // NOTE: GetDefaultTemplateVersion / GetDefaultSdkVersion read the running binary's assembly
    // version directly and therefore DO NOT honor ASPIRE_CLI_VERSION / sidecar identity overrides.
    // Identity-sensitive version decisions must read CliExecutionContext.IdentityVersion /
    // IdentitySdkVersion instead. These helpers remain for genuinely physical-binary reads (e.g.
    // bundled-package compatibility checks). See docs/specs/cli-identity-sidecar.md.
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
