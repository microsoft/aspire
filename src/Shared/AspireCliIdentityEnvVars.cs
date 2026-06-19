// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Shared;

/// <summary>
/// The canonical set of <c>ASPIRE_CLI_*</c> identity-override environment
/// variable names recognised by the CLI's <c>IdentityResolver</c>. Shared
/// between the CLI (which reads them and strips them at child-process spawn)
/// and any external tooling that needs to author them into a child shell to
/// coerce a chosen identity.
/// </summary>
/// <remarks>
/// <para>
/// Keep this file in sync with <c>docs/specs/cli-identity-sidecar.md</c>.
/// If you add a new override constant here, also add it to
/// <see cref="IdentityEnvVarNames"/> so the CLI's strip-list and the
/// resolver's read-list stay in lockstep — without it, a parent-process
/// override would silently leak into peer probes and corrupt
/// <c>aspire doctor</c>.
/// </para>
/// <para>
/// This file is link-included from <c>src/Aspire.Cli/Aspire.Cli.csproj</c>
/// (and any future host that needs to author these env vars) rather than
/// exposed via a runtime dependency, because the CLI ships as a standalone
/// executable and cannot afford a NuGet package round-trip just to share
/// these string constants.
/// </para>
/// </remarks>
internal static class AspireCliIdentityEnvVars
{
    /// <summary>
    /// Overrides the CLI's running channel (e.g. <c>stable</c>, <c>staging</c>,
    /// <c>daily</c>, <c>pr-&lt;N&gt;</c>, <c>local</c>).
    /// </summary>
    public const string Channel = "ASPIRE_CLI_CHANNEL";

    /// <summary>
    /// Overrides the CLI's reported informational version string
    /// (e.g. <c>13.4.0-preview.1.25366.3</c>).
    /// </summary>
    public const string Version = "ASPIRE_CLI_VERSION";

    /// <summary>
    /// Overrides the CLI's reported source-revision commit (the
    /// <c>+&lt;sha&gt;</c> portion of <c>AssemblyInformationalVersion</c>
    /// when no override is in effect).
    /// </summary>
    public const string Commit = "ASPIRE_CLI_COMMIT";

    /// <summary>
    /// Overrides the canonical <c>https://api.nuget.org/v3/index.json</c>
    /// URL the CLI writes into <em>newly-generated</em> <c>NuGet.config</c>
    /// files. Never rewrites URLs the CLI reads from existing user configs.
    /// </summary>
    public const string NuGetServiceIndex = "ASPIRE_CLI_NUGET_SERVICE_INDEX";

    /// <summary>
    /// Points the CLI's <c>Aspire*</c> package feed directly at a flat
    /// directory of <c>.nupkg</c> files (for example
    /// <c>artifacts/packages/&lt;Config&gt;/Shipping</c>) instead of a hive
    /// staged under <c>~/.aspire/hives</c>. The CLI synthesizes a package
    /// channel named after its resolved identity channel that maps
    /// <c>Aspire*</c> to this directory, so a locally built CLI can resolve
    /// freshly built packages without copying them. The directory must
    /// contain at most one version of each <c>Aspire*</c> package; the CLI
    /// fails fast when duplicates are present so an unintended version cannot
    /// be silently selected.
    /// </summary>
    public const string Packages = "ASPIRE_CLI_PACKAGES";

    private static readonly string[] s_all =
    [
        Channel,
        Version,
        Commit,
        NuGetServiceIndex,
        Packages,
    ];

    /// <summary>
    /// The full set of identity-override env var names, in declaration order.
    /// Iterate this when you need to strip every override at once (the CLI
    /// does this before spawning peer / app-host child processes) or when
    /// you need to enumerate the surface for diagnostics output.
    /// </summary>
    public static IReadOnlyList<string> IdentityEnvVarNames => s_all;
}
