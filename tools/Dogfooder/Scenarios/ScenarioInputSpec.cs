// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Dogfooder.Scenarios;

/// <summary>
/// Declarative description of one scenario input field. The form renders one
/// <c>FormTextField</c> per spec, keyed by <see cref="Key"/>; submitting the
/// form populates an <c>IReadOnlyDictionary&lt;string,string?&gt;</c> that
/// the scenario's <c>Build</c> method consumes.
/// </summary>
/// <param name="Key">Stable key persisted in <c>sessions.json</c>.</param>
/// <param name="Label">Form label shown to the user.</param>
/// <param name="Placeholder">Optional placeholder hint shown when empty.</param>
/// <param name="Required">
/// When true, the form's Continue button is disabled until a non-whitespace
/// value is supplied. When false the scenario must tolerate an empty value
/// and fall back to a sensible default.
/// </param>
internal sealed record ScenarioInputSpec(
    string Key,
    string Label,
    string? Placeholder = null,
    bool Required = false);
