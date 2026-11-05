// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Tray;

/// <summary>The status of an AppHost. Each one has a different shape, so color is never the only cue.</summary>
internal enum PanelStatusGlyph
{
    Healthy,
    Warning,
    Unhealthy,
    Unknown,
    Stopped,
    Busy
}

internal enum PanelIcon
{
    ExternalLink,
    Stop,
    Play,
    More,
    Gear,
    Power,
    ChevronDown,
    ChevronRight,
    ChevronLeft,
    Alert,
    Layers,
    AppWindow,
    Folder,
    Copy,
    Pin,
    Unpin,
    Remove,
    Globe
}

/// <summary>
/// Vector glyphs shared by every renderer. Each glyph is authored in its own design box
/// (20 for status glyphs, 16 for icons, 32 for the empty-state art) and scaled into the
/// requested bounds, so stroke widths scale with it.
/// </summary>
internal static class TrayPanelGlyphs
{
    public const double StatusDesignSize = 20;
    public const double IconDesignSize = 16;
    public const double ArtDesignSize = 32;
    public const double ToggleDesignWidth = 32;
    public const double ToggleDesignHeight = 18;

    public static void AddStatus(List<PanelCommand> commands, PanelStatusGlyph glyph, PanelRect bounds, TrayPanelPalette palette)
    {
        var scale = bounds.Width / StatusDesignSize;
        // Each disc has its own mark color, chosen in the palette to keep 3:1 against it.
        switch (glyph)
        {
            case PanelStatusGlyph.Healthy:
                Disc(palette.Success);
                Stroke(new PanelPath().MoveTo(6.4, 10.3).LineTo(8.9, 12.7).LineTo(13.6, 7.6), palette.SuccessMark, 1.8);
                break;
            case PanelStatusGlyph.Warning:
                Disc(palette.Warning);
                Stroke(new PanelPath().MoveTo(10, 5.6).LineTo(10, 10.8), palette.WarningMark, 2);
                Fill(PanelPath.Circle(10, 14.1, 1.15), palette.WarningMark);
                break;
            case PanelStatusGlyph.Unhealthy:
                Disc(palette.DangerFill);
                Stroke(new PanelPath().MoveTo(7.1, 7.1).LineTo(12.9, 12.9).MoveTo(12.9, 7.1).LineTo(7.1, 12.9), palette.DangerMark, 1.8);
                break;
            case PanelStatusGlyph.Unknown:
                Disc(palette.Neutral);
                Stroke(new PanelPath().MoveTo(7.7, 7.9)
                    .CubicTo(7.7, 6.5, 8.7, 5.5, 10, 5.5)
                    .CubicTo(11.4, 5.5, 12.4, 6.4, 12.4, 7.7)
                    .CubicTo(12.4, 9.3, 10, 9.5, 10, 11.3), palette.NeutralMark, 1.6);
                Fill(PanelPath.Circle(10, 14.1, 1.05), palette.NeutralMark);
                break;
            case PanelStatusGlyph.Stopped:
                Stroke(PanelPath.Circle(10, 10, 7.25), palette.Neutral, 1.5);
                Fill(PanelPath.RoundedRect(new(7, 7, 6, 6), 1.4), palette.Neutral);
                break;
            case PanelStatusGlyph.Busy:
                // A faint full track with an accent three-quarter ring on top, the usual
                // indeterminate progress shape. Hosts may rotate it later.
                Stroke(PanelPath.Circle(10, 10, 7), palette.Neutral.WithAlpha(0.3), 2);
                Stroke(new PanelPath().Arc(10, 10, 7, -90, 270), palette.Accent, 2);
                break;
        }

        void Disc(PanelColor color) => Fill(PanelPath.Circle(10, 10, 8), color);
        void Fill(PanelPath path, PanelColor color) => commands.Add(new FillPathCommand(path.Transform(scale, bounds.X, bounds.Y), color));
        void Stroke(PanelPath path, PanelColor color, double width)
            => commands.Add(new StrokePathCommand(path.Transform(scale, bounds.X, bounds.Y), color, width * scale));
    }

    public static void AddIcon(List<PanelCommand> commands, PanelIcon icon, PanelRect bounds, PanelColor color)
    {
        var design = icon == PanelIcon.Layers ? ArtDesignSize : IconDesignSize;
        var scale = bounds.Width / design;
        switch (icon)
        {
            case PanelIcon.ExternalLink:
                Stroke(new PanelPath().MoveTo(7, 3.25).LineTo(4.75, 3.25)
                    .CubicTo(3.92, 3.25, 3.25, 3.92, 3.25, 4.75).LineTo(3.25, 11.25)
                    .CubicTo(3.25, 12.08, 3.92, 12.75, 4.75, 12.75).LineTo(11.25, 12.75)
                    .CubicTo(12.08, 12.75, 12.75, 12.08, 12.75, 11.25).LineTo(12.75, 9), 1.5);
                Stroke(new PanelPath().MoveTo(9.5, 3.25).LineTo(12.75, 3.25).LineTo(12.75, 6.5)
                    .MoveTo(12.5, 3.5).LineTo(7.5, 8.5), 1.5);
                break;
            case PanelIcon.Stop:
                Fill(PanelPath.RoundedRect(new(4.25, 4.25, 7.5, 7.5), 1.5));
                break;
            case PanelIcon.Play:
                // Fill and stroke the same triangle: the round joins soften its corners.
                var play = new PanelPath().MoveTo(5.25, 3.75).LineTo(12.25, 8).LineTo(5.25, 12.25).Close();
                Fill(play);
                Stroke(play, 1.5);
                break;
            case PanelIcon.More:
                foreach (var x in new[] { 3.5, 8, 12.5 })
                {
                    Fill(PanelPath.Circle(x, 8, 1.3));
                }
                break;
            case PanelIcon.Gear:
                Stroke(CreateGear(), 1.4);
                Stroke(PanelPath.Circle(8, 8, 2.1), 1.4);
                break;
            case PanelIcon.Power:
                // The open arc leaves a 60° gap centered on the top (-90° on y-down axes).
                Stroke(new PanelPath().Arc(8, 8.5, 5.5, -60, 300), 1.5);
                Stroke(new PanelPath().MoveTo(8, 2).LineTo(8, 7.5), 1.5);
                break;
            case PanelIcon.ChevronDown:
                Stroke(new PanelPath().MoveTo(4.5, 6.25).LineTo(8, 9.75).LineTo(11.5, 6.25), 1.5);
                break;
            case PanelIcon.ChevronRight:
                Stroke(new PanelPath().MoveTo(6.25, 4.5).LineTo(9.75, 8).LineTo(6.25, 11.5), 1.5);
                break;
            case PanelIcon.ChevronLeft:
                Stroke(new PanelPath().MoveTo(9.75, 4.5).LineTo(6.25, 8).LineTo(9.75, 11.5), 1.5);
                break;
            case PanelIcon.Alert:
                Stroke(new PanelPath().MoveTo(8, 2.5).LineTo(14.25, 13.25).LineTo(1.75, 13.25).Close(), 1.4);
                Stroke(new PanelPath().MoveTo(8, 6.6).LineTo(8, 9.4), 1.5);
                Fill(PanelPath.Circle(8, 11.4, 0.85));
                break;
            case PanelIcon.Layers:
                Stroke(new PanelPath().MoveTo(16, 5).LineTo(28, 11).LineTo(16, 17).LineTo(4, 11).Close(), 1.75);
                Stroke(new PanelPath().MoveTo(4, 16).LineTo(16, 22).LineTo(28, 16), 1.75);
                Stroke(new PanelPath().MoveTo(4, 21).LineTo(16, 27).LineTo(28, 21), 1.75);
                break;
            case PanelIcon.AppWindow:
                // A generic application window: the Open in items use it for every application.
                Stroke(PanelPath.RoundedRect(new(2.25, 3, 11.5, 10), 1.75), 1.4);
                Stroke(new PanelPath().MoveTo(2.25, 6).LineTo(13.75, 6), 1.4);
                break;
            case PanelIcon.Folder:
                Stroke(new PanelPath().MoveTo(2.25, 4.75)
                    .CubicTo(2.25, 3.92, 2.92, 3.25, 3.75, 3.25).LineTo(6.25, 3.25).LineTo(7.75, 4.75).LineTo(12.25, 4.75)
                    .CubicTo(13.08, 4.75, 13.75, 5.42, 13.75, 6.25).LineTo(13.75, 11.25)
                    .CubicTo(13.75, 12.08, 13.08, 12.75, 12.25, 12.75).LineTo(3.75, 12.75)
                    .CubicTo(2.92, 12.75, 2.25, 12.08, 2.25, 11.25).Close(), 1.4);
                break;
            case PanelIcon.Copy:
                Stroke(PanelPath.RoundedRect(new(5.75, 5.75, 7.5, 7.5), 1.5), 1.4);
                Stroke(new PanelPath().MoveTo(10.25, 3.25).LineTo(4.75, 3.25)
                    .CubicTo(3.92, 3.25, 3.25, 3.92, 3.25, 4.75).LineTo(3.25, 10.25), 1.4);
                break;
            case PanelIcon.Pin:
            case PanelIcon.Unpin:
                // A push pin: a flat head, a flared body and the needle.
                Stroke(new PanelPath().MoveTo(5.25, 2.75).LineTo(10.75, 2.75)
                    .MoveTo(6.5, 2.75).LineTo(6.5, 6.75).LineTo(4.5, 9.25).LineTo(11.5, 9.25).LineTo(9.5, 6.75).LineTo(9.5, 2.75)
                    .MoveTo(8, 9.25).LineTo(8, 13.5), 1.4);
                if (icon == PanelIcon.Unpin)
                {
                    Stroke(new PanelPath().MoveTo(2.75, 2.75).LineTo(13.25, 13.25), 1.4);
                }
                break;
            case PanelIcon.Globe:
                // A globe for the web documentation: the outline, a meridian, and the equator.
                Stroke(PanelPath.Circle(8, 8, 5.75), 1.3);
                Stroke(new PanelPath().MoveTo(8, 2.25).CubicTo(5.1, 4.2, 5.1, 11.8, 8, 13.75)
                    .CubicTo(10.9, 11.8, 10.9, 4.2, 8, 2.25), 1.3);
                Stroke(new PanelPath().MoveTo(2.25, 8).LineTo(13.75, 8), 1.3);
                break;
            case PanelIcon.Remove:
                Stroke(new PanelPath().MoveTo(4.5, 4.5).LineTo(11.5, 11.5).MoveTo(11.5, 4.5).LineTo(4.5, 11.5), 1.5);
                break;
        }

        void Fill(PanelPath path) => commands.Add(new FillPathCommand(path.Transform(scale, bounds.X, bounds.Y), color));
        void Stroke(PanelPath path, double width)
            => commands.Add(new StrokePathCommand(path.Transform(scale, bounds.X, bounds.Y), color, width * scale));
    }

    /// <summary>
    /// Draws a toggle switch in a 32 × 18 design box: a pill track, accent when on and a faint
    /// neutral when off, with a knob on the matching side. The knob position, not only the
    /// color, tells on from off. A disabled switch is drawn at reduced opacity.
    /// </summary>
    public static void AddToggle(List<PanelCommand> commands, bool on, bool enabled, PanelRect bounds, TrayPanelPalette palette)
    {
        var scale = bounds.Width / ToggleDesignWidth;
        var opacity = enabled ? 1 : 0.45;
        var track = new PanelRect(0, 0, ToggleDesignWidth, ToggleDesignHeight);
        var trackColor = on ? palette.Accent : palette.Neutral.WithAlpha(0.35);
        commands.Add(new FillPathCommand(PanelPath.RoundedRect(track, 9).Transform(scale, bounds.X, bounds.Y),
            trackColor.WithAlpha(trackColor.Alpha * opacity)));
        if (!on || palette.IsHighContrast)
        {
            // Outline the track so the off state stays visible on any background.
            commands.Add(new StrokePathCommand(PanelPath.RoundedRect(track.Inset(0.5, 0.5), 8.5).Transform(scale, bounds.X, bounds.Y),
                palette.Neutral.WithAlpha(0.6 * opacity), scale));
        }
        commands.Add(new FillPathCommand(PanelPath.Circle(on ? 23 : 9, 9, 6.5).Transform(scale, bounds.X, bounds.Y),
            palette.OnColor.WithAlpha(enabled ? 1 : 0.7)));
        if (!on)
        {
            commands.Add(new StrokePathCommand(PanelPath.Circle(9, 9, 6.5).Transform(scale, bounds.X, bounds.Y),
                palette.Neutral.WithAlpha(0.5 * opacity), 0.75 * scale));
        }
    }

    /// <summary>
    /// Builds an 8-tooth gear outline around (8, 8): each tooth is a trapezoid from the root
    /// circle to the tip circle, and the gaps follow the root circle.
    /// </summary>
    private static PanelPath CreateGear()
    {
        const double Root = 5.1, Tip = 6.9, RootHalf = 13, TipHalf = 8.5;
        var path = new PanelPath();
        for (var i = 0; i < 8; i++)
        {
            var center = i * 45.0 - 90;
            var tooth = new[] { (center - RootHalf, Root), (center - TipHalf, Tip), (center + TipHalf, Tip), (center + RootHalf, Root) };
            for (var j = 0; j < tooth.Length; j++)
            {
                var (angle, radius) = tooth[j];
                var (sin, cos) = Math.SinCos(angle * Math.PI / 180);
                if (i == 0 && j == 0)
                {
                    path.MoveTo(8 + radius * cos, 8 + radius * sin);
                }
                else
                {
                    path.LineTo(8 + radius * cos, 8 + radius * sin);
                }
            }
            path.Arc(8, 8, Root, center + RootHalf, 45 - 2 * RootHalf, connect: true);
        }
        return path.Close();
    }
}
