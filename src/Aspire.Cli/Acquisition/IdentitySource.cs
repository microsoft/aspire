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
    /// Resolver had nothing to read from any layer and used the terminal
    /// default. For channel that default is <c>local</c>. For the optional
    /// NuGet service-index and packages-directory overrides — which have no
    /// assembly-baked equivalent — it is <see langword="null"/> (no override).
    /// Version and commit never use this source: they always fall back to the
    /// assembly informational version.
    /// </summary>
    TerminalDefault,
}
