// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Acquisition;

namespace Aspire.Cli.Utils;

/// <summary>
/// Detects whether the Aspire CLI is running from a Homebrew install and
/// provides the brew-equivalent self-update command, so the CLI surfaces the
/// correct guidance instead of attempting to overwrite a brew-managed binary
/// with the GitHub-binary downloader.
/// </summary>
/// <remarks>
/// <para>
/// Detection is sidecar-based: the formula writes
/// <c>{"source":"brew"}</c> into <c>.aspire-install.json</c> next to the CLI
/// binary in the Cellar (see <c>eng/homebrew-core/aspire.rb.template</c> and
/// <c>docs/specs/install-routes.md</c>). The path-shape of the install
/// (e.g. <c>/opt/homebrew/Cellar/...</c>) is intentionally NOT used: the
/// sidecar is the authoritative install-route contract, and adding path
/// heuristics here would risk false positives for unrelated binaries
/// placed under a Homebrew-shaped prefix.
/// </para>
/// <para>
/// Homebrew's published executable (e.g. <c>/opt/homebrew/bin/aspire</c>) is
/// a symlink into the Cellar. The sidecar lives next to the real binary, so
/// the process path is symlink-resolved before the sidecar lookup. This
/// matches <c>BundleService.ComputeDefaultExtractDir</c> and
/// <c>CliPathHelper.TryGetAspireHomeDirectoryFromInstallRoute</c>, which
/// also resolve the symlink before reading the sidecar.
/// </para>
/// </remarks>
internal static class BrewInstallDetection
{
    private static readonly AsyncLocal<string?> s_processPathOverride = new();

    internal static bool IsRunningFromBrew()
    {
        return GetBrewUpdateCommand() is not null;
    }

    internal static string? GetBrewUpdateCommand()
    {
        return GetBrewUpdateCommand(s_processPathOverride.Value ?? Environment.ProcessPath);
    }

    internal static string? GetBrewUpdateCommand(string? processPath)
    {
        if (string.IsNullOrEmpty(processPath))
        {
            return null;
        }

        var realBinaryPath = CliPathHelper.ResolveSymlinkOrOriginalPath(processPath);
        var binaryDir = Path.GetDirectoryName(realBinaryPath);
        if (string.IsNullOrEmpty(binaryDir))
        {
            return null;
        }

        var sidecarPath = Path.Combine(binaryDir, InstallSidecarReader.SidecarFileName);
        var rawSource = InstallSidecarReader.ReadSourceField(sidecarPath);
        if (InstallSourceExtensions.ParseInstallSource(rawSource) != InstallSource.Brew)
        {
            return null;
        }

        return "brew upgrade aspire";
    }

    internal static IDisposable UseProcessPathForTesting(string? processPath)
    {
        var previousValue = s_processPathOverride.Value;
        s_processPathOverride.Value = processPath;
        return new ProcessPathOverrideScope(previousValue);
    }

    private sealed class ProcessPathOverrideScope(string? previousValue) : IDisposable
    {
        public void Dispose()
        {
            s_processPathOverride.Value = previousValue;
        }
    }
}
