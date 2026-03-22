// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Interaction;

/// <summary>
/// Represents an emoji by its Spectre.Console name.
/// </summary>
internal readonly struct KnownEmoji(string name)
{
    /// <summary>
    /// Gets the Spectre.Console emoji name (e.g. "rocket", "check_mark").
    /// </summary>
    public string Name { get; } = name;
}

/// <summary>
/// Defines all emoji values used by the Aspire CLI. Prefer using existing known emojis when possible, but add new ones here as needed.
/// This allows the CLI to have consistent UI for common operations while adding new emojis relevant to new tasks when required.
/// </summary>
internal static class KnownEmojis
{
    public static readonly KnownEmoji Bug = new("bug");
    public static readonly KnownEmoji CheckBoxWithCheck = new("check_box_with_check");
    public static readonly KnownEmoji CheckMark = new("check_mark");
    public static readonly KnownEmoji CrossMark = new("cross_mark");
    public static readonly KnownEmoji FileCabinet = new("file_cabinet");
    public static readonly KnownEmoji FileFolder = new("file_folder");
    public static readonly KnownEmoji FloppyDisk = new("floppy_disk");
    public static readonly KnownEmoji Gear = new("gear");
    public static readonly KnownEmoji Hammer = new("hammer");
    public static readonly KnownEmoji Ice = new("ice");
    public static readonly KnownEmoji HammerAndWrench = new("hammer_and_wrench");
    public static readonly KnownEmoji Information = new("information");
    public static readonly KnownEmoji Key = new("key");
    public static readonly KnownEmoji LinkedPaperclips = new("linked_paperclips");
    public static readonly KnownEmoji LockedWithKey = new("locked_with_key");
    public static readonly KnownEmoji MagnifyingGlassTiltedLeft = new("magnifying_glass_tilted_left");
    public static readonly KnownEmoji MagnifyingGlassTiltedRight = new("magnifying_glass_tilted_right");
    public static readonly KnownEmoji Microscope = new("microscope");
    public static readonly KnownEmoji Package = new("package");
    public static readonly KnownEmoji PageFacingUp = new("page_facing_up");
    public static readonly KnownEmoji Rocket = new("rocket");
    public static readonly KnownEmoji RunningShoe = new("running_shoe");
    public static readonly KnownEmoji StopSign = new("stop_sign");
    public static readonly KnownEmoji UpButton = new("up_button");
    public static readonly KnownEmoji Warning = new("warning");
    public static readonly KnownEmoji Wrench = new("wrench");
}
