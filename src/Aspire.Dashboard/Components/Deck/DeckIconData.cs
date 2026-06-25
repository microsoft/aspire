// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Frozen;

namespace Aspire.Dashboard.Components.Deck;

/// <summary>
/// The single source of truth for the inner SVG markup of each <see cref="DeckIconName"/>.
/// </summary>
/// <remarks>
/// <para>
/// Each value is the inner markup (paths/rects/circles/lines) of a 24x24, stroke-based,
/// <c>currentColor</c> icon, ported from <c>src/Aspire.Deck/ui/src/components/Icons.tsx</c>.
/// </para>
/// <para>
/// Two renderers consume this data so the glyphs stay identical:
/// the <see cref="DeckIcon"/> Blazor component (emits the markup inside an <c>&lt;svg&gt;</c> via
/// <c>MarkupString</c>), and the resource graph (which can't host a Blazor component, so it sets the
/// markup as <c>innerHTML</c> of an SVG group in <c>app-resourcegraph.js</c>). Keeping the markup here
/// — rather than inline in the component — means the graph and the component can never drift.
/// </para>
/// </remarks>
internal static class DeckIconData
{
    // Markup is authored as multi-line verbatim strings to mirror the original component layout and
    // make diffs against Icons.tsx easy to read.
    private static readonly FrozenDictionary<DeckIconName, string> s_innerMarkup = new Dictionary<DeckIconName, string>
    {
        [DeckIconName.Resources] =
            """
            <rect x="3" y="3" width="7" height="7" rx="1.5" />
            <rect x="14" y="3" width="7" height="7" rx="1.5" />
            <rect x="3" y="14" width="7" height="7" rx="1.5" />
            <rect x="14" y="14" width="7" height="7" rx="1.5" />
            """,
        [DeckIconName.Parameters] =
            """
            <line x1="4" y1="8" x2="20" y2="8" />
            <line x1="4" y1="16" x2="20" y2="16" />
            <circle cx="9" cy="8" r="2.4" />
            <circle cx="15" cy="16" r="2.4" />
            """,
        [DeckIconName.Console] =
            """
            <rect x="3" y="4" width="18" height="16" rx="2" />
            <path d="m7 9 3 3-3 3" />
            <path d="M13 15h4" />
            """,
        [DeckIconName.Logs] =
            """
            <path d="M4 6h16" />
            <path d="M4 12h16" />
            <path d="M4 18h10" />
            """,
        [DeckIconName.Traces] =
            """
            <path d="M4 5h16" />
            <path d="M4 5v14" />
            <path d="M8 9h9" />
            <path d="M8 13h6" />
            <path d="M8 17h11" />
            """,
        [DeckIconName.Metrics] =
            """
            <path d="M4 19V5" />
            <path d="M4 15l4-5 4 3 7-8" />
            """,
        [DeckIconName.Graph] =
            """
            <circle cx="6" cy="6" r="2.5" />
            <circle cx="18" cy="6" r="2.5" />
            <circle cx="12" cy="18" r="2.5" />
            <path d="M7.6 7.8 10.6 16M16.4 7.8 13.4 16M8.5 6h7" />
            """,
        [DeckIconName.Project] =
            """
            <rect x="3" y="4" width="18" height="16" rx="2" />
            <path d="M3 9h18" />
            <circle cx="6.5" cy="6.5" r="0.6" fill="currentColor" />
            """,
        [DeckIconName.Container] =
            """
            <path d="M12 3 3 7.5v9L12 21l9-4.5v-9z" />
            <path d="M3 7.5 12 12l9-4.5" />
            <path d="M12 12v9" />
            """,
        [DeckIconName.Executable] =
            """
            <path d="m8 8-4 4 4 4" />
            <path d="m16 8 4 4-4 4" />
            """,
        [DeckIconName.Search] =
            """
            <circle cx="11" cy="11" r="7" />
            <path d="m20 20-3-3" />
            """,
        [DeckIconName.Filter] =
            """
            <path d="M22 3H2l8 9.46V19l4 2v-8.54L22 3z" />
            """,
        [DeckIconName.Play] =
            """
            <path d="m6 4 14 8-14 8z" />
            """,
        [DeckIconName.Pause] =
            """
            <line x1="8" y1="5" x2="8" y2="19" />
            <line x1="16" y1="5" x2="16" y2="19" />
            """,
        [DeckIconName.Stop] =
            """
            <rect x="5" y="5" width="14" height="14" rx="2" />
            """,
        [DeckIconName.Restart] =
            """
            <path d="M21 12a9 9 0 1 1-3-6.7" />
            <path d="M21 3v5h-5" />
            """,
        [DeckIconName.Close] =
            """
            <path d="m6 6 12 12" />
            <path d="m18 6-12 12" />
            """,
        [DeckIconName.External] =
            """
            <path d="M14 4h6v6" />
            <path d="M20 4 10 14" />
            <path d="M19 14v5a1 1 0 0 1-1 1H5a1 1 0 0 1-1-1V6a1 1 0 0 1 1-1h5" />
            """,
        [DeckIconName.Eye] =
            """
            <path d="M2 12s3.5-7 10-7 10 7 10 7-3.5 7-10 7-10-7-10-7z" />
            <circle cx="12" cy="12" r="2.5" />
            """,
        [DeckIconName.EyeOff] =
            """
            <path d="M10.6 6.2A9.6 9.6 0 0 1 12 6c6.5 0 10 6 10 6a16 16 0 0 1-3 3.6" />
            <path d="M6.2 6.2A16 16 0 0 0 2 12s3.5 7 10 7a9.6 9.6 0 0 0 4-.9" />
            <path d="m2 2 20 20" />
            """,
        [DeckIconName.Sun] =
            """
            <circle cx="12" cy="12" r="4" />
            <path d="M12 2v2M12 20v2M2 12h2M20 12h2M4.9 4.9l1.4 1.4M17.7 17.7l1.4 1.4M4.9 19.1l1.4-1.4M17.7 6.3l1.4-1.4" />
            """,
        [DeckIconName.Moon] =
            """
            <path d="M21 12.8A9 9 0 1 1 11.2 3a7 7 0 0 0 9.8 9.8z" />
            """,
        [DeckIconName.Back] =
            """
            <path d="m15 18-6-6 6-6" />
            """,
        [DeckIconName.Link] =
            """
            <path d="M9 12h6" />
            <path d="M10 17H7a5 5 0 0 1 0-10h3" />
            <path d="M14 7h3a5 5 0 0 1 0 10h-3" />
            """,
        [DeckIconName.Chevron] =
            """
            <path d="m9 18 6-6-6-6" />
            """,
        [DeckIconName.Settings] =
            """
            <circle cx="12" cy="12" r="3" />
            <path d="M19.4 15a1.65 1.65 0 0 0 .33 1.82l.06.06a2 2 0 1 1-2.83 2.83l-.06-.06a1.65 1.65 0 0 0-1.82-.33 1.65 1.65 0 0 0-1 1.51V21a2 2 0 0 1-4 0v-.09A1.65 1.65 0 0 0 9 19.4a1.65 1.65 0 0 0-1.82.33l-.06.06a2 2 0 1 1-2.83-2.83l.06-.06a1.65 1.65 0 0 0 .33-1.82 1.65 1.65 0 0 0-1.51-1H3a2 2 0 0 1 0-4h.09A1.65 1.65 0 0 0 4.6 9a1.65 1.65 0 0 0-.33-1.82l-.06-.06a2 2 0 1 1 2.83-2.83l.06.06a1.65 1.65 0 0 0 1.82.33H9a1.65 1.65 0 0 0 1-1.51V3a2 2 0 0 1 4 0v.09a1.65 1.65 0 0 0 1 1.51 1.65 1.65 0 0 0 1.82-.33l.06-.06a2 2 0 1 1 2.83 2.83l-.06.06a1.65 1.65 0 0 0-.33 1.82V9a1.65 1.65 0 0 0 1.51 1H21a2 2 0 0 1 0 4h-.09a1.65 1.65 0 0 0-1.51 1z" />
            """,
        [DeckIconName.Bell] =
            """
            <path d="M18 8a6 6 0 0 0-12 0c0 7-3 9-3 9h18s-3-2-3-9" />
            <path d="M13.7 21a2 2 0 0 1-3.4 0" />
            """,
        [DeckIconName.Help] =
            """
            <circle cx="12" cy="12" r="10" />
            <path d="M9.1 9a3 3 0 0 1 5.8 1c0 2-3 3-3 3" />
            <path d="M12 17h.01" />
            """,
        [DeckIconName.GitHub] =
            """
            <path d="M9 19c-5 1.5-5-2.5-7-3m14 6v-3.9a3.4 3.4 0 0 0-1-2.6c3-.3 6-1.5 6-6.6a5.1 5.1 0 0 0-1.4-3.5 4.8 4.8 0 0 0-.1-3.5s-1.1-.3-3.6 1.4a12.3 12.3 0 0 0-6.4 0C6 .9 4.9 1.2 4.9 1.2a4.8 4.8 0 0 0-.1 3.5A5.1 5.1 0 0 0 3.4 8.2c0 5 3 6.3 6 6.6a3.4 3.4 0 0 0-1 2.6V21" />
            """,
        [DeckIconName.Warning] =
            """
            <path d="M10.29 3.86 1.82 18a2 2 0 0 0 1.71 3h16.94a2 2 0 0 0 1.71-3L13.71 3.86a2 2 0 0 0-3.42 0z" />
            <line x1="12" y1="9" x2="12" y2="13" />
            <line x1="12" y1="17" x2="12.01" y2="17" />
            """,
        [DeckIconName.ErrorCircle] =
            """
            <circle cx="12" cy="12" r="10" />
            <line x1="12" y1="8" x2="12" y2="12" />
            <line x1="12" y1="16" x2="12.01" y2="16" />
            """,
        [DeckIconName.Sparkle] =
            """
            <path d="M12 3l1.6 5.1a2 2 0 0 0 1.3 1.3L20 11l-5.1 1.6a2 2 0 0 0-1.3 1.3L12 19l-1.6-5.1a2 2 0 0 0-1.3-1.3L4 11l5.1-1.6a2 2 0 0 0 1.3-1.3z" />
            <path d="M19 3v3" />
            <path d="M20.5 4.5h-3" />
            """,
        [DeckIconName.ChartMultiple] =
            """
            <path d="M3 3v18h18" />
            <path d="M18 17V9" />
            <path d="M13 17V5" />
            <path d="M8 17v-3" />
            """,
        [DeckIconName.GanttChart] =
            """
            <path d="M3 5v14" />
            <path d="M8 7h10" />
            <path d="M6 12h9" />
            <path d="M11 17h7" />
            """,
        [DeckIconName.DocumentError] =
            """
            <path d="M17 21H7a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h7l5 5v11a2 2 0 0 1-2 2z" />
            <path d="M14 3v4a1 1 0 0 0 1 1h4" />
            <line x1="12" y1="11" x2="12" y2="15" />
            <line x1="12" y1="18" x2="12.01" y2="18" />
            """,
        [DeckIconName.SlideSearch] =
            """
            <rect x="3" y="4" width="18" height="14" rx="2" />
            <circle cx="11" cy="11" r="2.5" />
            <path d="m13 13 2.5 2.5" />
            """,
        [DeckIconName.AppGeneric] =
            """
            <rect x="2" y="4" width="20" height="16" rx="2" />
            <path d="M2 9h20" />
            <path d="M6 4v5" />
            <path d="M10 4v5" />
            """,
        [DeckIconName.Pin] =
            """
            <path d="M12 17v5" />
            <path d="M9 10.8a2 2 0 0 1-1.1 1.8l-1.8.9A2 2 0 0 0 5 15.2V16a1 1 0 0 0 1 1h12a1 1 0 0 0 1-1v-.8a2 2 0 0 0-1.1-1.8l-1.8-.9A2 2 0 0 1 15 10.8V7a1 1 0 0 1 1-1 2 2 0 0 0 0-4H8a2 2 0 0 0 0 4 1 1 0 0 1 1 1z" />
            """,
        [DeckIconName.ArrowDown] =
            """
            <path d="M12 5v14" />
            <path d="m19 12-7 7-7-7" />
            """,
        [DeckIconName.ArrowTurnDownRight] =
            """
            <path d="m15 10 5 5-5 5" />
            <path d="M4 4v7a4 4 0 0 0 4 4h12" />
            """,
        [DeckIconName.AppsList] =
            """
            <rect x="3" y="4" width="7" height="7" rx="1" />
            <rect x="3" y="14" width="7" height="6" rx="1" />
            <path d="M14 6h7" />
            <path d="M14 10h7" />
            <path d="M14 16h7" />
            <path d="M14 19h7" />
            """,
        [DeckIconName.Server] =
            """
            <rect x="2" y="3" width="20" height="8" rx="2" />
            <rect x="2" y="13" width="20" height="8" rx="2" />
            <line x1="6" y1="7" x2="6.01" y2="7" />
            <line x1="6" y1="17" x2="6.01" y2="17" />
            """,
        [DeckIconName.Mail] =
            """
            <rect x="2" y="4" width="20" height="16" rx="2" />
            <path d="m22 7-10 5L2 7" />
            """,
        // database cylinder (lucide "database").
        [DeckIconName.Database] =
            """
            <ellipse cx="12" cy="5" rx="9" ry="3" />
            <path d="M3 5v14a9 3 0 0 0 18 0V5" />
            <path d="M3 12a9 3 0 0 0 18 0" />
            """,
        // heart (lucide "heart") — used for a healthy health status.
        [DeckIconName.Heart] =
            """
            <path d="M19 14c1.49-1.46 3-3.21 3-5.5A5.5 5.5 0 0 0 16.5 3c-1.76 0-3 .5-4.5 2-1.5-1.5-2.74-2-4.5-2A5.5 5.5 0 0 0 2 8.5c0 2.3 1.5 4.05 3 5.5l7 7Z" />
            """,
        // heart with a crack (lucide "heart-crack") — degraded/unhealthy health status.
        [DeckIconName.HeartBroken] =
            """
            <path d="M19 14c1.49-1.46 3-3.21 3-5.5A5.5 5.5 0 0 0 16.5 3c-1.76 0-3 .5-4.5 2-1.5-1.5-2.74-2-4.5-2A5.5 5.5 0 0 0 2 8.5c0 2.3 1.5 4.05 3 5.5l7 7Z" />
            <path d="m12 13-1-1 2-2-3-3 2-2" />
            """,
        // hollow circle — unknown/indeterminate health status.
        [DeckIconName.CircleHint] =
            """
            <circle cx="12" cy="12" r="9" />
            """,
        // circle with an "i" (lucide "info").
        [DeckIconName.Info] =
            """
            <circle cx="12" cy="12" r="10" />
            <path d="M12 16v-4" />
            <path d="M12 8h.01" />
            """,
        // curly braces (lucide "braces").
        [DeckIconName.Braces] =
            """
            <path d="M8 3H7a2 2 0 0 0-2 2v5a2 2 0 0 1-2 2 2 2 0 0 1 2 2v5a2 2 0 0 0 2 2h1" />
            <path d="M16 3h1a2 2 0 0 1 2 2v5a2 2 0 0 0 2 2 2 2 0 0 0-2 2v5a2 2 0 0 1-2 2h-1" />
            """,
        // document with text lines (lucide "file-text").
        [DeckIconName.DocumentText] =
            """
            <path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z" />
            <path d="M14 2v6h6" />
            <path d="M16 13H8" />
            <path d="M16 17H8" />
            <path d="M10 9H8" />
            """,
        // toolbox — resource commands menu.
        [DeckIconName.Toolbox] =
            """
            <rect x="2" y="7" width="20" height="14" rx="2" />
            <path d="M8 7V5a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2" />
            <path d="M2 13h20" />
            <path d="M10 13v2" />
            <path d="M14 13v2" />
            """,
        // downward chevron (lucide "chevron-down").
        [DeckIconName.ChevronDown] =
            """
            <path d="m6 9 6 6 6-6" />
            """,
        // three horizontal dots (lucide "more-horizontal"). Dots are filled.
        [DeckIconName.MoreHorizontal] =
            """
            <circle cx="12" cy="12" r="1" fill="currentColor" />
            <circle cx="19" cy="12" r="1" fill="currentColor" />
            <circle cx="5" cy="12" r="1" fill="currentColor" />
            """,
        // horizontal sliders (lucide "sliders-horizontal") — options menu.
        [DeckIconName.Options] =
            """
            <line x1="21" y1="4" x2="14" y2="4" />
            <line x1="10" y1="4" x2="3" y2="4" />
            <line x1="21" y1="12" x2="12" y2="12" />
            <line x1="8" y1="12" x2="3" y2="12" />
            <line x1="21" y1="20" x2="16" y2="20" />
            <line x1="12" y1="20" x2="3" y2="20" />
            <line x1="14" y1="2" x2="14" y2="6" />
            <line x1="8" y1="10" x2="8" y2="14" />
            <line x1="16" y1="18" x2="16" y2="22" />
            """,
        // trash can (lucide "trash-2").
        [DeckIconName.Delete] =
            """
            <path d="M3 6h18" />
            <path d="M19 6v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6" />
            <path d="M8 6V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2" />
            <line x1="10" y1="11" x2="10" y2="17" />
            <line x1="14" y1="11" x2="14" y2="17" />
            """,
        // checkmark (lucide "check").
        [DeckIconName.Checkmark] =
            """
            <path d="M20 6 9 17l-5-5" />
            """,
        // double down chevron — expand all.
        [DeckIconName.ExpandAll] =
            """
            <path d="m7 6 5 5 5-5" />
            <path d="m7 13 5 5 5-5" />
            """,
        // double up chevron — collapse all.
        [DeckIconName.CollapseAll] =
            """
            <path d="m7 11 5-5 5 5" />
            <path d="m7 18 5-5 5 5" />
            """,
        // down arrow into a tray (lucide "download").
        [DeckIconName.Download] =
            """
            <path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4" />
            <path d="M7 10l5 5 5-5" />
            <path d="M12 15V3" />
            """,
        // clock (lucide "clock").
        [DeckIconName.Clock] =
            """
            <circle cx="12" cy="12" r="9" />
            <path d="M12 7v5l3 2" />
            """,
        // checked checkbox (lucide "square-check").
        [DeckIconName.CheckboxChecked] =
            """
            <rect x="3" y="3" width="18" height="18" rx="2" />
            <path d="m8 12 3 3 5-6" />
            """,
        // empty checkbox (lucide "square").
        [DeckIconName.CheckboxUnchecked] =
            """
            <rect x="3" y="3" width="18" height="18" rx="2" />
            """,
        // checkbox with a dash (lucide "square-minus") — indeterminate.
        [DeckIconName.CheckboxIndeterminate] =
            """
            <rect x="3" y="3" width="18" height="18" rx="2" />
            <path d="M8 12h8" />
            """,
        // wrapping arrow (lucide "wrap-text") — toggle line wrapping.
        [DeckIconName.TextWrap] =
            """
            <path d="M3 6h18" />
            <path d="M3 12h15a3 3 0 1 1 0 6h-4" />
            <path d="m16 16-2 2 2 2" />
            <path d="M3 18h7" />
            """,
        // overlapping pages (lucide "copy").
        [DeckIconName.Copy] =
            """
            <rect x="9" y="9" width="13" height="13" rx="2" />
            <path d="M5 15H4a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2h9a2 2 0 0 1 2 2v1" />
            """,
        // stacked layers (lucide "layers").
        [DeckIconName.Stack] =
            """
            <path d="m12 2 9 5-9 5-9-5 9-5z" />
            <path d="m3 12 9 5 9-5" />
            <path d="m3 17 9 5 9-5" />
            """,
        // person silhouette (lucide "user").
        [DeckIconName.Person] =
            """
            <circle cx="12" cy="8" r="4" />
            <path d="M4 21a8 8 0 0 1 16 0" />
            """,
        // checkmark inside a circle (lucide "circle-check") — success.
        [DeckIconName.CheckmarkCircle] =
            """
            <circle cx="12" cy="12" r="10" />
            <path d="m8 12 3 3 5-6" />
            """,
        // up arrow inside a circle (lucide "circle-arrow-up").
        [DeckIconName.ArrowCircleUp] =
            """
            <circle cx="12" cy="12" r="10" />
            <path d="m8 12 4-4 4 4" />
            <path d="M12 16V8" />
            """,
        // down arrow inside a circle (lucide "circle-arrow-down").
        [DeckIconName.ArrowCircleDown] =
            """
            <circle cx="12" cy="12" r="10" />
            <path d="m8 12 4 4 4-4" />
            <path d="M12 8v8" />
            """,
        // right arrow inside a circle (lucide "circle-arrow-right").
        [DeckIconName.ArrowCircleRight] =
            """
            <circle cx="12" cy="12" r="10" />
            <path d="m12 8 4 4-4 4" />
            <path d="M8 12h8" />
            """,
    }.ToFrozenDictionary();

    /// <summary>
    /// Gets the inner SVG markup for <paramref name="name"/>. Falls back to the
    /// <see cref="DeckIconName.Resources"/> grid glyph for any unmapped value, matching
    /// <see cref="DeckIcon"/>'s default case.
    /// </summary>
    public static string GetInnerMarkup(DeckIconName name)
    {
        return s_innerMarkup.TryGetValue(name, out var markup)
            ? markup
            : s_innerMarkup[DeckIconName.Resources];
    }
}
