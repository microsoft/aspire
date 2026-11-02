using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Playwright;

namespace Recorder;

/// <summary>
/// Records one scene of a video: a browser page captured frame by frame through the DevTools screencast, with a
/// visible cursor, and the start time of each narrated step, so the narration can be laid over the video.
/// </summary>
public sealed class Scene : IAsyncDisposable
{
    public const int Width = 1536;
    public const int Height = 864;
    public const float Scale = 1.25f;

    // The cursor and the highlight ring, drawn in the page since a headless browser draws no cursor. Their colors
    // ({{ACCENT}}, {{CAPTION}}) are Aspire's, or neutral in a video with "branding": false (see Overlay).
    private const string OverlayScript = """
        (() => {
          if (window.__demoOverlay) return;
          window.__demoOverlay = true;
          const install = () => {
            if (document.getElementById('demo-cursor')) return;
            const style = document.createElement('style');
            style.textContent = `
              #demo-cursor { position: fixed; left: 0; top: 0; width: 26px; height: 26px; z-index: 2147483647; pointer-events: none;
                transform: translate(var(--x, -100px), var(--y, -100px)); transition: none; }
              #demo-cursor svg { filter: drop-shadow(0 2px 3px rgba(0,0,0,.45)); }
              .demo-ripple { position: fixed; z-index: 2147483646; pointer-events: none; width: 34px; height: 34px; margin: -17px 0 0 -17px;
                border-radius: 50%; border: 3px solid {{ACCENT}}; background: rgba({{ACCENT_RGB}},.3); animation: demo-ripple .6s ease-out forwards; }
              @keyframes demo-ripple { from { transform: scale(.3); opacity: 1 } to { transform: scale(1.6); opacity: 0 } }
              .demo-highlight { position: fixed; z-index: 2147483645; pointer-events: none; border: 3px solid {{ACCENT}}; border-radius: 10px;
                box-shadow: 0 0 0 4px rgba({{ACCENT_RGB}},.3), 0 0 24px rgba({{ACCENT_RGB}},.6); transition: opacity .3s; }
              .demo-caption { position: fixed; z-index: 2147483645; pointer-events: none; background: {{CAPTION}}; color: #ffffff; font: 600 15px/1.3 system-ui, sans-serif;
                padding: 6px 10px; border-radius: 6px; box-shadow: 0 4px 14px rgba(0,0,0,.35); max-width: 360px; }`;
            document.documentElement.appendChild(style);
            const cursor = document.createElement('div');
            cursor.id = 'demo-cursor';
            cursor.innerHTML = '<svg width="26" height="26" viewBox="0 0 26 26"><path d="M3 2 L3 21 L8.5 16 L12.5 24.5 L16 23 L12 14.8 L19.5 14.8 Z" fill="#fff" stroke="#1a1a1d" stroke-width="1.6" stroke-linejoin="round"/></svg>';
            document.documentElement.appendChild(cursor);
            const saved = sessionStorage.getItem('demo-cursor');
            if (saved) { const [x, y] = saved.split(','); cursor.style.setProperty('--x', x + 'px'); cursor.style.setProperty('--y', y + 'px'); }
          };
          document.addEventListener('mousemove', (e) => {
            install();
            const cursor = document.getElementById('demo-cursor');
            cursor.style.setProperty('--x', e.clientX + 'px');
            cursor.style.setProperty('--y', e.clientY + 'px');
            sessionStorage.setItem('demo-cursor', e.clientX + ',' + e.clientY);
          }, true);
          document.addEventListener('mousedown', (e) => {
            install();
            const ripple = document.createElement('div');
            ripple.className = 'demo-ripple';
            ripple.style.left = e.clientX + 'px';
            ripple.style.top = e.clientY + 'px';
            document.documentElement.appendChild(ripple);
            setTimeout(() => ripple.remove(), 700);
          }, true);
          if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', install); else install();
        })();
        """;

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly List<(double Time, string File)> _frames = [];
    private readonly List<(string Id, double Time)> _steps = [];
    private readonly List<(double Time, double[] Box, double Scale)> _zooms = [];
    private readonly string _directory;
    private readonly IReadOnlyDictionary<string, double> _durations;
    private ICDPSession _cdp;
    private int _frameNumber;
    private double _cursorX = Width / 2.0;
    private double _cursorY = Height / 2.0;
    private bool _zoomed;

    private Scene(string directory, IReadOnlyDictionary<string, double> durations)
    {
        _directory = directory;
        _durations = durations;
    }

    public IBrowserContext Context { get; private set; }

    public IPage Page { get; private set; }

    public string BaseUrl { get; private set; }

    /// <summary>
    /// Zooms in slightly on what <see cref="HighlightAsync"/> and <see cref="TypeAsync"/> point at, until the step
    /// ends. assemble.py renders the zooms: see references/specs.md. Turn it off for a scene that must stay wide.
    /// </summary>
    public bool AutoZoom { get; set; } = true;

    /// <summary>
    /// The zoom of <see cref="ZoomAsync"/> and of the automatic zooms: slight, so the page keeps its context.
    /// </summary>
    public const double DefaultZoom = 1.25;

    public static async Task<Scene> StartAsync(IBrowser browser, string name, string baseUrl, string storageStatePath, string outputRoot, IReadOnlyDictionary<string, double> durations)
    {
        var directory = Path.Combine(outputRoot, name);

        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }

        Directory.CreateDirectory(Path.Combine(directory, "frames"));

        var scene = new Scene(directory, durations) { BaseUrl = baseUrl };
        scene.Context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = Width, Height = Height },
            DeviceScaleFactor = Scale,
            StorageStatePath = File.Exists(storageStatePath) ? storageStatePath : null,
            Locale = "en-US",
            ColorScheme = ColorScheme.Dark,
            // The dashboard and local services use the ASP.NET Core development certificate.
            IgnoreHTTPSErrors = true,
        });
        await scene.Context.AddInitScriptAsync(Overlay(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(outputRoot))!, "storyboard.json")));
        scene.Page = await scene.Context.NewPageAsync();
        scene.Page.SetDefaultTimeout(20_000);

        return scene;
    }

    // The overlay in the colors of the video: Aspire's purple, or, when the storyboard says "branding": false, white
    // rings and ripples and black captions, with no Aspire color.
    private static string Overlay(string storyboardPath)
    {
        var branded = true;

        if (File.Exists(storyboardPath)
            && JsonNode.Parse(File.ReadAllText(storyboardPath))?["branding"] is JsonValue branding
            && branding.TryGetValue<bool>(out var value))
        {
            branded = value;
        }

        return branded
            ? OverlayScript.Replace("{{ACCENT}}", "#7455DD").Replace("{{ACCENT_RGB}}", "116,85,221").Replace("{{CAPTION}}", "#512BD4")
            : OverlayScript.Replace("{{ACCENT}}", "#FFFFFF").Replace("{{ACCENT_RGB}}", "255,255,255").Replace("{{CAPTION}}", "rgba(0,0,0,.85)");
    }

    /// <summary>
    /// Navigates before the capture starts, so the scene opens on a loaded page.
    /// </summary>
    public async Task OpenAsync(string path)
    {
        await Page.GotoAsync(BaseUrl + path, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
        await Page.Mouse.MoveAsync((float)_cursorX, (float)_cursorY);
        await Task.Delay(400);
    }

    /// <summary>
    /// Navigates during the capture, and keeps the screencast going on the new document.
    /// </summary>
    public async Task NavigateAsync(string path)
    {
        ZoomOut();
        await Page.GotoAsync(BaseUrl + path, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
        await RestartScreencastAsync();
        await NudgeAsync();
    }

    // Clicks something that navigates, then keeps the screencast going on the new document.
    public async Task ClickAndWaitForNavigationAsync(ILocator locator)
    {
        await MoveToAsync(locator);
        await Task.Delay(180);
        ZoomOut();
        await Page.RunAndWaitForNavigationAsync(() => Page.Mouse.ClickAsync((float)_cursorX, (float)_cursorY), new PageRunAndWaitForNavigationOptions { WaitUntil = WaitUntilState.NetworkIdle });
        await RestartScreencastAsync();
        await NudgeAsync();
        await Task.Delay(300);
    }

    /// <summary>
    /// Runs an action that navigates, such as a form submitted by a key or a menu item, and keeps capturing the new document.
    /// </summary>
    public async Task WaitForNavigationAsync(Func<Task> action)
    {
        ZoomOut();
        await Page.RunAndWaitForNavigationAsync(action, new PageRunAndWaitForNavigationOptions { WaitUntil = WaitUntilState.NetworkIdle });
        await RestartScreencastAsync();
        await NudgeAsync();
        await Task.Delay(300);
    }

    private async Task RestartScreencastAsync()
    {
        if (_cdp is null)
        {
            return;
        }

        try
        {
            await _cdp.SendAsync("Page.stopScreencast");
        }
        catch (PlaywrightException)
        {
        }

        await _cdp.SendAsync("Page.startScreencast", ScreencastOptions());
    }

    /// <summary>
    /// Captures another page of the context from now on, for instance a tab opened by a link.
    /// </summary>
    public async Task SwitchToAsync(IPage page)
    {
        if (_cdp is not null)
        {
            try
            {
                await _cdp.SendAsync("Page.stopScreencast");
            }
            catch (PlaywrightException)
            {
            }

            await _cdp.DetachAsync();
        }

        ZoomOut();
        Page = page;
        await Page.BringToFrontAsync();
        await Page.SetViewportSizeAsync(Width, Height);
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        BaseUrl = new Uri(Page.Url).GetLeftPart(UriPartial.Authority);
        await StartCaptureCoreAsync();
        await NudgeAsync();
    }

    private static Dictionary<string, object> ScreencastOptions() => new()
    {
        ["format"] = "jpeg",
        ["quality"] = 92,
        ["maxWidth"] = (int)(Width * 1.125),
        ["maxHeight"] = (int)(Height * 1.125),
        ["everyNthFrame"] = 1,
    };

    public async Task StartCaptureAsync()
    {
        await StartCaptureCoreAsync();

        // A first frame, then a still moment before the narration starts.
        await NudgeAsync();
        await Task.Delay(600);
    }

    private async Task StartCaptureCoreAsync()
    {
        _cdp = await Context.NewCDPSessionAsync(Page);
        _cdp.Event("Page.screencastFrame").OnEvent += (_, args) =>
        {
            var data = args.Value.GetProperty("data").GetString();
            var sessionId = args.Value.GetProperty("sessionId").GetInt32();
            var file = Path.Combine(_directory, "frames", $"{_frameNumber++:D6}.jpg");
            File.WriteAllBytes(file, Convert.FromBase64String(data));

            lock (_frames)
            {
                _frames.Add((_clock.Elapsed.TotalSeconds, file));
            }

            _ = _cdp.SendAsync("Page.screencastFrameAck", new Dictionary<string, object> { ["sessionId"] = sessionId });
        };

        await _cdp.SendAsync("Page.startScreencast", ScreencastOptions());
    }

    /// <summary>
    /// Runs the actions of a narrated step, then holds until the step's narration is over.
    /// </summary>
    public async Task StepAsync(string id, Func<Task> actions = null)
    {
        var start = _clock.Elapsed.TotalSeconds;
        ZoomOut();
        _steps.Add((id, start));
        Console.WriteLine($"  step {id} at {start:F1}s");

        if (actions is not null)
        {
            await actions();
        }

        var narration = _durations.TryGetValue(id, out var seconds) ? seconds : 2;
        var remaining = start + narration + 0.45 - _clock.Elapsed.TotalSeconds;

        if (remaining > 0)
        {
            await HoldAsync(remaining);
        }
    }

    // Keeps frames coming while nothing changes, so the screencast doesn't stall.
    public async Task HoldAsync(double seconds)
    {
        var until = _clock.Elapsed.TotalSeconds + seconds;

        while (_clock.Elapsed.TotalSeconds < until)
        {
            await Task.Delay((int)Math.Min(400, Math.Max(1, (until - _clock.Elapsed.TotalSeconds) * 1000)));
        }
    }

    public Task PauseAsync(int milliseconds) => Task.Delay(milliseconds);

    /// <summary>
    /// Sends requests to the app, before the capture, so the newest logs and traces are those the narration talks
    /// about. Uses HTTP/1.1: curl's HTTP/2 over TLS makes Kestrel log errors when it closes connections.
    /// </summary>
    public async Task RequestAsync(string url, int count = 10)
    {
        for (var i = 0; i < count; i++)
        {
            var response = await Context.APIRequest.GetAsync(url);

            if (!response.Ok)
            {
                throw new InvalidOperationException($"{url} returned {response.Status}.");
            }
        }

        // Lets the telemetry reach the dashboard.
        await Task.Delay(2500);
    }

    public async Task MoveToAsync(double x, double y, int steps = 25)
    {
        var fromX = _cursorX;
        var fromY = _cursorY;

        for (var i = 1; i <= steps; i++)
        {
            // Ease in and out.
            var t = i / (double)steps;
            var eased = t < .5 ? 2 * t * t : 1 - Math.Pow(-2 * t + 2, 2) / 2;
            _cursorX = fromX + (x - fromX) * eased;
            _cursorY = fromY + (y - fromY) * eased;
            await Page.Mouse.MoveAsync((float)_cursorX, (float)_cursorY);
            await Task.Delay(14);
        }
    }

    public async Task<(double X, double Y)> CenterOfAsync(ILocator locator)
    {
        var box = await BoxOfAsync(locator) ?? throw new InvalidOperationException($"No box for {locator}.");

        return (box.X + box.Width / 2, box.Y + box.Height / 2);
    }

    /// <summary>
    /// The box of an element, scrolled into view. An element with "display: contents", such as a row of the
    /// dashboard's grids, has no box of its own: its box is the union of its children's.
    /// </summary>
    public async Task<LocatorBoundingBoxResult> BoxOfAsync(ILocator locator)
    {
        var box = await locator.EvaluateAsync<double[]>("""
            element => {
              const boxes = [];
              const walk = e => {
                if (getComputedStyle(e).display === 'contents') { [...e.children].forEach(walk); return; }
                if (boxes.length === 0) e.scrollIntoView({ block: 'nearest', inline: 'nearest' });
                const r = e.getBoundingClientRect();
                if (r.width > 0 && r.height > 0) boxes.push(r);
              };
              walk(element);
              if (boxes.length === 0) return null;
              const left = Math.min(...boxes.map(r => r.left)), top = Math.min(...boxes.map(r => r.top));
              const right = Math.max(...boxes.map(r => r.right)), bottom = Math.max(...boxes.map(r => r.bottom));
              return [left, top, right - left, bottom - top];
            }
            """);

        return box is null ? null : new LocatorBoundingBoxResult { X = (float)box[0], Y = (float)box[1], Width = (float)box[2], Height = (float)box[3] };
    }

    public async Task MoveToAsync(ILocator locator, double offsetX = 0, double offsetY = 0)
    {
        var (x, y) = await CenterOfAsync(locator);
        await MoveToAsync(x + offsetX, y + offsetY);
    }

    public async Task ClickAsync(ILocator locator, double offsetX = 0, double offsetY = 0, int pauseAfter = 500)
    {
        await MoveToAsync(locator, offsetX, offsetY);
        await Task.Delay(180);
        await Page.Mouse.ClickAsync((float)_cursorX, (float)_cursorY);
        await Task.Delay(pauseAfter);
    }

    public async Task ClickIfVisibleAsync(ILocator locator, int pauseAfter = 500)
    {
        if (await locator.IsVisibleAsync())
        {
            await ClickAsync(locator, pauseAfter: pauseAfter);
        }
    }

    public async Task DoubleClickAsync(ILocator locator, int pauseAfter = 600)
    {
        await MoveToAsync(locator);
        await Task.Delay(180);
        await Page.Mouse.DblClickAsync((float)_cursorX, (float)_cursorY);
        await Task.Delay(pauseAfter);
    }

    public async Task RightClickAsync(ILocator locator, int pauseAfter = 600)
    {
        await MoveToAsync(locator);
        await Task.Delay(180);
        await Page.Mouse.ClickAsync((float)_cursorX, (float)_cursorY, new MouseClickOptions { Button = MouseButton.Right });
        await Task.Delay(pauseAfter);
    }

    public async Task DragAsync(ILocator from, double toX, double toY)
    {
        await MoveToAsync(from);
        await Page.Mouse.DownAsync();
        await MoveToAsync(toX, toY, 40);
        await Page.Mouse.UpAsync();
        await Task.Delay(400);
    }

    public async Task TypeAsync(ILocator locator, string text, int delay = 55, bool zoom = true)
    {
        if (zoom && AutoZoom)
        {
            await ZoomAsync(locator);
        }

        await ClickAsync(locator, pauseAfter: 200);
        await locator.PressSequentiallyAsync(text, new LocatorPressSequentiallyOptions { Delay = delay });
        await Task.Delay(300);
    }

    public async Task ScrollAsync(double deltaX, double deltaY, int steps = 12)
    {
        for (var i = 0; i < steps; i++)
        {
            await Page.Mouse.WheelAsync((float)(deltaX / steps), (float)(deltaY / steps));
            await Task.Delay(35);
        }

        await Task.Delay(300);
    }

    /// <summary>
    /// Draws a purple ring around an element, with an optional caption, until <see cref="ClearHighlightsAsync"/>.
    /// The caption goes below the element, or above it with captionAbove, for instance when the row below matters.
    /// </summary>
    public async Task HighlightAsync(ILocator locator, string caption = null, int padding = 6, bool captionAbove = false, bool zoom = true)
    {
        try
        {
            await locator.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 3000 });
        }
        catch (Exception ex) when (ex is TimeoutException or PlaywrightException)
        {
            Console.WriteLine($"  (nothing to highlight: {locator})");
            return;
        }

        var box = await BoxOfAsync(locator);

        if (box is null)
        {
            Console.WriteLine($"  (nothing to highlight: {locator})");
            return;
        }

        var shown = await Page.EvaluateAsync<double[]>("""
            ({ x, y, w, h, p, caption, above }) => {
              const ring = document.createElement('div');
              ring.className = 'demo-highlight';
              Object.assign(ring.style, { left: (x - p) + 'px', top: (y - p) + 'px', width: (w + 2 * p) + 'px', height: (h + 2 * p) + 'px' });
              document.documentElement.appendChild(ring);
              if (caption) {
                const label = document.createElement('div');
                label.className = 'demo-caption';
                label.textContent = caption;
                const below = !(above && y - p - 44 > 0) && y + h + p + 10 + 40 < window.innerHeight;
                Object.assign(label.style, { left: Math.max(8, Math.min(x - p, window.innerWidth - 380)) + 'px', top: (below ? y + h + p + 10 : y - p - 44) + 'px' });
                label.dataset.demo = 'caption';
                document.documentElement.appendChild(label);
                const r = label.getBoundingClientRect();
                const left = Math.min(x - p, r.left), top = Math.min(y - p, r.top);
                return [left, top, Math.max(x + w + p, r.right) - left, Math.max(y + h + p, r.bottom) - top];
              }
              return [x - p, y - p, w + 2 * p, h + 2 * p];
            }
            """, new { x = (double)box.X, y = (double)box.Y, w = (double)box.Width, h = (double)box.Height, p = padding, caption, above = captionAbove });

        if (zoom && AutoZoom && shown is not null)
        {
            ZoomTo(shown[0], shown[1], shown[2], shown[3]);
        }
    }

    /// <summary>
    /// Removes the highlights, and zooms back out.
    /// </summary>
    public async Task ClearHighlightsAsync()
    {
        await Page.EvaluateAsync("() => document.querySelectorAll('.demo-highlight, .demo-caption').forEach(e => e.remove())");
        ZoomOut();
    }

    /// <summary>
    /// Zooms in slightly on an element, until <see cref="ZoomOut"/>, the next zoom, a navigation, or the end of the
    /// step. The zoom is smaller when the element is too large to fit: a large element gets no zoom.
    /// </summary>
    public async Task ZoomAsync(ILocator locator, double scale = DefaultZoom)
    {
        var box = await BoxOfAsync(locator);

        if (box is not null)
        {
            ZoomTo(box.X, box.Y, box.Width, box.Height, scale);
        }
    }

    /// <summary>
    /// Zooms in slightly on a region of the page, in CSS pixels.
    /// </summary>
    public void ZoomTo(double x, double y, double width, double height, double scale = DefaultZoom)
    {
        _zooms.Add((_clock.Elapsed.TotalSeconds, [x, y, width, height], scale));
        _zoomed = true;
    }

    /// <summary>
    /// Zooms back out to the whole page.
    /// </summary>
    public void ZoomOut()
    {
        if (_zoomed)
        {
            _zooms.Add((_clock.Elapsed.TotalSeconds, null, 1));
            _zoomed = false;
        }
    }

    // Moves the cursor by a pixel, so the screencast sends a frame.
    public async Task NudgeAsync()
    {
        await Page.Mouse.MoveAsync((float)_cursorX + 1, (float)_cursorY);
        await Page.Mouse.MoveAsync((float)_cursorX, (float)_cursorY);
    }

    public async ValueTask DisposeAsync()
    {
        await HoldAsync(0.8);
        var end = _clock.Elapsed.TotalSeconds;

        if (_cdp is not null)
        {
            try
            {
                await _cdp.SendAsync("Page.stopScreencast");
            }
            catch (PlaywrightException)
            {
            }
        }

        await Task.Delay(300);

        List<(double Time, string File)> frames;

        lock (_frames)
        {
            frames = [.. _frames.OrderBy(frame => frame.Time)];
        }

        var origin = frames.Count > 0 ? frames[0].Time : 0;
        var timeline = new JsonObject
        {
            ["end"] = end - origin,
            ["frames"] = new JsonArray([.. frames.Select(frame => (JsonNode)new JsonObject { ["t"] = frame.Time - origin, ["file"] = Path.GetFileName(frame.File) })]),
            ["steps"] = new JsonArray([.. _steps.Select(step => (JsonNode)new JsonObject { ["id"] = step.Id, ["t"] = step.Time - origin })]),
            // The zooms, in CSS pixels of the viewport; a zoom without a box zooms back out.
            ["viewport"] = new JsonArray(Width, Height),
            ["zooms"] = new JsonArray([.. _zooms.Select(zoom => (JsonNode)new JsonObject
            {
                ["t"] = zoom.Time - origin,
                ["box"] = zoom.Box is null ? null : new JsonArray([.. zoom.Box.Select(value => (JsonNode)Math.Round(value, 1))]),
                ["scale"] = zoom.Scale,
            })]),
        };

        await File.WriteAllTextAsync(Path.Combine(_directory, "timeline.json"), timeline.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        await Context.CloseAsync();
        Console.WriteLine($"  {frames.Count} frames, {end - origin:F1}s");
    }
}
