// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Dogfooder.State;

/// <summary>
/// Channel personas the CLI can be coerced into via
/// <c>ASPIRE_CLI_CHANNEL</c>. Mirrors the values the CLI itself understands;
/// see <c>docs/specs/cli-identity-sidecar.md</c>.
/// </summary>
internal enum ChannelKind
{
    /// <summary>Latest release-quality drop.</summary>
    Stable,

    /// <summary>Release-candidate / pre-shipping drop.</summary>
    Staging,

    /// <summary>Internal daily build.</summary>
    Daily,

    /// <summary>A specific in-flight pull request build.</summary>
    Pr,

    /// <summary>A locally-built CLI (assembly metadata is authoritative).</summary>
    Local,
}

/// <summary>
/// Immutable snapshot of the overrides the user has selected for a single
/// dogfooding session. Persisted to <c>~/.aspire/dogfooder/sessions.json</c>
/// and converted to an env-var dictionary by
/// <c>IDogfoodSessionPreparer.PrepareAsync</c> when the embedded terminal
/// launches.
/// </summary>
/// <param name="Channel">Which channel persona the CLI should report.</param>
/// <param name="PrNumber">For <see cref="ChannelKind.Pr"/>, the GH PR number. Null otherwise.</param>
/// <param name="VersionOverride">Optional <c>ASPIRE_CLI_VERSION</c> value.</param>
/// <param name="CommitOverride">Optional <c>ASPIRE_CLI_COMMIT</c> value.</param>
/// <param name="NuGetServiceIndexOverride">
/// Optional substitute for the <c>https://api.nuget.org/v3/index.json</c> URL
/// the CLI writes into generated <c>NuGet.config</c> files. Use this when
/// pointing the CLI at a local NuGet pass-through proxy.
/// </param>
internal sealed record DogfoodSessionConfig(
    ChannelKind Channel,
    int? PrNumber,
    string? VersionOverride,
    string? CommitOverride,
    string? NuGetServiceIndexOverride)
{
    public static DogfoodSessionConfig Empty { get; } = new(
        Channel: ChannelKind.Stable,
        PrNumber: null,
        VersionOverride: null,
        CommitOverride: null,
        NuGetServiceIndexOverride: null);
}
