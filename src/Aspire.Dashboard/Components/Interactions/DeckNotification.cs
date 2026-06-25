// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.AspNetCore.Components;

namespace Aspire.Dashboard.Components.Interactions;

/// <summary>
/// View model for a single notification toast rendered by <see cref="NotificationStack"/>.
/// Backed by a live AppHost interaction; it stays until the user acts on it or dismisses it.
/// </summary>
/// <param name="InteractionId">The AppHost interaction id this toast responds to.</param>
/// <param name="Title">Optional toast title.</param>
/// <param name="Message">Rendered message HTML (already HTML-encoded or sanitized Markdown).</param>
/// <param name="Tone">Deck tone class: <c>error</c>/<c>warning</c>/<c>success</c>/<c>info</c>.</param>
/// <param name="PrimaryButtonText">Primary action label, or <see langword="null"/> for none.</param>
/// <param name="SecondaryButtonText">Secondary action label.</param>
/// <param name="ShowSecondaryButton">Whether the secondary action is shown.</param>
/// <param name="ShowDismiss">Whether the dismiss (X) button is shown.</param>
/// <param name="LinkText">Optional link label.</param>
/// <param name="LinkUrl">Optional link URL (opened in a new tab).</param>
public sealed record DeckNotification(
    int InteractionId,
    string? Title,
    MarkupString Message,
    string Tone,
    string? PrimaryButtonText,
    string? SecondaryButtonText,
    bool ShowSecondaryButton,
    bool ShowDismiss,
    string? LinkText,
    string? LinkUrl);
