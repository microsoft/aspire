using SkiaSharp;

/// <summary>
/// The Aspire look of every video: colors, fonts, the logo, backgrounds, title cards, and the frame around
/// recordings. Don't change these values for a single video: every Aspire video shares them. See references/specs.md.
/// </summary>
static class Brand
{
    // The output frame.
    public const int W = 1920;
    public const int H = 1080;
    public const int Fps = 30;

    // The Aspire brand colors (aspire.dev, src/frontend/src/styles/aspire-brand.css).
    public static readonly SKColor Purple = SKColor.Parse("#512BD4");     // the brand purple: pills, the logo
    public static readonly SKColor Primary = SKColor.Parse("#7455DD");    // accents, frame, highlights, cursor clicks
    public static readonly SKColor Secondary = SKColor.Parse("#B9AAEE");  // light accents on the dark background
    public static readonly SKColor White = SKColors.White;

    // The dark theme of the videos: the aspire.dev dark theme.
    public static readonly SKColor Background = SKColor.Parse("#1F1E33");
    public static readonly SKColor Panel = SKColor.Parse("#2A2949");       // dark surface
    public static readonly SKColor PanelDim = SKColor.Parse("#24233E");
    public static readonly SKColor Border = SKColor.Parse("#3B3A5E");      // dark surface raised
    public static readonly SKColor CodeBackground = SKColor.Parse("#161527");
    public static readonly SKColor Text = SKColor.Parse("#F6F3FF");        // dark text
    public static readonly SKColor Muted = SKColor.Parse("#CDC7E8");       // dark text muted
    public static readonly SKColor Dim = SKColor.Parse("#6B6986");         // ink subtle

    // Unbranded videos ("branding": false): no logo, no frame, no Aspire color; white and gray text on black.
    public static readonly SKColor PlainBackground = SKColors.Black;
    public static readonly SKColor PlainText = SKColors.White;
    public static readonly SKColor PlainMuted = SKColor.Parse("#AAAAAA");

    // Where a recording sits in the frame: 1728 x 972 (90%), centered, under the header.
    public const int VideoW = 1728;
    public const int VideoH = 972;
    public const int VideoX = (W - VideoW) / 2;
    public const int VideoY = H - VideoH - 14;
    public const int Margin = 96;

    // The fonts are the system's: the aspire.dev font stack falls back to the system UI font
    // (system-ui, -apple-system, 'Segoe UI', roboto) and, for code, to ui-monospace, SFMono, Menlo or Consolas.
    // The first family installed wins: Windows, then macOS, then the usual Linux families.
    private static readonly string[] s_sansFamilies = ["Segoe UI", "Helvetica Neue", "Noto Sans", "DejaVu Sans", "Liberation Sans", "Arial"];
    private static readonly string[] s_monoFamilies = ["Cascadia Mono", "Consolas", "Menlo", "SF Mono", "Noto Sans Mono", "DejaVu Sans Mono", "Liberation Mono", "Courier New"];
    private static readonly Dictionary<(SKFontStyleWeight, bool), SKTypeface> s_typefaces = [];

    public static string SkillTools { get; } = (string)AppContext.GetData("EntryPointFileDirectoryPath")!;

    private static SKTypeface Typeface(SKFontStyleWeight weight, bool mono)
    {
        if (!s_typefaces.TryGetValue((weight, mono), out var typeface))
        {
            var style = new SKFontStyle(weight, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright);
            typeface = (mono ? s_monoFamilies : s_sansFamilies)
                .Select(family => SKFontManager.Default.MatchFamily(family, style))
                .FirstOrDefault(match => match is not null)
                ?? SKFontManager.Default.MatchFamily(null, style)
                ?? SKTypeface.Default;
            s_typefaces[(weight, mono)] = typeface;
        }

        return typeface;
    }

    public static SKFont Font(float size, SKFontStyleWeight weight = SKFontStyleWeight.Normal)
        => new(Typeface(weight, mono: false), size) { Edging = SKFontEdging.Antialias, Subpixel = true };

    public static SKFont Mono(float size)
        => new(Typeface(SKFontStyleWeight.Normal, mono: true), size) { Edging = SKFontEdging.Antialias, Subpixel = true };

    /// <summary>Draws a bitmap at its own size: logos and frames are composed without scaling.</summary>
    public static void DrawBitmap(SKCanvas canvas, SKBitmap bitmap, float x, float y)
    {
        using var image = SKImage.FromBitmap(bitmap);
        canvas.DrawImage(image, x, y, new SKSamplingOptions(SKCubicResampler.Mitchell));
    }

    public static SKPaint Fill(SKColor color) => new() { Color = color, IsAntialias = true, Style = SKPaintStyle.Fill };

    public static SKPaint Stroke(SKColor color, float width) => new() { Color = color, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = width };

    public enum Align { Left, Center, Right }

    /// <summary>
    /// Draws text anchored like PIL's "lm" / "mm" anchors: horizontally at x (left, center or right), and vertically
    /// centered on y between the font's ascent and descent; or on the baseline at y with baseline: true.
    /// </summary>
    public static void DrawText(SKCanvas canvas, string text, float x, float y, SKFont font, SKColor color, Align align = Align.Left, bool baseline = false)
    {
        var width = font.MeasureText(text);
        var left = align switch { Align.Center => x - width / 2, Align.Right => x - width, _ => x };
        // Ascent is negative and descent positive: the middle between them is (ascent + descent) / 2 from the baseline.
        var metrics = font.Metrics;
        var bottom = baseline ? y : y - (metrics.Ascent + metrics.Descent) / 2;
        using var paint = Fill(color);
        canvas.DrawText(text, left, bottom, SKTextAlign.Left, font, paint);
    }

    public static SKBitmap NewImage(SKColor background)
    {
        var bitmap = new SKBitmap(W, H, SKColorType.Rgba8888, SKAlphaType.Premul);
        bitmap.Erase(background);
        return bitmap;
    }

    public static void Save(SKBitmap bitmap, string path, int jpegQuality = 0)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = jpegQuality > 0 ? image.Encode(SKEncodedImageFormat.Jpeg, jpegQuality) : image.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = File.Create(path);
        data.SaveTo(stream);
    }

    /// <summary>The bounds of the visible (non-transparent) pixels of an image.</summary>
    private static SKRectI Visible(SKBitmap bitmap)
    {
        int left = bitmap.Width, top = bitmap.Height, right = 0, bottom = 0;
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                if (bitmap.GetPixel(x, y).Alpha > 0)
                {
                    left = Math.Min(left, x);
                    top = Math.Min(top, y);
                    right = Math.Max(right, x + 1);
                    bottom = Math.Max(bottom, y + 1);
                }
            }
        }

        return new SKRectI(left, top, right, bottom);
    }

    private static SKBitmap Crop(SKBitmap source, SKRectI bounds)
    {
        var cropped = new SKBitmap(bounds.Width, bounds.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        source.ExtractSubset(cropped, bounds);
        return cropped.Copy();
    }

    private static SKBitmap Resize(SKBitmap source, int width)
    {
        var height = (int)Math.Round(source.Height * (double)width / source.Width);
        return source.Resize(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul), new SKSamplingOptions(SKCubicResampler.Mitchell));
    }

    private static readonly Dictionary<(int, bool), SKBitmap> s_logos = [];

    /// <summary>
    /// The logo for a dark background: the Aspire symbol, followed by the name "Aspire" in white. symbolOnly: the
    /// symbol alone. The symbol comes from assets/aspire-logo-1024.png, rendered from aspire.dev's SVG.
    /// </summary>
    public static SKBitmap Logo(int width, bool symbolOnly = false)
    {
        if (s_logos.TryGetValue((width, symbolOnly), out var cached))
        {
            return cached;
        }

        using var source = SKBitmap.Decode(Path.Combine(SkillTools, "assets", "aspire-logo-1024.png"));
        using var symbol = Crop(source, Visible(source));
        SKBitmap logo;

        if (symbolOnly)
        {
            logo = Resize(symbol, width);
        }
        else
        {
            // Composed at a large size, then scaled: the name's cap height is about 60% of the symbol's height.
            using var big = symbol.Resize(new SKImageInfo((int)(symbol.Width * 400.0 / symbol.Height), 400, SKColorType.Rgba8888, SKAlphaType.Premul), new SKSamplingOptions(SKCubicResampler.Mitchell));
            using var nameFont = Font(380, SKFontStyleWeight.SemiBold);
            var gap = 90;
            var nameWidth = (int)Math.Ceiling(nameFont.MeasureText("Aspire"));
            using var composed = new SKBitmap(big.Width + gap + nameWidth + 40, 560, SKColorType.Rgba8888, SKAlphaType.Premul);
            composed.Erase(SKColors.Transparent);
            using (var canvas = new SKCanvas(composed))
            {
                DrawBitmap(canvas, big, 0, 0);
                DrawText(canvas, "Aspire", big.Width + gap, 400, nameFont, White, baseline: true);
            }

            using var trimmed = Crop(composed, Visible(composed));
            logo = Resize(trimmed, width);
        }

        s_logos[(width, symbolOnly)] = logo;
        return logo;
    }

    /// <summary>The dark background, with a soft purple glow in the top right corner and a magenta one in the bottom left.</summary>
    public static SKBitmap NewBackground()
    {
        var bitmap = NewImage(Background);
        using var canvas = new SKCanvas(bitmap);
        using var blur = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 180);
        using var purple = new SKPaint { Color = new SKColor(66, 42, 150, 242), IsAntialias = true, MaskFilter = blur };
        using var magenta = new SKPaint { Color = new SKColor(70, 34, 104, 242), IsAntialias = true, MaskFilter = blur };
        canvas.DrawOval(SKRect.Create(W - 760, -560, 1320, 1080), purple);
        canvas.DrawOval(SKRect.Create(-600, H - 380, 1120, 1000), magenta);
        return bitmap;
    }

    /// <summary>The header of recordings and slides: the logo, the chapter's title, and a purple pill such as "Chapter 3".</summary>
    public static void Header(SKCanvas canvas, string chapterTitle, string? label)
    {
        var mark = Logo(190);
        DrawBitmap(canvas, mark, Margin, 47 - mark.Height / 2);
        var x = Margin + mark.Width + 24;
        using (var line = Stroke(Dim, 2))
        {
            canvas.DrawLine(x, 28, x, 66, line);
        }

        using var titleFont = Font(32, SKFontStyleWeight.SemiBold);
        DrawText(canvas, chapterTitle, x + 24, 47, titleFont, Text);

        if (label is not null)
        {
            var text = label.ToUpperInvariant();
            using var labelFont = Font(21, SKFontStyleWeight.Bold);
            var width = labelFont.MeasureText(text) + 40;
            var right = W - Margin;
            using var pill = Fill(Purple);
            canvas.DrawRoundRect(SKRect.Create(right - width, 27, width, 40), 20, 20, pill);
            DrawText(canvas, text, right - width / 2, 48, labelFont, White, Align.Center);
        }
    }

    /// <summary>The frame a recording is laid over: the header, and a shadowed purple border around the recording.</summary>
    public static SKBitmap Frame(string chapterTitle, string? label)
    {
        var bitmap = NewBackground();
        using var canvas = new SKCanvas(bitmap);
        Header(canvas, chapterTitle, label);
        using var shadowBlur = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 10);
        using var shadow = new SKPaint { Color = new SKColor(0, 0, 0, 170), IsAntialias = true, MaskFilter = shadowBlur };
        canvas.DrawRoundRect(SKRect.Create(VideoX - 4, VideoY - 2, VideoW + 8, VideoH + 10), 10, 10, shadow);
        using var border = Stroke(Primary, 3);
        canvas.DrawRoundRect(SKRect.Create(VideoX - 1.5f, VideoY - 1.5f, VideoW + 3, VideoH + 3), 8, 8, border);
        return bitmap;
    }

    /// <summary>
    /// A title card. big: the opening and closing cards, with the full logo. Otherwise a chapter card, with the
    /// symbol and a label such as "Chapter 3".
    /// </summary>
    public static SKBitmap Card(string title, string subtitle, string? label = null, bool big = false)
    {
        var bitmap = NewBackground();
        using var canvas = new SKCanvas(bitmap);
        var mark = big ? Logo(640) : Logo(150, symbolOnly: true);
        using var titleFont = Font(big ? 72 : 76, SKFontStyleWeight.Bold);
        var gap = big ? 110 : 70;
        var height = mark.Height + gap + (label is not null ? 64 : 0) + 96 + 56 + 50;
        var top = (H - height) / 2;
        DrawBitmap(canvas, mark, (W - mark.Width) / 2, top);
        float y = top + mark.Height + gap;

        if (label is not null)
        {
            using var labelFont = Font(30, SKFontStyleWeight.Bold);
            DrawText(canvas, label.ToUpperInvariant(), W / 2, y, labelFont, Secondary, Align.Center);
            y += 64;
        }

        DrawText(canvas, title, W / 2, y, titleFont, Text, Align.Center);
        y += 72;
        using (var bar = Fill(Primary))
        {
            canvas.DrawRoundRect(SKRect.Create(W / 2 - 60, y, 120, 6), 3, 3, bar);
        }

        y += 56;
        using var subtitleFont = Font(38);
        DrawText(canvas, subtitle, W / 2, y, subtitleFont, Muted, Align.Center);
        return bitmap;
    }

    /// <summary>A title card without branding: the label (such as "Chapter 3"), the title, and the subtitle, centered.</summary>
    public static SKBitmap PlainCard(string title, string subtitle, string? label = null)
    {
        var bitmap = NewImage(PlainBackground);
        using var canvas = new SKCanvas(bitmap);
        float y = H / 2 - (label is not null ? 40 : 0) - 40;

        if (label is not null)
        {
            using var labelFont = Font(30, SKFontStyleWeight.SemiBold);
            DrawText(canvas, label.ToUpperInvariant(), W / 2, y - 80, labelFont, PlainMuted, Align.Center);
        }

        using var titleFont = Font(72, SKFontStyleWeight.Bold);
        DrawText(canvas, title, W / 2, y, titleFont, PlainText, Align.Center);

        if (!string.IsNullOrEmpty(subtitle))
        {
            using var subtitleFont = Font(38);
            DrawText(canvas, subtitle, W / 2, y + 96, subtitleFont, PlainMuted, Align.Center);
        }

        return bitmap;
    }
}
