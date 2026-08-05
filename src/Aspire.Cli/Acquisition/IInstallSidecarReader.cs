// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Acquisition;

/// <summary>
/// Result of reading an install-route sidecar from a binary directory.
/// </summary>
/// <param name="SidecarPath">
/// Absolute path of the sidecar file that was read. Always populated for
/// <see cref="InstallSidecarInfo"/> because a successful read requires a
/// resolved sidecar path.
/// </param>
/// <param name="Source">
/// Parsed install route. <see cref="InstallSource.Unknown"/> when the sidecar
/// exists but its <c>source</c> field does not match a known route.
/// </param>
/// <param name="RawSource">
/// The literal <c>source</c> string from the sidecar (may be a value not yet
/// understood by this build). Empty when the sidecar JSON is valid but the
/// <c>source</c> field is missing or empty.
/// </param>
/// <param name="Channel">
/// Optional channel identity override written by the installer (e.g.
/// <c>stable</c>, <c>staging</c>, <c>daily</c>, <c>pr-&lt;N&gt;</c>). Consumed
/// by <c>IIdentityResolver</c>. Null when the sidecar does not carry channel
/// information, in which case identity resolution falls back to the
/// assembly-baked <c>AspireCliChannel</c> metadata. See
/// <c>docs/specs/cli-identity-sidecar.md</c>.
/// </param>
/// <param name="Version">
/// Optional informational version override (e.g. <c>13.4.0</c>). Null when
/// absent. Resolved value is observed by call sites via
/// <c>CliExecutionContext.IdentityVersion</c>.
/// </param>
/// <param name="Commit">
/// Optional source-revision (commit SHA) override. Null when absent.
/// Resolved value is observed by call sites via
/// <c>CliExecutionContext.IdentityCommit</c>.
/// </param>
/// <param name="NuGetServiceIndexOverride">
/// Optional replacement for the <c>https://api.nuget.org/v3/index.json</c>
/// URL the CLI writes into <em>newly-generated</em> <c>NuGet.config</c> files.
/// Never used to rewrite URLs the CLI <em>reads</em> from existing user
/// configs — that asymmetry is intentional, see
/// <c>docs/specs/cli-identity-sidecar.md</c>. Null when no override is in
/// effect, in which case callers use the canonical URL from
/// <c>PackageSources.NuGetOrg</c>.
/// </param>
/// <param name="Packages">
/// Optional path to a flat directory of <c>.nupkg</c> files that the CLI's
/// <c>Aspire*</c> package feed should resolve from directly (the sidecar
/// equivalent of <c>ASPIRE_CLI_PACKAGES</c>). Null when absent. Consumed by
/// <c>PackagingService</c>, which synthesizes a package channel pointing at
/// this directory. See <c>docs/specs/cli-identity-sidecar.md</c>.
/// </param>
internal sealed record InstallSidecarInfo(
    string SidecarPath,
    InstallSource Source,
    string RawSource,
    string? Channel,
    string? Version,
    string? Commit,
    string? NuGetServiceIndexOverride,
    string? Packages);

/// <summary>
/// Result of attempting to read an install-route sidecar.
/// </summary>
/// <param name="SidecarPath">
/// Path of the sidecar file that was considered. Absolute when the binary
/// directory could be resolved; empty (<see cref="string.Empty"/>) when the
/// caller passed an empty or unusable directory (e.g.
/// <see cref="Path.GetDirectoryName(string?)"/> returned null/empty for the
/// candidate binary), in which case the result is always
/// <see cref="NotFound"/>.
/// </param>
internal abstract record InstallSidecarReadResult(string SidecarPath)
{
    /// <summary>Sidecar was read and parsed.</summary>
    public sealed record Ok(InstallSidecarInfo Info) : InstallSidecarReadResult(Info.SidecarPath);

    /// <summary>Sidecar file does not exist.</summary>
    public sealed record NotFound(string Path) : InstallSidecarReadResult(Path);

    /// <summary>Sidecar file exists but could not be read or parsed.</summary>
    public sealed record Invalid(string Path, string Reason) : InstallSidecarReadResult(Path);
}

/// <summary>
/// Reads the install-route sidecar (<c>.aspire-install.json</c>) that an
/// install route writes next to the CLI binary. The sidecar identifies the
/// installation route so callers (e.g. <c>BundleService</c>,
/// <c>aspire doctor</c>, <c>aspire uninstall</c>) can branch behavior without
/// path-shape heuristics.
/// </summary>
/// <remarks>
/// See <c>docs/specs/install-routes.md</c> for the file contract. The reader
/// is AOT-safe: parsing uses <c>JsonDocument</c> instead of reflection-based
/// deserialization.
/// </remarks>
internal interface IInstallSidecarReader
{
    /// <summary>
    /// Attempts to read the sidecar at
    /// <c>&lt;<paramref name="binaryDir"/>&gt;/.aspire-install.json</c>.
    /// </summary>
    /// <param name="binaryDir">Directory containing the CLI binary.</param>
    /// <returns>A categorized read result.</returns>
    InstallSidecarReadResult TryRead(string binaryDir);
}
