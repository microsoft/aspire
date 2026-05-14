// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Acquisition;

/// <summary>
/// Identifies the installation route that placed the running CLI binary.
/// The value is read from the <c>source</c> field of the
/// <c>.aspire-install.json</c> sidecar that sits next to the binary.
/// See <c>docs/specs/install-routes.md</c>.
/// </summary>
internal enum InstallSource
{
    /// <summary>
    /// No sidecar was found, or the sidecar contained a value that does not
    /// match any known route. Treated as legacy / pre-sidecar by callers.
    /// </summary>
    Unknown = 0,

    /// <summary>Release installer: <c>get-aspire-cli.{sh,ps1}</c>.</summary>
    Script,

    /// <summary>PR / dogfood installer: <c>get-aspire-cli-pr.{sh,ps1}</c>.</summary>
    Pr,

    /// <summary>WinGet portable manifest.</summary>
    Winget,

    /// <summary>Homebrew cask.</summary>
    Brew,

    /// <summary>.NET global tool (<c>dotnet tool install -g Aspire.Cli</c>).</summary>
    DotnetTool,

    /// <summary>Locally-built install from <c>localhive.sh</c> / <c>localhive.ps1</c>.</summary>
    LocalHive,
}

/// <summary>
/// Maps between <see cref="InstallSource"/> values and the wire strings used
/// in the sidecar's <c>source</c> field.
/// </summary>
internal static class InstallSourceExtensions
{
    // Wire strings — these match the contract in docs/specs/install-routes.md
    // exactly. They are kebab-case strings consumed by both the installer
    // scripts and BundleService.ComputeDefaultExtractDir.
    internal const string ScriptWire = "script";
    internal const string PrWire = "pr";
    internal const string WingetWire = "winget";
    internal const string BrewWire = "brew";
    internal const string DotnetToolWire = "dotnet-tool";
    internal const string LocalHiveWire = "localhive";

    /// <summary>
    /// Parses a sidecar <c>source</c> string into the strongly-typed enum.
    /// Returns <see cref="InstallSource.Unknown"/> for null, empty, or
    /// unrecognized values so callers can treat unknown sources as a
    /// legacy / pre-sidecar install.
    /// </summary>
    public static InstallSource ParseInstallSource(string? raw)
    {
        return raw switch
        {
            ScriptWire => InstallSource.Script,
            PrWire => InstallSource.Pr,
            WingetWire => InstallSource.Winget,
            BrewWire => InstallSource.Brew,
            DotnetToolWire => InstallSource.DotnetTool,
            LocalHiveWire => InstallSource.LocalHive,
            _ => InstallSource.Unknown,
        };
    }

    /// <summary>
    /// Returns the canonical wire string for a known
    /// <see cref="InstallSource"/>, or <see langword="null"/> for
    /// <see cref="InstallSource.Unknown"/>.
    /// </summary>
    public static string? ToWireString(this InstallSource source)
    {
        return source switch
        {
            InstallSource.Script => ScriptWire,
            InstallSource.Pr => PrWire,
            InstallSource.Winget => WingetWire,
            InstallSource.Brew => BrewWire,
            InstallSource.DotnetTool => DotnetToolWire,
            InstallSource.LocalHive => LocalHiveWire,
            _ => null,
        };
    }
}
