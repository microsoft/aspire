// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Acquisition;

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
///   <item><description>Environment variable (<c>ASPIRE_CLI_CHANNEL</c>, <c>ASPIRE_CLI_VERSION</c>, <c>ASPIRE_CLI_COMMIT</c>, <c>ASPIRE_CLI_NUGET_SERVICE_INDEX</c>, <c>ASPIRE_CLI_PACKAGES</c>).</description></item>
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

    /// <summary>
    /// Resolves an optional path to a flat directory of <c>.nupkg</c> files
    /// (for example <c>artifacts/packages/&lt;Config&gt;/Shipping</c>) that the
    /// CLI's <c>Aspire*</c> package feed should resolve from directly, sourced
    /// from <c>ASPIRE_CLI_PACKAGES</c> or the <c>packages</c> field of
    /// <c>.aspire-install.json</c>. Returns <see cref="IdentityValue{T}.Value"/>
    /// as <see langword="null"/> when no override is in effect.
    /// </summary>
    /// <remarks>
    /// When set, <c>PackagingService</c> synthesizes a package channel named
    /// after the resolved identity channel that maps <c>Aspire*</c> to this
    /// directory, letting a locally built CLI resolve locally built packages
    /// without staging them under <c>~/.aspire/hives</c>. See
    /// <c>docs/specs/cli-identity-sidecar.md</c>.
    /// </remarks>
    IdentityValue<string?> ResolvePackagesDirectory();
}
