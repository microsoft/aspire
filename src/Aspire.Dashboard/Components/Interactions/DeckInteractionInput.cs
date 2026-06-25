// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Dashboard.Components.Interactions;

/// <summary>
/// View model for a single input field rendered by <see cref="InteractionPane"/>, ported from
/// Deck's <c>InteractionInputInfo</c> (src/Aspire.Deck/CONTRACT.md). Presentational only — the
/// provider maps the AppHost interaction inputs to this shape.
/// </summary>
/// <param name="Name">Input name (the key sent back in the response values map).</param>
/// <param name="Label">Field label.</param>
/// <param name="Placeholder">Placeholder text.</param>
/// <param name="InputType">One of <c>text</c>/<c>secretText</c>/<c>choice</c>/<c>boolean</c>/<c>number</c>.</param>
/// <param name="Required">Whether the field is required.</param>
/// <param name="Options"><c>(value, display)</c> options for choice inputs.</param>
/// <param name="Value">Current value.</param>
/// <param name="ValidationErrors">Inline validation errors shown under the field.</param>
/// <param name="Description">Help text shown under the field.</param>
/// <param name="MaxLength">Maximum length (0 = unlimited).</param>
/// <param name="AllowCustomChoice">Whether a choice input accepts a free value.</param>
/// <param name="Disabled">Whether the field is disabled.</param>
/// <param name="UpdateStateOnChange">Whether changing the value re-validates with the server live.</param>
public sealed record DeckInteractionInput(
    string Name,
    string Label,
    string Placeholder,
    string InputType,
    bool Required,
    IReadOnlyList<(string Value, string Display)> Options,
    string Value,
    IReadOnlyList<string> ValidationErrors,
    string Description,
    int MaxLength,
    bool AllowCustomChoice,
    bool Disabled,
    bool UpdateStateOnChange);
