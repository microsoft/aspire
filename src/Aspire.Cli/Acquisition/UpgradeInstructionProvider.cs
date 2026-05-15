// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Resources;
using Aspire.Cli.Utils;

namespace Aspire.Cli.Acquisition;

/// <summary>
/// Returns the installer-appropriate command a user should run to update
/// the Aspire CLI, given the install route that produced their binary.
/// Consumed by <c>aspire update --self</c>'s refusal path and the
/// "update available" notifier so users always see the right command
/// for their install.
/// </summary>
internal interface IUpgradeInstructionProvider
{
    /// <summary>
    /// Returns the command string a user should run to update an
    /// installation produced by <paramref name="source"/>.
    /// </summary>
    /// <param name="source">Install route the running binary was placed by.</param>
    /// <param name="processPath">Absolute path of the running binary. Used
    /// only for <see cref="InstallSource.DotnetTool"/>: a global-tool
    /// install (under <c>~/.dotnet/tools/.store/</c>) gets
    /// <c>dotnet tool update -g Aspire.Cli</c>, a <c>--tool-path</c>
    /// install gets the path-aware variant.</param>
    /// <param name="identityChannel">The CLI's identity channel
    /// (<c>CliExecutionContext.IdentityChannel</c>). Used only for
    /// <see cref="InstallSource.Pr"/> to substitute the PR number into the
    /// <c>get-aspire-cli-pr</c> command.</param>
    /// <returns>
    /// The command to print verbatim, or <see langword="null"/> for
    /// <see cref="InstallSource.Script"/> (which stays in-process and has
    /// no separate update command to display).
    /// </returns>
    string? GetUpdateCommand(InstallSource source, string? processPath, string identityChannel);
}

/// <summary>
/// Default <see cref="IUpgradeInstructionProvider"/>. The mapping is a
/// pure function of <c>(source, processPath, identityChannel)</c>; no
/// I/O beyond the path-shape parsing already performed by
/// <see cref="DotNetToolDetection"/> for the <see cref="InstallSource.DotnetTool"/>
/// route.
/// </summary>
internal sealed class UpgradeInstructionProvider : IUpgradeInstructionProvider
{
    /// <inheritdoc />
    public string? GetUpdateCommand(InstallSource source, string? processPath, string identityChannel)
    {
        return source switch
        {
            // Script is the in-process update path; no separate command to display.
            InstallSource.Script => null,

            InstallSource.Pr => GetPrUpdateCommand(identityChannel),
            InstallSource.Winget => "winget upgrade Microsoft.Aspire",
            InstallSource.Brew => "brew upgrade --cask aspire",

            // Prefer the supplied process path so tests and callers can
            // classify synthesized paths without depending on Environment.ProcessPath.
            // When no path is supplied, the no-arg overload preserves the
            // existing production behavior and AsyncLocal test override.
            InstallSource.DotnetTool => GetDotNetToolUpdateCommand(processPath),

            // LocalHive installs are produced by re-running the dev script
            // in the user's own checkout. There is no canonical update
            // command — the user must rebuild from source.
            InstallSource.LocalHive => "Run ./localhive.sh (Linux/macOS) or .\\localhive.ps1 (Windows) in the local hive directory.",
            InstallSource.Unknown => UpdateCommandStrings.UnknownRouteRefusalHint,

            _ => null,
        };
    }

    private const string PrChannelPrefix = "pr-";

    private static string GetDotNetToolUpdateCommand(string? processPath)
    {
        return (processPath is not null
                ? DotNetToolDetection.GetDotNetToolUpdateCommand(processPath)
                : DotNetToolDetection.GetDotNetToolUpdateCommand())
            ?? "dotnet tool update -g Aspire.Cli";
    }

    private static string GetPrUpdateCommand(string identityChannel)
    {
        // The PR channel form is `pr-<N>` (parsed and validated by
        // IdentityChannelReader); extract the digits if present so the
        // hint shows the actual PR number.
        if (identityChannel.StartsWith(PrChannelPrefix, StringComparison.Ordinal) &&
            identityChannel.Length > PrChannelPrefix.Length)
        {
            var prNumber = identityChannel[PrChannelPrefix.Length..];
            // Print both POSIX and Windows install lines so the docs are
            // discoverable regardless of which shell the user is on.
            return $"get-aspire-cli-pr.sh {prNumber}    # or: get-aspire-cli-pr.ps1 -PRNumber {prNumber}";
        }

        // Defensive: if a PR-route sidecar coexists with a non-PR identity
        // channel (theoretically impossible because PR archives bake the
        // matching channel), emit the parameterised form so the user knows
        // they need to supply the number.
        return "get-aspire-cli-pr.sh <N>    # or: get-aspire-cli-pr.ps1 -PRNumber <N>";
    }
}
