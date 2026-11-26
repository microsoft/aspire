// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Hex1b;
using Hex1b.Input;
using Hex1b.Surfaces;
using Hex1b.Theming;
using Hex1b.Tokens;
using Hex1b.Widgets;

namespace Aspire.Cli.Tui;

internal sealed class AdmireSplashApp
{
    internal const int FormationDurationMs = 4200;
    internal const int CaptionFadeDurationMs = 800;
    internal const int ExitStartMs = FormationDurationMs + CaptionFadeDurationMs + 2500;
    internal const int ExitStaggerMs = 1400;
    internal const int ExitTravelMs = 2600;
    internal const int TotalDurationMs = ExitStartMs + ExitStaggerMs + ExitTravelMs;
    private const int LogoWidth = 40;
    private const int LogoHeight = 20;
    private readonly string _version;
    private readonly BrailleDot[] _dots;
    private readonly Particle[] _particles;
    private MousePosition? _mousePosition;

    public AdmireSplashApp(string version)
    {
        _version = version;
        _dots = CreateDots();
        _particles = _dots.Select(_ => new Particle()).ToArray();
    }

    internal void MoveMouse(int x, int y) => Volatile.Write(ref _mousePosition, new MousePosition(x, y));

    public async Task RunAsync(Hex1bTerminalBuilder builder, CancellationToken cancellationToken)
    {
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        await using var terminal = builder.WithMouse().AddPresentationFilter(new MouseTrackingFilter(this)).WithHex1bFlow(async flow =>
        {
            using var flowStopping = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token, flow.CancellationToken);
            var start = Stopwatch.GetTimestamp();
            var step = flow.Step(ctx => Build(ctx, Stopwatch.GetElapsedTime(start).TotalMilliseconds)
                .InputBindings(bindings => bindings.Ctrl().Key(Hex1bKey.C).Action(_ => stopping.Cancel())),
                options => options.EnableMouse = true);

            while (Stopwatch.GetElapsedTime(start).TotalMilliseconds < TotalDurationMs)
            {
                await Task.Delay(16, flowStopping.Token);
                step.Invalidate();
            }

            // The particles have returned to the mouse. Freeze the empty viewport,
            // reserving one row for the shell prompt in the normal buffer.
            await step.CompleteAsync(ctx => ctx.VStack(_ =>
                [Build(ctx, TotalDurationMs).FixedWidth(flow.TerminalWidth)
                    .FixedHeight(Math.Max(1, flow.TerminalHeight - 1))]), flowStopping.Token);
        }, options =>
        {
            options.UseSoftWrapTombstones = true;
            // The builder queries the initial cursor before entering raw mode.
            // A second Console.GetCursorPosition query at flow startup can deadlock
            // with the terminal input pump on Unix, which shares stdin.
            options.CursorRowProvider = () => null;
        }).Build();

        await terminal.RunAsync(stopping.Token);
    }

    private Hex1bWidget Build(RootContext ctx, double elapsedMs)
    {
        return ctx.Surface(layers => [layers.Layer(surface => Render(surface, elapsedMs))]).Fill();
    }

    internal void Render(Surface surface, double elapsedMs)
    {
        var scale = Math.Min(1f, Math.Min(surface.Width / (float)LogoWidth,
            Math.Max(0, surface.Height - 3) / (float)LogoHeight));
        var width = (int)(LogoWidth * scale);
        var height = (int)(LogoHeight * scale);
        var offsetX = (surface.Width - width) / 2;
        var offsetY = Math.Max(0, (surface.Height - height - 2) / 2);
        var mouse = Volatile.Read(ref _mousePosition);
        var mouseX = Math.Clamp(mouse?.X ?? surface.Width / 2, 0, Math.Max(0, surface.Width - 1)) * 2f;
        var mouseY = Math.Clamp(mouse?.Y ?? surface.Height / 2, 0, Math.Max(0, surface.Height - 1)) * 4f;
        var cells = new Dictionary<(int X, int Y), (int Pattern, int R, int G, int B, int Count)>();

        ReadOnlySpan<int> bits = [0, 1, 2, 6, 3, 4, 5, 7];
        for (var i = 0; i < _dots.Length; i++)
        {
            var dot = _dots[i];
            var particle = _particles[i];
            var launchDelay = dot.Stagger * 1000;
            if (elapsedMs < launchDelay)
            {
                continue;
            }

            if (!particle.Spawned)
            {
                particle.OriginX = mouseX;
                particle.OriginY = mouseY;
                particle.Spawned = true;
            }

            var finalX = dot.X * scale + offsetX * 2;
            var finalY = dot.Y * scale + offsetY * 4;
            var exitProgress = Math.Clamp((float)((elapsedMs - ExitStartMs - dot.Stagger * ExitStaggerMs) / ExitTravelMs), 0, 1);
            if (exitProgress >= 1)
            {
                continue;
            }

            if (exitProgress == 0)
            {
                var progress = Math.Clamp((float)((elapsedMs - launchDelay) / (FormationDurationMs - launchDelay)), 0, 1);
                var eased = 1 - MathF.Pow(1 - progress, 3);
                var spray = MathF.Sin(progress * MathF.PI) * dot.StartRadius * scale;
                particle.X = particle.OriginX + (finalX - particle.OriginX) * eased + spray * MathF.Cos(dot.StartAngle);
                particle.Y = particle.OriginY + (finalY - particle.OriginY) * eased + spray * MathF.Sin(dot.StartAngle);
            }
            else
            {
                if (particle.ExitProgress == 0)
                {
                    particle.X = finalX;
                    particle.Y = finalY;
                }

                // Apply the remaining cubic trajectory incrementally. Retargeting
                // never teleports a particle, even when the mouse moves mid-flight.
                var remaining = (1 - exitProgress) / (1 - particle.ExitProgress);
                var steering = 1 - remaining * remaining * remaining;
                particle.X += (mouseX - particle.X) * steering;
                particle.Y += (mouseY - particle.Y) * steering;
                particle.ExitProgress = exitProgress;
            }

            var bx = (int)MathF.Round(particle.X);
            var by = (int)MathF.Round(particle.Y);
            if (bx < 0 || bx >= surface.Width * 2 || by < 0 || by >= surface.Height * 4 || scale == 0)
            {
                continue;
            }

            // Unicode braille packs two columns of four dots into one cell:
            // left bits 0,1,2,6; right bits 3,4,5,7 (U+2800 + the mask).
            // https://www.unicode.org/charts/PDF/U2800.pdf
            var bit = 1 << bits[bx % 2 * 4 + by % 4];
            var key = (bx / 2, by / 4);
            cells.TryGetValue(key, out var cell);
            cells[key] = (cell.Pattern | bit, cell.R + dot.R, cell.G + dot.G, cell.B + dot.B, cell.Count + 1);
        }

        foreach (var ((x, y), cell) in cells)
        {
            surface.WriteChar(x, y, (char)(0x2800 | cell.Pattern),
                Hex1bColor.FromRgb((byte)(cell.R / cell.Count), (byte)(cell.G / cell.Count), (byte)(cell.B / cell.Count)));
        }

        var fade = Math.Clamp((float)((elapsedMs - FormationDurationMs) / CaptionFadeDurationMs), 0, 1);
        fade *= 1 - Math.Clamp((float)((elapsedMs - ExitStartMs) / CaptionFadeDurationMs), 0, 1);
        if (fade > 0)
        {
            var caption = $"Aspire {_version}";
            var x = Math.Max(0, (surface.Width - caption.Length) / 2);
            var y = Math.Min(surface.Height - 1, offsetY + height + 1);
            var intensity = (byte)(230 * fade);
            surface.WriteText(x, y, caption, Hex1bColor.FromRgb(intensity, intensity, intensity));
        }
    }

    private static BrailleDot[] CreateDots()
    {
        var pixels = Convert.FromBase64String(PixelData);
        var dots = new List<BrailleDot>(pixels.Length / 5 * 4);
        var random = new Random(99);
        for (var i = 0; i < pixels.Length; i += 5)
        {
            for (var dy = 0; dy < 2; dy++)
            {
                for (var dx = 0; dx < 2; dx++)
                {
                    dots.Add(new BrailleDot(pixels[i] * 2 + dx, pixels[i + 1] * 2 + dy,
                        pixels[i + 2], pixels[i + 3], pixels[i + 4],
                        (float)(40 + random.NextDouble() * 60),
                        (float)(random.NextDouble() * Math.PI * 2),
                        (float)random.NextDouble()));
                }
            }
        }

        return dots.ToArray();
    }

    private readonly record struct BrailleDot(int X, int Y, byte R, byte G, byte B,
        float StartRadius, float StartAngle, float Stagger);

    private sealed record MousePosition(int X, int Y);

    private sealed class Particle
    {
        public float OriginX;
        public float OriginY;
        public float X;
        public float Y;
        public float ExitProgress;
        public bool Spawned;
    }

    internal sealed class MouseTrackingFilter(AdmireSplashApp app) : IHex1bTerminalPresentationFilter
    {
        public ValueTask OnInputAsync(IReadOnlyList<AnsiToken> tokens, TimeSpan elapsed, CancellationToken ct = default)
        {
            // SGR motion arrives as ESC[<35;column;rowM. Hex1b decodes the
            // one-based wire coordinates into zero-based terminal coordinates.
            // https://invisible-island.net/xterm/ctlseqs/ctlseqs.html#h3-Mouse-Tracking
            foreach (var token in tokens)
            {
                if (token is SgrMouseToken mouse)
                {
                    app.MoveMouse(mouse.X, mouse.Y);
                }
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask OnSessionStartAsync(int width, int height, DateTimeOffset timestamp, CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask OnResizeAsync(int width, int height, TimeSpan elapsed, CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask OnSessionEndAsync(TimeSpan elapsed, CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask<IReadOnlyList<AnsiToken>> OnOutputAsync(IReadOnlyList<AppliedToken> tokens, TimeSpan elapsed, CancellationToken ct = default)
            => ValueTask.FromResult<IReadOnlyList<AnsiToken>>(tokens.Select(token => token.Token).ToArray());
    }

    // RGB logo pixels recovered from the Aspire monitor splash:
    // https://github.com/microsoft/aspire/pull/14402
    // Each five-byte record is x, y, red, green, blue on a 40x40 pixel grid.
    private const string PixelData = """
        EgMODg0TAyMeOhQDIx06FQMNDg0RBC0jTxIEWDnHEwRaNd8UBFo03xUEWDjHFgQtI08QBSYfPxEFYDjlEgVOJtgTBU8p0RQFTynRFQVOJtgWBWA45RcFJh4/
        DwYKCwkQBlU4uREGTyfaEgZWM9ITBm9P3BQGb0/cFQZWM9IWBk8m2hcGVTe5GAYJCgkPBzUnYxAHWjHmEQdOKM8SB21M2xMHd1jdFAd3WN0VB21M2xYHTijQ
        FwdaMeUYBzgqZg4IFRMaDwhYNswQCE0l1REIXjvXEgh2WN4TCHNU3RQIc1TdFQh2WN4WCF471xcITCXVGAhcOc8ZCBYVGw4JSTWLDwlWLeQQCVEs0REJcVHc
        Egl1Vt0TCXRV3RQJdFXdFQl1Vt0WCXFR3BcJUSzRGAlWLeMZCUUwhw0KIR0xDgpdN90PCksk0RAKZELZEQp3WN4SCnNU3RMKdFXdFAp0Vd0VCnNU3RYKd1je
        FwpkQtkYCkwl0hkKWzXaGgofGi8NC081pQ4LUinfDwtVMdMQC3RV3RELdFXdEgt0Vd0TC3RV3RQLdFXdFQt0Vd0WC3RV3RcLdFXdGAtWMdMZC1Ep3hoLVDmq
        GwsHBwQMDCogSQ0MWTHhDgxNJ9EPDGlH2hAMd1jeEQxzVN0SDHRV3RMMdFXdFAx0Vd0VDHRV3RYMc1TdFwx2WN4YDGlI2hkMTCbQGgxdNOQbDC4jTQsNCQkJ
        DA1WOL8NDU8n2g4NWjXVDw14Wd4QDXJT3RENdFXdEg10Vd0TDXRV3RQNdFXdFQ10Vd0WDXRV3RcNdFTdGA12V90ZDVo31RoNTibZGw1VNr4cDQkJCQsOOitt
        DA5YL+QNDk4p0A4OhGnhDw6YgeUQDoVq4REOclPdEg50Vt0TDnRV3RQOdFXdFQ50Vd0WDnRV3RcOdFXdGA52V90ZDm5N2xoOTyrQGw5YL+QcDjorbQoPFRMd
        Cw9cOdMMD0oi1A0Pb0/aDg+dh+YPD5eA5RAPmYPlEQ98X98SD3JT3RMPdFbdFA90Vd0VD3RV3RYPdFXdFw90Vd0YD3NU3RkPd1jeGg9gPdcbD0wm1BwPXDjT
        HQ8VEx0KEEcyjgsQVCvhDBBVMNINEJJ65A4QmILlDxCWf+UQEJmC5REQj3fjEhBzVN0TEHRV3RQQdFXdFRB0Vd0WEHRV3RcQdFXdGBB0Vd0ZEHVW3RoQcVLc
        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
        Ex25qu4UHbmq7hUduaruFh25qu4XHbmp7hgdu6zvGR2ijegaHZV95Bsdl4HlHB2XgOUdHZeA5R4dl4DlHx2XgOUgHZZ/5SEdnIbmIh10VtwjHUkh0iQdXjnb
        JR0fGy4CHko1iwMeUynjBB5iQNUFHtfP9QYe3df2Bx7b1PYIHtzV9gke3NX2Ch7c1fYLHtzV9gwe3NX2DR7c1fYOHtvU9g8e3tj2EB7Qx/MRHreo7hIeuavu
        Ex65qu4UHrmq7hUeuaruFh65qu4XHrmq7hgeuqvuGR60o+0aHpiB5Rsel4DlHB6XgOUdHpeA5R4el4DlHx6XgOUgHpeA5SEemIHlIh6VfeQjHlk20yQeVCrj
        JR5GMYcCHzkhjQMfSyDdBB+PdeEFH+nm+QYf2M/1Bx/b1PYIH9vU9gkf29T2Ch/b1PYLH9vU9gwf29T2DR/b1PYOH9vU9g8f29T2EB/b1PYRH7ut7xIft6ju
        Ex+4qe4UH7ip7hUfuKnuFh+4qe4XH7ip7hgfuKnuGR+7re4aH5+K5xsflH3lHB+WgOUdH5Z/5R4fln/lHx+Wf+UgH5Z/5SEflX7lIh+eiOYjH3BR2SQfUCfg
        JR85IY4CIEcwjwMgUCbhBCBpSdYFINbO9QYg4974ByDh2/cIIOLc9wkg4tz3CiDi3PcLIOLc9wwg4tz3DSDi3PcOIOHb9w8g4934ECDVzfURIL2u7xIgva/v
        EyC9r+8UIL2v7xUgva/vFiC9r+8XIL2v7xggvrDvGSC3p+0aIJyG5hsgmYPmHCCag+YdIJqD5h4gmoPmHyCag+YgIJqD5iEgmoTmIiCUfOUjIF060yQgUinh
        JSBGMI4CISwlQgMhYDnkBCFJIdMFIV470wYhc1baByFxU9kIIXFT2QkhcVPZCiFxU9kLIXFT2QwhcVPZDSFxU9kOIXFT2Q8hcVLZECFqSNcRIWlG1xIhaUfX
        EyFpR9cUIWlH1xUhaUfXFiFpR9cXIWlH1xghaUfXGSFjQdUaIWA+1BshYT/VHCFhP9UdIWE/1R4hYT/VHyFhP9UgIWE+1SEhYkDVIiFXM9EjIUsk0yQhXzjk
        JSEsJEIDIjwvaAQiXDfbBSJVK+MGIk8l4QciUCXiCCJQJeIJIlAl4goiUCXiCyJQJeIMIlAl4g0iUCXiDiJQJeIPIlAl4hAiUifiESJSJ+ISIlIn4hMiUifi
        FCJSJ+IVIlIn4hYiUifiFyJSJ+IYIlIn4hkiUynjGiJUKuMbIlMp4xwiUynjHSJTKeMeIlMp4x8iUynjICJUKeMhIlMp4iIiVi3jIyJcNtskIjwvaAQjGRUm
        BSMzI2UGIysbZQcjLBpkCCMsGmUJIywaZQojLBplCyMsGmUMIywaZQ0jLBplDiMsGmUPIywaZRAjLBpkESMsGmQSIywaZBMjLBpkFCMsGmQVIywaZBYjLBpk
        FyMsGmQYIywaZBkjKxlkGiMrGWQbIysZZBwjKxlkHSMrGWQeIysZZB8jKxlkICMrGWQhIysaZSIjMiNlIyMaFiY=
        """;
}
