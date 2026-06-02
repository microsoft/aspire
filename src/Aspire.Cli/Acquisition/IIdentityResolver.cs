// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Acquisition;

/// <summary>
/// Where a resolved identity field originated. Surfaced by
/// <c>aspire doctor --self</c> so an operator can tell at a glance whether an
/// override is active, which sidecar populated it, or whether the resolver
/// fell back to the build-time stamp. See
/// <c>docs/specs/cli-identity-sidecar.md</c>.
/// </summary>
internal enum IdentitySource
{
    /// <summary>Value came from an <c>ASPIRE_CLI_*</c> environment variable.</summary>
    Environment,

    /// <summary>Value came from a field in <c>.aspire-install.json</c> next to the running binary.</summary>
    Sidecar,

    /// <summary>
    /// Value came from the assembly's build-time stamp (for example
    /// <c>[AssemblyMetadata("AspireCliChannel", ...)]</c> for channel, or
    /// <c>AssemblyInformationalVersion</c> for version/commit). This is the
    /// path locally-built dev binaries take when no sidecar exists and no
    /// env var is set.
    /// </summary>
    AssemblyFallback,

    /// <summary>
    /// Resolver had nothing to read — no env var, no sidecar field, no
    /// assembly stamp — and used the terminal default. For channel this is
    /// <c>local</c>. Other fields do not use this source today.
    /// </summary>
    TerminalDefault,
}

/// <summary>
/// A resolved identity value tagged with the layer that produced it. The
/// tag is the only signal an operator has for "is my override actually
/// taking effect?", so every resolved field carries one.
/// </summary>
internal readonly record struct IdentityValue<T>(T Value, IdentitySource Source);

/// <summary>
/// Resolves the running CLI's identity — channel, version, commit — and the
/// optional NuGet service-index override that lets a test-bench session
/// point newly-generated <c>NuGet.config</c> files at a local proxy.
/// </summary>
/// <remarks>
/// <para>
/// Each field is resolved independently so a caller can override one without
/// inheriting the others. Resolution order, highest precedence first:
/// </para>
/// <list type="number">
///   <item><description>Environment variable (<c>ASPIRE_CLI_CHANNEL</c>, <c>ASPIRE_CLI_VERSION</c>, <c>ASPIRE_CLI_COMMIT</c>, <c>ASPIRE_CLI_NUGET_SERVICE_INDEX</c>).</description></item>
///   <item><description>The matching field in <c>.aspire-install.json</c> next to the running binary.</description></item>
///   <item><description>For channel/version/commit: the assembly's build-time stamp. For the NuGet override: <see langword="null"/> (no override).</description></item>
/// </list>
/// <para>
/// The env-var layer is deliberately <strong>not</strong> propagated to child
/// Aspire CLI processes (see the env-strip behavior in <c>PeerInstallProbe</c>
/// and friends). Treat the overrides as process-local test affordances, never
/// as ambient configuration. See <c>docs/specs/cli-identity-sidecar.md</c>.
/// </para>
/// </remarks>
internal interface IIdentityResolver
{
    /// <summary>
    /// Resolves the running CLI's channel identity (e.g. <c>stable</c>,
    /// <c>staging</c>, <c>daily</c>, <c>local</c>, <c>pr-&lt;N&gt;</c>).
    /// </summary>
    IdentityValue<string> ResolveChannel();

    /// <summary>
    /// Resolves the running CLI's informational version
    /// (e.g. <c>13.4.0-preview.1.25366.3</c>).
    /// </summary>
    IdentityValue<string> ResolveVersion();

    /// <summary>
    /// Resolves the running CLI's source-revision commit (the <c>+&lt;sha&gt;</c>
    /// portion of <see cref="System.Reflection.AssemblyInformationalVersionAttribute"/>
    /// when no override is in effect). Returns an empty string when neither a
    /// sidecar nor the assembly informational version carries a commit suffix.
    /// </summary>
    IdentityValue<string> ResolveCommit();

    /// <summary>
    /// Resolves an optional replacement for the canonical
    /// <c>https://api.nuget.org/v3/index.json</c> URL the CLI writes into
    /// <em>newly-generated</em> <c>NuGet.config</c> files. Returns
    /// <see cref="IdentityValue{T}.Value"/> as <see langword="null"/> when no
    /// override is in effect — callers then use
    /// <c>Packaging.PackageSources.NuGetOrg</c>.
    /// </summary>
    /// <remarks>
    /// This override never rewrites URLs the CLI <em>reads</em> from existing
    /// user configs. That asymmetry is intentional and the contract callers
    /// rely on; consult <c>docs/specs/cli-identity-sidecar.md</c> for the
    /// reasoning before adding a consumer.
    /// </remarks>
    IdentityValue<string?> ResolveNuGetServiceIndexOverride();
}
