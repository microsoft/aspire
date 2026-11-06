using System.Text.Json.Nodes;
using SkiaSharp;
using TextMateSharp.Grammars;
using TextMateSharp.Registry;

/// <summary>
/// Slides, drawn from the "slide" of a step in storyboard.json: a heading, then bullet cards, code panels, or code
/// panels with note cards. See references/storyboard.md for the format.
/// </summary>
static class Slides
{
    // The syntax colors of code panels: the Visual Studio Code Dark+ theme, through its TextMate grammars.
    private static readonly RegistryOptions s_options = new(ThemeName.DarkPlus);
    private static readonly Registry s_registry = new(s_options);
    private static readonly SKColor s_defaultCode = SKColor.Parse("#D4D4D4");
    private static readonly SKColor s_typeColor = SKColor.Parse("#4EC9B0");

    // The language names of the storyboard, and the TextMate language ids they map to.
    private static readonly Dictionary<string, string> s_languages = new()
    {
        ["csharp"] = "csharp", ["json"] = "json", ["typescript"] = "typescript", ["javascript"] = "javascript",
        ["bash"] = "shellscript", ["powershell"] = "powershell", ["xml"] = "xml", ["html"] = "html", ["cshtml"] = "html",
        ["yaml"] = "yaml",
    };

    /// <summary>Stops the build when a slide's text runs off its card, panel, or the slide.</summary>
    private static void Fits(bool condition, string message)
    {
        if (!condition)
        {
            throw new VideoException($"Slide layout: {message}.");
        }
    }

    public static SKBitmap Render(JsonObject slide, string chapterTitle, string? label, string workspace)
    {
        var bitmap = Brand.NewBackground();
        using var canvas = new SKCanvas(bitmap);
        Brand.Header(canvas, chapterTitle, label);

        // The heading, underlined in purple. Content starts at y = 240.
        var heading = (string?)slide["heading"] ?? throw new VideoException("A slide needs a \"heading\".");
        using var headingFont = Brand.Font(52, SKFontStyleWeight.Bold);
        Fits(headingFont.MeasureText(heading) <= Brand.W - 2 * Brand.Margin, $"heading \"{heading}\" is too long");
        Brand.DrawText(canvas, heading, Brand.Margin, 150, headingFont, Brand.Text);
        using (var bar = Brand.Fill(Brand.Primary))
        {
            canvas.DrawRoundRect(SKRect.Create(Brand.Margin, 192, 100, 6), 3, 3, bar);
        }

        var code = slide["code"] switch
        {
            JsonArray panels => panels.Select(panel => panel!.AsObject()).ToList(),
            JsonObject panel => [panel],
            _ => [],
        };
        var notes = slide["notes"]?.AsArray();

        if (slide["bullets"] is JsonArray bullets)
        {
            Fits(code.Count == 0, "a slide has bullets or code, not both");
            var active = slide["active"]?.AsArray().Select(index => (int)index!).ToHashSet();
            Bullets(canvas, bullets, active);
        }
        else if (code.Count > 0)
        {
            var right = notes is null ? Brand.W - Brand.Margin : 1240;
            // Each panel is as tall as its code (the window bar, the lines, and a margin), so none ends in a large
            // empty area; stacked panels have 40 px between them, and must fit above y = 1030.
            var top = 240f;
            foreach (var panel in code)
            {
                var lines = CodeText(panel, workspace).Split('\n').Length;
                var height = 74 + lines * LineHeight(panel) + 30;
                Fits(top + height <= 1030, $"{(string?)panel["title"]}: the code panels don't fit the slide; shorten the code, or split the slide");
                Code(canvas, new SKRect(Brand.Margin, top, right, top + height), panel, workspace);
                top += height + 40;
            }

            if (notes is not null)
            {
                Notes(canvas, notes, new SKRect(1280, 300, Brand.W - Brand.Margin, 1030));
            }
        }
        else
        {
            Fits(notes is null, "note cards go next to code");
        }

        return bitmap;
    }

    /// <summary>
    /// Up to four cards of { icon, title, text }. icon is a short text, such as "1", or "check" for a check mark.
    /// active lists the indexes shown bright, the others are dimmed; without it, all are bright.
    /// </summary>
    private static void Bullets(SKCanvas canvas, JsonArray items, HashSet<int>? active)
    {
        using var titleFont = Brand.Font(36, SKFontStyleWeight.Bold);
        using var textFont = Brand.Font(28);
        using var iconFont = Brand.Font(40, SKFontStyleWeight.Bold);
        float x = Brand.Margin, y = 250;

        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index]!.AsObject();
            var (icon, title, text) = ((string?)item["icon"] ?? "", (string?)item["title"] ?? "", (string?)item["text"] ?? "");
            var on = active is null || active.Contains(index);
            Fits(y + 150 <= Brand.H - 30, $"bullet card \"{title}\" runs off the bottom of the slide");
            Fits(titleFont.MeasureText(title) <= Brand.W - Brand.Margin - x - 170, $"bullet title \"{title}\" is too long");
            Fits(textFont.MeasureText(text) <= Brand.W - Brand.Margin - x - 170, $"bullet text \"{text}\" is too long");

            var card = new SKRect(x, y, Brand.W - Brand.Margin, y + 150);
            using (var fill = Brand.Fill(on ? Brand.Panel : Brand.PanelDim))
            using (var outline = Brand.Stroke(on ? Brand.Primary : Brand.Border, 2))
            {
                canvas.DrawRoundRect(card, 16, 16, fill);
                canvas.DrawRoundRect(card, 16, 16, outline);
            }

            using (var circle = Brand.Fill(on ? Brand.Primary : Brand.Border))
            {
                canvas.DrawOval(new SKRect(x + 34, y + 39, x + 106, y + 111), circle);
            }

            var color = on ? Brand.White : Brand.Muted;
            if (icon == "check")
            {
                using var check = Brand.Stroke(color, 7);
                check.StrokeJoin = SKStrokeJoin.Round;
                check.StrokeCap = SKStrokeCap.Round;
                using var builder = new SKPathBuilder();
                builder.MoveTo(x + 52, y + 76);
                builder.LineTo(x + 65, y + 90);
                builder.LineTo(x + 89, y + 60);
                using var path = builder.Detach();
                canvas.DrawPath(path, check);
            }
            else
            {
                Brand.DrawText(canvas, icon, x + 70, y + 75, iconFont, color, Brand.Align.Center);
            }

            Brand.DrawText(canvas, title, x + 140, y + 50, titleFont, on ? Brand.Text : Brand.Dim);
            Brand.DrawText(canvas, text, x + 140, y + 102, textFont, on ? Brand.Muted : Brand.Dim);
            y += 176;
        }
    }

    /// <summary>Up to three cards of { title, text } stacked in a box, next to a code panel: a fourth runs off it.</summary>
    private static void Notes(SKCanvas canvas, JsonArray notes, SKRect box)
    {
        using var titleFont = Brand.Font(34, SKFontStyleWeight.Bold);
        using var textFont = Brand.Font(30);
        var y = box.Top;

        foreach (var note in notes)
        {
            var (title, text) = ((string?)note!["title"] ?? "", (string?)note["text"] ?? "");
            Fits(y + 180 <= box.Bottom, $"note card \"{title}\" runs off its box");
            Fits(titleFont.MeasureText(title) <= box.Width - 80, $"note title \"{title}\" is too long");
            Fits(textFont.MeasureText(text) <= box.Width - 80, $"note text \"{text}\" is too long");
            var card = new SKRect(box.Left, y, box.Right, y + 180);
            using (var fill = Brand.Fill(Brand.Panel))
            using (var outline = Brand.Stroke(Brand.Primary, 2))
            {
                canvas.DrawRoundRect(card, 16, 16, fill);
                canvas.DrawRoundRect(card, 16, 16, outline);
            }

            Brand.DrawText(canvas, title, box.Left + 40, y + 64, titleFont, Brand.Text);
            Brand.DrawText(canvas, text, box.Left + 40, y + 118, textFont, Brand.Muted);
            y += 220;
        }
    }

    private static float Size(JsonObject panel) => (float?)panel["size"] ?? 25;

    private static int LineHeight(JsonObject panel) => (int)(Size(panel) * 1.42);

    /// <summary>The code of a panel: its "text", or the content of its "source" file, relative to the workspace.</summary>
    private static string CodeText(JsonObject panel, string workspace)
    {
        var text = (string?)panel["text"]
            ?? ((string?)panel["source"] is { } source ? File.ReadAllText(Path.Combine(workspace, source)) : null)
            ?? throw new VideoException("A code panel needs a \"text\" or a \"source\".");
        return text.ReplaceLineEndings("\n").Trim('\n');
    }

    /// <summary>
    /// A code panel with a window bar and a file name. "types": names colored as types, since the grammars can't
    /// tell them from variables. "highlight": the 1-based lines shown bright, the others dimmed. "size": the font
    /// size, 22 or more; keep at most about 20 lines per panel.
    /// </summary>
    private static void Code(SKCanvas canvas, SKRect box, JsonObject panel, string workspace)
    {
        var title = (string?)panel["title"] ?? "";
        var language = (string?)panel["language"] ?? "csharp";
        var types = panel["types"]?.AsArray().Select(type => (string)type!).ToHashSet() ?? [];
        var highlight = panel["highlight"]?.AsArray().Select(line => (int)line!).ToHashSet();
        var size = Size(panel);
        var code = CodeText(panel, workspace);

        using (var fill = Brand.Fill(Brand.CodeBackground))
        using (var outline = Brand.Stroke(Brand.Border, 2))
        {
            canvas.DrawRoundRect(box, 14, 14, fill);
            canvas.DrawRoundRect(box, 14, 14, outline);
        }

        using (var bar = Brand.Fill(Brand.Panel))
        {
            canvas.Save();
            canvas.ClipRect(new SKRect(box.Left, box.Top, box.Right, box.Top + 52));
            canvas.DrawRoundRect(box, 14, 14, bar);
            canvas.Restore();
        }

        SKColor[] dots = [SKColor.Parse("#ED6A5E"), SKColor.Parse("#F5BF4F"), SKColor.Parse("#62C554")];
        for (var index = 0; index < dots.Length; index++)
        {
            using var dot = Brand.Fill(dots[index]);
            canvas.DrawCircle(box.Left + 29 + index * 26, box.Top + 26, 7, dot);
        }

        using (var titleFont = Brand.Font(21, SKFontStyleWeight.SemiBold))
        {
            Brand.DrawText(canvas, title, box.Left + 112, box.Top + 26, titleFont, Brand.Muted);
        }

        using var font = Brand.Mono(size);
        var lineHeight = LineHeight(panel);
        var lines = code.Split('\n');
        Fits(box.Top + 74 + lines.Length * lineHeight <= box.Bottom, $"{title}: {lines.Length} lines don't fit the panel; make it taller or the code shorter");
        foreach (var line in lines)
        {
            Fits(font.MeasureText(line) <= box.Width - 60, $"{title}: line \"{line.Trim()}\" is wider than the panel");
        }

        if (!s_languages.TryGetValue(language, out var languageId))
        {
            throw new VideoException($"{title}: unknown language \"{language}\"; use {string.Join(", ", s_languages.Keys)}.");
        }

        var grammar = s_registry.LoadGrammar(s_options.GetScopeByLanguageId(languageId));
        var theme = s_registry.GetTheme();
        IStateStack? state = null;
        var y = box.Top + 74 - font.Metrics.Ascent;

        for (var number = 1; number <= lines.Length; number++)
        {
            var line = lines[number - 1];
            var result = grammar.TokenizeLine(line, state, TimeSpan.FromSeconds(5));
            state = result.RuleStack;
            var x = box.Left + 30;

            foreach (var token in result.Tokens)
            {
                var start = Math.Min(token.StartIndex, line.Length);
                var end = Math.Min(token.EndIndex, line.Length);
                if (end <= start)
                {
                    continue;
                }

                var part = line[start..end];
                var color = s_defaultCode;
                // The last matching rule with a foreground wins, as in the TextMate theme engine.
                foreach (var rule in theme.Match(token.Scopes))
                {
                    if (rule.foreground > 0)
                    {
                        color = SKColor.Parse(theme.GetColor(rule.foreground));
                    }
                }

                if (types.Contains(part))
                {
                    color = s_typeColor;
                }

                if (highlight is not null && !highlight.Contains(number))
                {
                    color = Blend(color, Brand.Background, 0.38f);
                }

                using var paint = Brand.Fill(color);
                canvas.DrawText(part, x, y, SKTextAlign.Left, font, paint);
                x += font.MeasureText(part);
            }

            y += lineHeight;
        }
    }

    private static SKColor Blend(SKColor color, SKColor background, float amount)
        => new((byte)(color.Red * amount + background.Red * (1 - amount)),
               (byte)(color.Green * amount + background.Green * (1 - amount)),
               (byte)(color.Blue * amount + background.Blue * (1 - amount)));
}
