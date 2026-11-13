// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Interaction;

/// <summary>
/// Represents an emoji by its Spectre.Console name, with an optional fallback color
/// for terminals that render the emoji as a monochrome text character.
/// </summary>
internal readonly struct KnownEmoji(string name, string? textColor = null)
{
    /// <summary>
    /// Gets the Spectre.Console emoji name (e.g. "rocket", "check_mark").
    /// </summary>
    public string Name { get; } = name;

    /// <summary>
    /// Gets the Spectre.Console color name applied when the emoji is rendered as a
    /// monochrome text character. ANSI foreground color is ignored by terminals that
    /// render full-color emoji glyphs, so this is always safe to apply.
    /// When <see langword="null"/>, no color is applied.
    /// </summary>
    public string? TextColor { get; } = textColor;
}

/// <summary>
/// Defines all emoji values used by the Aspire CLI. Prefer using existing known emojis when possible, but add new ones here as needed.
/// This allows the CLI to have consistent UI for common operations while adding new emojis relevant to new tasks when required.
/// </summary>
/// <remarks>
/// Prefer characters with default emoji presentation, such as <c>inbox_tray</c>, for new icons.
/// Do not use <c>down_arrow</c>: Spectre.Console emits U+2B07, whose default presentation is text.
/// Adding U+FE0F (VS16) requests an emoji glyph, but Windows Terminal can use the selector for
/// character width without changing glyph presentation. Other text-default symbols can have
/// the same problem even if they look correct in another terminal.
/// See <see href="https://www.unicode.org/reports/tr51/#def_emoji_presentation_selector">Unicode emoji presentation selectors</see>
/// and <see href="https://github.com/microsoft/terminal/issues/19100">microsoft/terminal#19100</see>.
/// </remarks>
internal static class KnownEmojis
{
    public static readonly KnownEmoji Bug = new("bug", "red");
    public static readonly KnownEmoji BarChart = new("bar_chart", "blue");
    public static readonly KnownEmoji CheckMarkButton = new("check_mark_button", "green");
    public static readonly KnownEmoji CrossMark = new("cross_mark", "red");
    public static readonly KnownEmoji FileFolder = new("file_folder", "yellow");
    public static readonly KnownEmoji FloppyDisk = new("floppy_disk", "blue");
    public static readonly KnownEmoji Gear = new("gear");
    public static readonly KnownEmoji Hammer = new("hammer");
    public static readonly KnownEmoji Ice = new("ice", "cyan");
    public static readonly KnownEmoji HammerAndWrench = new("hammer_and_wrench");
    public static readonly KnownEmoji InboxTray = new("inbox_tray", "blue");
    public static readonly KnownEmoji Information = new("information", "blue");
    public static readonly KnownEmoji Key = new("key", "yellow");
    public static readonly KnownEmoji LinkedPaperclips = new("linked_paperclips");
    public static readonly KnownEmoji LockedWithKey = new("locked_with_key", "yellow");
    public static readonly KnownEmoji MagnifyingGlassTiltedLeft = new("magnifying_glass_tilted_left", "blue");
    public static readonly KnownEmoji Microscope = new("microscope", "blue");
    public static readonly KnownEmoji Package = new("package", "yellow");
    public static readonly KnownEmoji PageFacingUp = new("page_facing_up", "white");
    public static readonly KnownEmoji Robot = new("robot", "blue");
    public static readonly KnownEmoji Rocket = new("rocket", "darkorange");
    public static readonly KnownEmoji RunningShoe = new("running_shoe", "green");
    public static readonly KnownEmoji Dizzy = new("dizzy");
    public static readonly KnownEmoji StopSign = new("stop_sign", "red");
    public static readonly KnownEmoji UpButton = new("up_button", "blue");
    // The warning emoji renders inconsistently across terminals, so use a police car light for warnings instead.
    public static readonly KnownEmoji PoliceCarLight = new("police_car_light", "red");
    public static readonly KnownEmoji Wrench = new("wrench");
}
