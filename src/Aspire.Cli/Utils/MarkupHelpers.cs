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
        return SafeLink(interactionService.SupportsLinks, link, title);
    }

    /// <summary>
    /// Builds a clickable link markup string when the console supports links,
    /// otherwise returns a plain-text fallback.
    /// </summary>
    public static string SafeLink(bool supportsLinks, string link, string? title = null)
    {
        var noTitle = title is null || title == link;
        link = link.EscapeMarkup();
        title = title?.EscapeMarkup();

        if (supportsLinks)
        {
            return noTitle ? $"[link]{link}[/]" : $"[link={link}]{title}[/]";
        }

        return noTitle ? link : $"{title} ({link})";
    }

    /// <summary>
    /// Builds a clickable file-link markup string for the specified file path
    /// when the console supports links, otherwise returns a plain-text fallback.
    /// The displayed text is the original <paramref name="filePath"/> and the
    /// link target is a <c>file://</c> URI built from the absolute path.
    /// </summary>
    public static string SafeFileLink(IInteractionService interactionService, string filePath)
    {
        return SafeFileLink(interactionService.SupportsLinks, filePath);
    }

    /// <summary>
    /// Builds a clickable file-link markup string for the specified file path
    /// when the console supports links, otherwise returns a plain-text fallback.
    /// The displayed text is the original <paramref name="filePath"/> and the
    /// link target is a <c>file://</c> URI built from the absolute path.
    /// </summary>
    public static string SafeFileLink(bool supportsLinks, string filePath)
    {
        return SafeFileLink(supportsLinks, filePath, displayName: null);
    }

    /// <summary>
    /// Builds a clickable file-link markup string for the specified file path
    /// when the console supports links, otherwise returns a plain-text fallback.
    /// The displayed text is <paramref name="displayName"/> (or the full path when
    /// <see langword="null"/>). If the path cannot be resolved to a valid URI
    /// (e.g. it came from an external process and is malformed), the display name
    /// is returned as escaped plain text.
    /// </summary>
    public static string SafeFileLink(IInteractionService interactionService, string filePath, string? displayName)
    {
        return SafeFileLink(interactionService.SupportsLinks, filePath, displayName);
    }

    /// <summary>
    /// Builds a clickable file-link markup string for the specified file path
    /// when the console supports links, otherwise returns a plain-text fallback.
    /// The displayed text is <paramref name="displayName"/> (or the full path when
    /// <see langword="null"/>). If the path cannot be resolved to a valid URI
    /// (e.g. it came from an external process and is malformed), the display name
    /// is returned as escaped plain text.
    /// </summary>
    public static string SafeFileLink(bool supportsLinks, string filePath, string? displayName)
    {
        if (string.IsNullOrEmpty(filePath))
        {
            return string.Empty;
        }

        displayName ??= filePath;

        if (!supportsLinks)
        {
            return displayName.EscapeMarkup();
        }

        try
        {
            var fileUri = new Uri(Path.GetFullPath(filePath)).AbsoluteUri;
            return SafeLink(supportsLinks: true, fileUri, displayName);
        }
        catch (Exception)
        {
            // The path may come from an external process (e.g. via backchannel) and
            // could be malformed. Fall back to plain escaped text so the error-display
            // path in BaseCommand doesn't throw and mask the original failure.
            return displayName.EscapeMarkup();
        }
    }
}
