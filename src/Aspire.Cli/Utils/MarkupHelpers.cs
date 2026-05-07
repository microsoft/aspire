// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Interaction;
using Spectre.Console;

namespace Aspire.Cli.Utils;

/// <summary>
/// Provides helpers for building Spectre.Console markup strings.
/// </summary>
internal static class MarkupHelpers
{
    /// <summary>
    /// Builds a clickable link markup string when the console supports links,
    /// otherwise returns a plain-text fallback.
    /// </summary>
    public static string SafeLink(IInteractionService interactionService, string link, string? title = null)
    {
        if (interactionService.SupportsLinks)
        {
            return $"[link={link}]{(title ?? link).EscapeMarkup()}[/]";
        }

        return title is null ? link : $"{title} ({link})";
    }
}
