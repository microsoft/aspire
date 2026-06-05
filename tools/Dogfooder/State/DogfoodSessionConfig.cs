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
/// Persisted snapshot of the user's scenario choice for a single dogfooding
/// session. The session no longer carries the bazillion knobs (channel,
/// version, build toggle, proxy toggle, …) directly — they are derived from
/// the scenario at preparation time. This record carries only what we need
/// to round-trip: the scenario id and any user-supplied inputs the scenario
/// declared (e.g. PR number).
/// </summary>
/// <param name="ScenarioId">
/// Stable id of the chosen <c>IDogfoodScenario</c>. The
/// <c>DogfoodScenarioRegistry</c> looks the scenario up at preparation time
/// and falls back to its <c>Default</c> when an id no longer resolves
/// (older session JSON, removed/renamed scenarios).
/// </param>
/// <param name="Inputs">
/// User-supplied values for the scenario's declared inputs, keyed by
/// <c>ScenarioInputSpec.Key</c>. Values may be null/empty when the user has
/// not filled them in yet; scenarios are responsible for defending against
/// that.
/// </param>
internal sealed record DogfoodSessionConfig(
    string ScenarioId,
    IReadOnlyDictionary<string, string?> Inputs)
{
    /// <summary>
    /// Convenience for callers that want to construct a session with no
    /// inputs yet. The scenario id must be valid for the registry; see
    /// <c>DogfoodScenarioRegistry.Default.Id</c> for the conventional default.
    /// </summary>
    public static DogfoodSessionConfig ForScenario(string scenarioId) =>
        new(scenarioId, new Dictionary<string, string?>(StringComparer.Ordinal));
}
