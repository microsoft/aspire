// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text;
using System.Xml.Linq;

namespace Aspire.Tray.Tests.Helpers;

/// <summary>
/// Renders a <see cref="TrayPanelScene"/> to SVG for previews and documentation. It mirrors the
/// native renderers: same paths, same colors, and system UI fonts at the same sizes. SVG cannot
/// measure text, so truncation and wrapping use an average glyph width; that is close to, but not
/// exactly, what AppKit and GDI produce.
/// </summary>
internal static class TrayPanelSvgRenderer
{
    private const string FontFamily = "-apple-system, BlinkMacSystemFont, 'SF Pro Text', 'Segoe UI Variable Text', 'Segoe UI', sans-serif";
    private static readonly XNamespace s_svg = "http://www.w3.org/2000/svg";
    private static readonly XNamespace s_xlink = "http://www.w3.org/1999/xlink";

    /// <summary>Renders the scene. <paramref name="logoPng"/> is embedded as a data URI when given.</summary>
    public static string Render(TrayPanelScene scene, byte[]? logoPng = null, double margin = 0, bool guides = false)
    {
        var width = scene.Width + 2 * margin;
        var height = scene.Height + 2 * margin;
        var root = new XElement(s_svg + "svg",
            new XAttribute("width", F(width)),
            new XAttribute("height", F(height)),
            new XAttribute("viewBox", $"{F(-margin)} {F(-margin)} {F(width)} {F(height)}"),
            new XAttribute(XNamespace.Xmlns + "xlink", s_xlink.NamespaceName),
            new XAttribute("font-family", FontFamily));
        var defs = new XElement(s_svg + "defs");
        root.Add(defs);
        if (margin > 0)
        {
            defs.Add(new XElement(s_svg + "filter", new XAttribute("id", "shadow"),
                new XAttribute("x", "-20%"), new XAttribute("y", "-20%"), new XAttribute("width", "140%"), new XAttribute("height", "140%"),
                new XElement(s_svg + "feDropShadow", new XAttribute("dx", "0"), new XAttribute("dy", "6"),
                    new XAttribute("stdDeviation", "9"), new XAttribute("flood-opacity", "0.22"))));
            root.Add(new XElement(s_svg + "rect", new XAttribute("width", F(scene.Width)), new XAttribute("height", F(scene.Height)),
                new XAttribute("rx", F(TrayPanel.CornerRadius)), new XAttribute("fill", "#000"), new XAttribute("filter", "url(#shadow)")));
        }

        var stack = new Stack<XElement>();
        var current = root;
        var clipId = 0;
        foreach (var command in scene.Commands)
        {
            switch (command)
            {
                case FillPathCommand fill:
                    current.Add(new XElement(s_svg + "path", new XAttribute("d", ToPathData(fill.Path)), Paint("fill", fill.Color)));
                    break;
                case StrokePathCommand stroke:
                    current.Add(new XElement(s_svg + "path", new XAttribute("d", ToPathData(stroke.Path)), new XAttribute("fill", "none"),
                        Paint("stroke", stroke.Color), new XAttribute("stroke-width", F(stroke.Width)),
                        new XAttribute("stroke-linecap", "round"), new XAttribute("stroke-linejoin", "round")));
                    break;
                case TextCommand text:
                    current.Add(RenderText(text));
                    break;
                case AppIconCommand appIcon:
                    // The renderer has no application icons; draw the generic glyph hosts fall back to.
                    foreach (var shape in appIcon.Fallback)
                    {
                        current.Add(shape switch
                        {
                            FillPathCommand fill => new XElement(s_svg + "path", new XAttribute("d", ToPathData(fill.Path)), Paint("fill", fill.Color)),
                            StrokePathCommand stroke => new XElement(s_svg + "path", new XAttribute("d", ToPathData(stroke.Path)), new XAttribute("fill", "none"),
                                Paint("stroke", stroke.Color), new XAttribute("stroke-width", F(stroke.Width)),
                                new XAttribute("stroke-linecap", "round"), new XAttribute("stroke-linejoin", "round")),
                            _ => throw new InvalidOperationException("App icon fallbacks are shapes only.")
                        });
                    }
                    break;
                case LogoCommand logo:
                    current.Add(logoPng is null
                        ? new XElement(s_svg + "rect", Bounds(logo.Bounds), new XAttribute("rx", "6"), new XAttribute("fill", "#512BD4"))
                        : new XElement(s_svg + "image", Bounds(logo.Bounds),
                            new XAttribute("href", "data:image/png;base64," + Convert.ToBase64String(logoPng))));
                    break;
                case PushClipCommand clip:
                    var id = $"clip{clipId++}";
                    defs.Add(new XElement(s_svg + "clipPath", new XAttribute("id", id), new XElement(s_svg + "rect", Bounds(clip.Bounds))));
                    var group = new XElement(s_svg + "g", new XAttribute("clip-path", $"url(#{id})"));
                    current.Add(group);
                    stack.Push(current);
                    current = group;
                    break;
                case PopClipCommand:
                    current = stack.Pop();
                    break;
            }
        }
        if (stack.Count != 0)
        {
            throw new InvalidOperationException("Unbalanced clip commands.");
        }
        if (guides)
        {
            AddGuides(root, scene);
        }
        return root.ToString();
    }

    /// <summary>
    /// Draws the scene's alignment grid over the panel, in the same colors as the screenshot
    /// overlays: columns in cyan, line centers in magenta, shared baselines in green, and the
    /// boxes glyphs should fill in yellow, with the text baselines the renderers will use in red.
    /// </summary>
    private static void AddGuides(XElement root, TrayPanelScene scene)
    {
        var layer = new XElement(s_svg + "g", new XAttribute("font-family", "Menlo, Consolas, monospace"), new XAttribute("font-size", "4"));
        root.Add(layer);
        static XElement Line(double x1, double y1, double x2, double y2, string color, string? dash = null)
        {
            var line = new XElement(s_svg + "line", new XAttribute("x1", F(x1)), new XAttribute("y1", F(y1)), new XAttribute("x2", F(x2)),
                new XAttribute("y2", F(y2)), new XAttribute("stroke", color), new XAttribute("stroke-width", "0.5"));
            if (dash is not null)
            {
                line.Add(new XAttribute("stroke-dasharray", dash));
            }
            return line;
        }
        foreach (var guide in scene.Guides)
        {
            var b = guide.Bounds;
            switch (guide.Kind)
            {
                case PanelGuideKind.Column:
                    layer.Add(Line(guide.Position, b.Y, guide.Position, b.Bottom, "#00E5FF"));
                    layer.Add(new XElement(s_svg + "text", new XAttribute("x", F(guide.Position)), new XAttribute("y", "-2"),
                        new XAttribute("fill", "#00A8C0"), new XAttribute("text-anchor", "middle"), F(guide.Position)));
                    break;
                case PanelGuideKind.Center:
                    layer.Add(Line(b.X, guide.Position, b.Right, guide.Position, "#FF2BD6"));
                    break;
                case PanelGuideKind.Baseline:
                    layer.Add(Line(b.X, guide.Position, b.Right, guide.Position, "#2BB52B", "2 1"));
                    break;
                case PanelGuideKind.Box:
                    layer.Add(new XElement(s_svg + "rect", Bounds(b), new XAttribute("fill", "none"), new XAttribute("stroke", "#E6B800"),
                        new XAttribute("stroke-width", "0.5")));
                    break;
            }
        }
        foreach (var text in scene.Commands.OfType<TextCommand>().Where(text => text.MaxLines == 1))
        {
            layer.Add(Line(text.Bounds.X, text.Baseline, text.Bounds.X + Math.Min(text.Bounds.Width, 40), text.Baseline, "#FF3B30", "1 0.6"));
        }
    }

    public static string ToPathData(PanelPath path)
    {
        var builder = new StringBuilder();
        foreach (var segment in path.Segments)
        {
            builder.Append(segment.Verb switch
            {
                PanelPathVerb.MoveTo => $"M{F(segment.X3)} {F(segment.Y3)}",
                PanelPathVerb.LineTo => $"L{F(segment.X3)} {F(segment.Y3)}",
                PanelPathVerb.CubicTo => $"C{F(segment.X1)} {F(segment.Y1)} {F(segment.X2)} {F(segment.Y2)} {F(segment.X3)} {F(segment.Y3)}",
                _ => "Z"
            });
        }
        return builder.ToString();
    }

    private static XElement RenderText(TextCommand text)
    {
        var size = PanelFonts.Size(text.Font);
        var lines = Layout(text.Text, text.Bounds.Width, size, text.MaxLines, text.Truncation);
        var (anchor, x) = text.Alignment switch
        {
            PanelTextAlignment.Center => ("middle", text.Bounds.CenterX),
            PanelTextAlignment.Trailing => ("end", text.Bounds.Right),
            _ => ("start", text.Bounds.X)
        };
        var lineHeight = Math.Round(size * 1.3);
        var element = new XElement(s_svg + "text",
            new XAttribute("x", F(x)),
            new XAttribute("font-size", F(size)),
            new XAttribute("font-weight", PanelFonts.IsSemibold(text.Font) ? "600" : "400"),
            new XAttribute("text-anchor", anchor),
            Paint("fill", text.Color));
        if (text.Font == PanelFontRole.Section)
        {
            element.Add(new XAttribute("letter-spacing", "0.4"));
        }
        if (text.MaxLines == 1)
        {
            // Put a single line on the scene's baseline, as the native renderers do: it centers the
            // capitals on the line, or is the baseline the header status shares with the title.
            element.Add(new XAttribute("y", F(text.Baseline)), lines[0]);
            return element;
        }
        for (var i = 0; i < lines.Count; i++)
        {
            element.Add(new XElement(s_svg + "tspan", new XAttribute("x", F(x)),
                new XAttribute("y", F(text.Bounds.Y + size + i * lineHeight - 1)), lines[i]));
        }
        return element;
    }

    private static List<string> Layout(string text, double width, double size, int maxLines, PanelTruncation truncation)
    {
        var capacity = Math.Max(1, (int)(width / (size * 0.53)));
        if (maxLines <= 1)
        {
            return [Truncate(text, capacity, truncation)];
        }
        var lines = new List<string>();
        var line = "";
        var words = text.Split(' ');
        for (var i = 0; i < words.Length; i++)
        {
            var candidate = line.Length == 0 ? words[i] : line + " " + words[i];
            if (candidate.Length <= capacity || line.Length == 0)
            {
                line = candidate;
                continue;
            }
            if (lines.Count == maxLines - 1)
            {
                lines.Add(Truncate(string.Join(' ', [line, .. words[i..]]), capacity, PanelTruncation.End));
                return lines;
            }
            lines.Add(line);
            line = words[i];
        }
        lines.Add(Truncate(line, capacity, PanelTruncation.End));
        return lines;
    }

    private static string Truncate(string text, int capacity, PanelTruncation truncation)
    {
        if (text.Length <= capacity)
        {
            return text;
        }
        if (truncation == PanelTruncation.Middle)
        {
            var tail = (capacity - 1) / 2;
            return text[..(capacity - 1 - tail)] + "…" + text[^tail..];
        }
        return text[..(capacity - 1)].TrimEnd() + "…";
    }

    private static object[] Paint(string name, PanelColor color)
        => color.A == 255
            ? [new XAttribute(name, color.ToHex())]
            : [new XAttribute(name, (color with { A = 255 }).ToHex()), new XAttribute(name + "-opacity", F(color.Alpha))];

    private static object[] Bounds(PanelRect rect)
        => [new XAttribute("x", F(rect.X)), new XAttribute("y", F(rect.Y)), new XAttribute("width", F(rect.Width)), new XAttribute("height", F(rect.Height))];

    private static string F(double value) => Math.Round(value, 2).ToString(CultureInfo.InvariantCulture);
}
