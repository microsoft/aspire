// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Dogfooder.Scenarios;

/// <summary>
/// A pre-baked dogfooding configuration. Scenarios bundle a coherent set of
/// build/version/proxy/identity settings so the user picks an intent
/// ("reproduce vCurrent") rather than wiring a dozen independent knobs that
/// must agree with each other to be useful.
/// </summary>
/// <remarks>
/// <para>
/// Each scenario implementation derives the full <see cref="ScenarioPlan"/>
/// from its few declared <see cref="Inputs"/>. The dogfooder UI renders the
/// scenario picker and only the inputs the picked scenario declares; the
/// preparer asks the scenario for its plan and runs the existing build →
/// proxy → environment-overrides pipeline against the result.
/// </para>
/// <para>
/// Scenarios are discovered through <see cref="DogfoodScenarioRegistry"/>;
/// each registered scenario is a singleton so it can cache things like
/// the detected vCurrent version without re-reading <c>eng/Versions.props</c>
/// on every render pass.
/// </para>
/// </remarks>
internal interface IDogfoodScenario
{
    /// <summary>
    /// Stable id persisted in <c>sessions.json</c>; never localise or rename
    /// once a scenario has shipped (existing saved sessions reference it).
    /// </summary>
    string Id { get; }

    /// <summary>Human-facing scenario label shown in the picker.</summary>
    string DisplayName { get; }

    /// <summary>One-paragraph description shown alongside the picker.</summary>
    string Description { get; }

    /// <summary>
    /// Inputs the scenario needs from the user before it can produce a plan.
    /// Empty for scenarios that are fully self-describing (everything in this
    /// catalog except <c>vnext-pr-local</c> at the time of writing).
    /// </summary>
    IReadOnlyList<ScenarioInputSpec> Inputs { get; }

    /// <summary>
    /// Produces the concrete <see cref="ScenarioPlan"/> the preparer should
    /// execute. Implementations should treat missing/empty input values
    /// defensively (the form may submit an incomplete dict) and fall back to
    /// sensible defaults where possible; surface a clear plan that the
    /// preparation log can render even when inputs are off.
    /// </summary>
    ScenarioPlan Build(IReadOnlyDictionary<string, string?> inputs);
}
