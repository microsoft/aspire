// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;

namespace Aspire.Tray;

/// <summary>
/// Writes what a panel scene draws, and the alignment grid it intends, as JSON. The interactive
/// smoke preview writes it next to a screenshot of the real window, so tools can draw the guides
/// over the screenshot and measure where the ink actually landed. Not used by the product.
/// </summary>
/// <remarks>
/// Shape (coordinates in DIPs, relative to the panel's top-left corner, except the window frame,
/// which is in points on macOS and device pixels on Windows):
/// <code>
/// { "platform": "macOS", "appearance": "dark", "scale": 2, "width": 360, "height": 412,
///   "windowId": 4711, "window": { "x": 1052, "y": 33, "width": 720, "height": 824 },
///   "texts": [ { "text": "Aspire", "font": "Title", "size": 15, "semibold": true,
///                "x": 52, "y": 12, "w": 56, "h": 20, "align": "Leading", "maxLines": 1 } ],
///   "images": [ { "kind": "logo", "x": 20, "y": 12, "w": 20, "h": 20 } ],
///   "targets": [ { "kind": "Row", "x": 8, "y": 50, "w": 344, "h": 48, "enabled": true } ],
///   "guides": [ { "kind": "Column", "position": 30, "label": "leading center", "x": 0, "y": 0, "w": 360, "h": 412 } ] }
/// </code>
/// </remarks>
internal static class TrayPanelSceneJson
{
    public static void Write(string path, TrayPanelScene scene, string platform, string appearance, double scale, long windowId,
        (int X, int Y, int Width, int Height) window)
    {
        using var stream = File.Create(path);
        using var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        json.WriteStartObject();
        json.WriteString("platform", platform);
        json.WriteString("appearance", appearance);
        json.WriteNumber("scale", scale);
        json.WriteNumber("width", scene.Width);
        json.WriteNumber("height", scene.Height);
        // The CGWindowID on macOS (for `screencapture -l`), the HWND on Windows.
        json.WriteNumber("windowId", windowId);
        json.WriteStartObject("window");
        json.WriteNumber("x", window.X);
        json.WriteNumber("y", window.Y);
        json.WriteNumber("width", window.Width);
        json.WriteNumber("height", window.Height);
        json.WriteEndObject();

        json.WriteStartArray("texts");
        foreach (var text in scene.Commands.OfType<TextCommand>())
        {
            json.WriteStartObject();
            json.WriteString("text", text.Text);
            json.WriteString("font", text.Font.ToString());
            json.WriteNumber("size", PanelFonts.Size(text.Font));
            json.WriteBoolean("semibold", PanelFonts.IsSemibold(text.Font));
            Rect(json, text.Bounds);
            json.WriteNumber("baseline", text.Baseline);
            json.WriteString("align", text.Alignment.ToString());
            json.WriteNumber("maxLines", text.MaxLines);
            json.WriteEndObject();
        }
        json.WriteEndArray();

        json.WriteStartArray("images");
        foreach (var command in scene.Commands)
        {
            var (kind, bounds) = command switch
            {
                LogoCommand logo => ("logo", logo.Bounds),
                AppIconCommand icon => ("app:" + icon.Application, icon.Bounds),
                _ => (null, default)
            };
            if (kind is not null)
            {
                json.WriteStartObject();
                json.WriteString("kind", kind);
                Rect(json, bounds);
                json.WriteEndObject();
            }
        }
        json.WriteEndArray();

        json.WriteStartArray("targets");
        foreach (var target in scene.Targets.Where(target => !target.HitBounds.IsEmpty))
        {
            json.WriteStartObject();
            json.WriteString("kind", target.Kind.ToString());
            Rect(json, target.Bounds);
            json.WriteBoolean("enabled", target.Enabled);
            json.WriteEndObject();
        }
        json.WriteEndArray();

        json.WriteStartArray("guides");
        foreach (var guide in scene.Guides)
        {
            json.WriteStartObject();
            json.WriteString("kind", guide.Kind.ToString());
            json.WriteNumber("position", guide.Position);
            json.WriteString("label", guide.Label);
            Rect(json, guide.Bounds);
            json.WriteEndObject();
        }
        json.WriteEndArray();
        json.WriteEndObject();

        static void Rect(Utf8JsonWriter json, PanelRect rect)
        {
            json.WriteNumber("x", rect.X);
            json.WriteNumber("y", rect.Y);
            json.WriteNumber("w", rect.Width);
            json.WriteNumber("h", rect.Height);
        }
    }
}
