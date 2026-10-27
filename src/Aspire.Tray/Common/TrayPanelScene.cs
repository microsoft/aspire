// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Tray;

/// <summary>
/// The text styles of the panel. Renderers map each role to the system UI font at the
/// same size in DIPs (SF on macOS, Segoe UI Variable Text on Windows) without embedding a font.
/// </summary>
internal enum PanelFontRole
{
    /// <summary>15 semibold: the panel title.</summary>
    Title,
    /// <summary>13 semibold: AppHost names and the empty-state title.</summary>
    Name,
    /// <summary>12 regular: status lines, subtitles, banner and button labels.</summary>
    Body,
    /// <summary>11 semibold: uppercase section headers.</summary>
    Section,
    /// <summary>12 semibold: the labels of text buttons in confirmation strips.</summary>
    Button
}

internal static class PanelFonts
{
    public static double Size(PanelFontRole role) => role switch
    {
        PanelFontRole.Title => 15,
        PanelFontRole.Name => 13,
        PanelFontRole.Section => 11,
        _ => 12
    };

    public static bool IsSemibold(PanelFontRole role) => role is not PanelFontRole.Body;

    /// <summary>
    /// The height of capital letters. SF Pro and Segoe UI both draw capitals 0.70 em tall, so
    /// centering capitals on a line puts the baseline half of this below the line's center.
    /// </summary>
    public static double CapHeight(PanelFontRole role) => 0.7 * Size(role);
}

internal enum PanelTextAlignment
{
    Leading,
    Center,
    Trailing
}

internal enum PanelTruncation
{
    End,
    Middle
}

/// <summary>One drawing instruction. Commands are drawn in order, painter's algorithm.</summary>
internal abstract record PanelCommand;

internal sealed record FillPathCommand(PanelPath Path, PanelColor Color) : PanelCommand;

/// <summary>Strokes a path with round caps and round joins.</summary>
internal sealed record StrokePathCommand(PanelPath Path, PanelColor Color, double Width) : PanelCommand;

/// <summary>
/// Draws text inside fixed bounds. The layout never measures text: a renderer truncates with
/// an ellipsis to fit. Single-line text sits on <see cref="Baseline"/>, which by default centers
/// its capitals on the bounds' middle; renderers place the baseline there exactly instead of
/// centering the font's line box, because SF and Segoe UI have different ascents and descents.
/// Multi-line text wraps at word boundaries and is top-aligned.
/// </summary>
internal sealed record TextCommand(
    PanelRect Bounds,
    string Text,
    PanelFontRole Font,
    PanelColor Color,
    PanelTextAlignment Alignment = PanelTextAlignment.Leading,
    int MaxLines = 1,
    PanelTruncation Truncation = PanelTruncation.End,
    double? ExplicitBaseline = null) : PanelCommand
{
    /// <summary>
    /// The y of a single line's baseline: <see cref="ExplicitBaseline"/> when set (text sharing a
    /// baseline with larger text beside it), otherwise the one that centers the capitals.
    /// </summary>
    public double Baseline => ExplicitBaseline ?? Bounds.CenterY + PanelFonts.CapHeight(Font) / 2;
}

/// <summary>Draws the full-color Aspire logo scaled into the bounds.</summary>
internal sealed record LogoCommand(PanelRect Bounds) : PanelCommand;

/// <summary>
/// Draws the icon of an installed application, one of <see cref="TrayPanel.OpenInApplications"/>,
/// scaled into the bounds. Hosts look the icon up by <see cref="Application"/> title; when they
/// have none, they draw <see cref="Fallback"/> instead, a generic application glyph.
/// </summary>
internal sealed record AppIconCommand(PanelRect Bounds, string Application, IReadOnlyList<PanelCommand> Fallback) : PanelCommand;

/// <summary>Intersects the clip with a rectangle until the matching <see cref="PopClipCommand"/>.</summary>
internal sealed record PushClipCommand(PanelRect Bounds) : PanelCommand;

internal sealed record PopClipCommand : PanelCommand;

internal enum PanelTargetKind
{
    Row,
    Dashboard,
    Stop,
    Start,
    More,
    ToggleRecent,
    ClearRecent,
    Documentation,
    Settings,
    Quit,
    /// <summary>The Cancel button of an inline confirmation strip.</summary>
    ConfirmCancel,
    /// <summary>The destructive button of an inline confirmation strip (Clear, Remove).</summary>
    ConfirmAccept,
    /// <summary>The back arrow in the header of the Settings view.</summary>
    Back,
    /// <summary>The "Launch at sign-in" toggle row of the Settings view.</summary>
    LaunchAtSignIn,
    /// <summary>
    /// The "Open in" item of a row's action list, which shows or hides the applications under it.
    /// </summary>
    OpenInMenu,
    /// <summary>
    /// An application under "Open in". <see cref="PanelTargetKey.Item"/> indexes
    /// <see cref="TrayPanel.OpenInApplications"/>.
    /// </summary>
    OpenIn,
    /// <summary>The "Show in Finder" or "Show in File Explorer" item of a row's action list.</summary>
    ShowInFolder,
    /// <summary>The "Copy path" item of a row's action list.</summary>
    CopyPath,
    /// <summary>The "Pin" or "Unpin" item of a row's action list.</summary>
    TogglePin,
    /// <summary>The "Remove from recent" item of a recent row's action list.</summary>
    RemoveRecent
}

/// <summary>The inline confirmation a panel is showing in place of a row or section header.</summary>
internal enum PanelConfirmKind
{
    /// <summary>Clear the recent list; replaces the Recent section header.</summary>
    ClearRecent,
    /// <summary>Remove a recent AppHost whose source file is gone; replaces its row.</summary>
    RemoveMissing
}

internal enum PanelSection
{
    None,
    Running,
    Pinned,
    Recent
}

/// <summary>
/// Identifies a hit target across scene rebuilds, so hover, press and focus survive a
/// refresh even when rows move. The same AppHost can be listed only once per section.
/// <see cref="Item"/> tells apart targets of the same kind for one row, such as the
/// "Open in" items of its action list.
/// </summary>
internal readonly record struct PanelTargetKey(PanelTargetKind Kind, PanelSection Section, AppHostId? Host, int Item = 0);

/// <summary>
/// How a screen reader presents an element. Hosts map it to the platform role: an AXButton or
/// UIA Button, an AXCheckBox with the AXSwitch subrole or a UIA CheckBox with the Toggle pattern,
/// and AXStaticText or a UIA Text element.
/// </summary>
internal enum PanelAccessibleRole
{
    Button,
    /// <summary>An on/off control. Its state is <see cref="PanelHitTarget.Checked"/>, not part of its name.</summary>
    Switch,
    /// <summary>Read-only text: the header status, a notice, a section title or a description.</summary>
    Text
}

/// <summary>
/// An interactive region. <see cref="Bounds"/> is where it is drawn and <see cref="HitBounds"/>
/// is the part that is visible through the list clip, which is what pointer input tests.
/// Icon-only buttons carry a <see cref="Tooltip"/>, which hosts show as a native tooltip over
/// <see cref="HitBounds"/>; buttons with a visible label have none.
/// </summary>
internal sealed record PanelHitTarget(
    PanelTargetKey Key,
    PanelRect Bounds,
    PanelRect HitBounds,
    string AccessibleName,
    bool Enabled,
    AppHostMenuItem? Host,
    PanelAccessibleRole Role = PanelAccessibleRole.Button,
    bool? Checked = null,
    string? Tooltip = null)
{
    public PanelTargetKind Kind => Key.Kind;
}

/// <summary>
/// Identifies an accessible element across scene rebuilds, so a host can keep the same native
/// element (and a screen reader its cursor on it) while the panel redraws. Targets use their
/// <see cref="PanelTargetKey"/>; text uses a stable label such as "header.status".
/// </summary>
internal readonly record struct PanelAccessibleKey(PanelTargetKey? Target, string? Label)
{
    public static PanelAccessibleKey For(PanelTargetKey target) => new(target, null);
    public static PanelAccessibleKey For(string label) => new(null, label);
}

/// <summary>
/// One element of the panel's accessibility tree, in reading order. Interactive elements carry
/// their <see cref="Target"/>; text has none. <see cref="VisibleBounds"/> is empty when the
/// element is scrolled out of the list, which hosts report as offscreen.
/// </summary>
internal sealed record PanelAccessibleElement(
    PanelAccessibleKey Key,
    PanelAccessibleRole Role,
    string Name,
    PanelRect Bounds,
    PanelRect VisibleBounds,
    PanelHitTarget? Target = null,
    string? Description = null)
{
    public bool Enabled => Target?.Enabled ?? true;
    public bool? Checked => Target?.Checked;
    public bool IsOffscreen => VisibleBounds.IsEmpty;
}

/// <summary>
/// The output of one layout pass: the panel size, what to draw, where to click, and what a
/// screen reader reads, in reading order (header, list, footer).
/// </summary>
internal sealed record TrayPanelScene(
    double Width,
    double Height,
    IReadOnlyList<PanelCommand> Commands,
    IReadOnlyList<PanelHitTarget> Targets,
    IReadOnlyList<PanelAccessibleElement> Accessibility)
{
    /// <summary>
    /// The alignment grid the layout intends, for tests and screenshot overlays: the columns,
    /// the vertical center of each visible element, and the boxes glyphs should fill. Renderers
    /// ignore it.
    /// </summary>
    public IReadOnlyList<PanelGuide> Guides { get; init; } = [];
}

internal enum PanelGuideKind
{
    /// <summary>A vertical line at <see cref="PanelGuide.Position"/> (x), spanning <see cref="PanelGuide.Bounds"/>.</summary>
    Column,
    /// <summary>
    /// A horizontal line at <see cref="PanelGuide.Position"/> (y), spanning <see cref="PanelGuide.Bounds"/>: where
    /// the vertical center of the elements on that line should be.
    /// </summary>
    Center,
    /// <summary>The box a glyph, icon or logo should fill, centered on its column and line.</summary>
    Box,
    /// <summary>A horizontal line at <see cref="PanelGuide.Position"/> (y) where text on that line shares its baseline.</summary>
    Baseline
}

/// <summary>One line or box of the alignment grid. <see cref="Label"/> names it in overlays and test failures.</summary>
internal sealed record PanelGuide(PanelGuideKind Kind, double Position, string Label, PanelRect Bounds);

internal enum PanelActionKind
{
    OpenDashboard,
    /// <summary>Stop the AppHost now. Stopping isn't confirmed, as in the dashboard.</summary>
    Stop,
    /// <summary>
    /// Start a pinned or recent AppHost. When its source file is gone, the host calls
    /// <see cref="TrayPanel.ConfirmRemoveMissing"/> instead of showing a dialog.
    /// </summary>
    Start,
    /// <summary>
    /// Open the AppHost's folder in <see cref="PanelAction.Application"/>, one of
    /// <see cref="TrayPanel.OpenInApplications"/>.
    /// </summary>
    OpenIn,
    /// <summary>Reveal the AppHost file in Finder or File Explorer.</summary>
    ShowInFolder,
    /// <summary>Copy the path of the folder that contains the AppHost.</summary>
    CopyPath,
    /// <summary>Pin the AppHost, or unpin it when it is already pinned.</summary>
    TogglePin,
    /// <summary>Clear the recent list. The panel has already confirmed it inline.</summary>
    ClearRecent,
    /// <summary>
    /// Remove one entry from the recent list: picked from its action list, or confirmed inline
    /// after its source file was found missing.
    /// </summary>
    RemoveRecent,
    ToggleRecent,
    Documentation,
    /// <summary>
    /// The gear was clicked. The host reads the startup settings and calls
    /// <see cref="TrayPanel.ShowSettings"/> to switch the panel to its Settings view.
    /// </summary>
    Settings,
    /// <summary>Turn launch at sign-in on or off, per <see cref="PanelAction.Enabled"/>.</summary>
    SetLaunchAtSignIn,
    Quit,
    Close
}

/// <summary>
/// What the platform host should do in response to input. <see cref="Anchor"/> is the
/// rectangle in panel coordinates of the control that produced it.
/// </summary>
internal sealed record PanelAction(PanelActionKind Kind, AppHostMenuItem? Host = null, PanelRect Anchor = default)
{
    /// <summary>True when the action is for an entry of the recent list rather than a current AppHost.</summary>
    public bool Recent { get; init; }

    /// <summary>For the Settings toggles: the value the user asked for.</summary>
    public bool Enabled { get; init; }

    /// <summary>For <see cref="PanelActionKind.OpenIn"/>: the title of the application to open the folder in.</summary>
    public string? Application { get; init; }
}

/// <summary>The result of an input event: whether the panel must be redrawn, and an action to run.</summary>
internal readonly record struct PanelResponse(bool Redraw, PanelAction? Action = null)
{
    public static PanelResponse None => default;
    public static PanelResponse Changed => new(true);
}

internal enum PanelKey
{
    Up,
    Down,
    Tab,
    ShiftTab,
    Enter,
    Space,
    Escape,
    Home,
    End
}
