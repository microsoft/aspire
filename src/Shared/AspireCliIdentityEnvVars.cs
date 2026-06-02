// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Shared;

/// <summary>
/// The canonical set of <c>ASPIRE_CLI_*</c> identity-override environment
/// variable names recognised by the CLI's <c>IdentityResolver</c>. Shared
/// between the CLI (which reads them and strips them at child-process spawn)
/// and external tools like <c>tools/Dogfooder</c> (which writes them into
/// the embedded child shell to coerce a chosen identity).
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
/// and <c>tools/Dogfooder/Dogfooder.csproj</c> (and any future host that
/// needs to author these env vars) rather than exposed via a runtime
/// dependency, because both consumers ship as standalone executables and
/// neither can afford a NuGet package round-trip just to share four string
/// constants.
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

    private static readonly string[] s_all =
    [
        Channel,
        Version,
        Commit,
        NuGetServiceIndex,
    ];

    /// <summary>
    /// The full set of identity-override env var names, in declaration order.
    /// Iterate this when you need to strip every override at once (the CLI
    /// does this before spawning peer / app-host child processes) or when
    /// you need to enumerate the surface for diagnostics output.
    /// </summary>
    public static IReadOnlyList<string> IdentityEnvVarNames => s_all;
}
