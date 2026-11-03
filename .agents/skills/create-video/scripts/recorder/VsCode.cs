using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace Recorder;

/// <summary>
/// VS Code in the browser, served by "code serve-web --port 8123 --without-connection-token", for clips that show code
/// being written or commands run in its terminal. See "Code in VS Code, and a live terminal" in references/recording.md,
/// and the VS Code section of references/troubleshooting.md.
/// </summary>
public static class VsCode
{
    /// <summary>The address of VS Code: VIDEO_VSCODE_URL, http://localhost:8123 by default.</summary>
    public static string Url { get; } = Environment.GetEnvironmentVariable("VIDEO_VSCODE_URL") ?? "http://localhost:8123";

    /// <summary>
    /// The user settings written into VS Code before each scene: vscode-settings.jsonc, next to recorder.cs, or the file
    /// named by VIDEO_VSCODE_SETTINGS. Its comments say why it exists and what each group of settings is for. VS Code
    /// reads user settings as JSON with comments, so the file is written as is.
    /// </summary>
    public static string SettingsPath { get; } = Environment.GetEnvironmentVariable("VIDEO_VSCODE_SETTINGS") is { Length: > 0 } path
        ? path
        : Path.Combine((string)AppContext.GetData("EntryPointFileDirectoryPath")!, "vscode-settings.jsonc");

    /// <summary>
    /// Opens a folder in VS Code, before the capture: writes the user settings, waits for the explorer, then closes the
    /// editors and notifications that VS Code opens on its own. Create the scene with <see cref="Url"/> as its base URL.
    /// </summary>
    public static async Task OpenAsync(Scene scene, string folder)
    {
        // The user settings of VS Code in the browser live in the browser's IndexedDB, not on the server: writes them
        // from a page of the VS Code origin (/manifest.json works, even as a 404) before the workbench loads. They run
        // the TypeScript extension on the server (in the browser, it runs in a partial mode that doesn't resolve the
        // generated Aspire modules), and turn off Restricted Mode, where it doesn't run.
        await scene.Page.GotoAsync(Url + "/manifest.json");
        await scene.Page.EvaluateAsync("""
            async settings => {
              const db = await new Promise((resolve, reject) => {
                const request = indexedDB.open('vscode-web-db', 3);
                request.onupgradeneeded = () => {
                  for (const store of ['vscode-userdata-store', 'vscode-logs-store', 'vscode-filehandles-store'])
                    if (!request.result.objectStoreNames.contains(store)) request.result.createObjectStore(store);
                };
                request.onsuccess = () => resolve(request.result);
                request.onerror = () => reject(request.error);
              });
              await new Promise((resolve, reject) => {
                const transaction = db.transaction('vscode-userdata-store', 'readwrite');
                transaction.objectStore('vscode-userdata-store').put(new TextEncoder().encode(settings), '/User/settings.json');
                transaction.oncomplete = resolve;
                transaction.onerror = () => reject(transaction.error);
              });
              db.close();
            }
            """, File.ReadAllText(SettingsPath));
        await scene.OpenAsync("/?folder=" + Uri.EscapeDataString(folder));
        await scene.Page.Locator(".monaco-workbench").WaitForAsync();
        await scene.Page.GetByRole(AriaRole.Treeitem).First.WaitForAsync(new() { Timeout = 60_000 });
        await Task.Delay(2500);

        await scene.Page.Keyboard.PressAsync("Escape");
        await RunCommandAsync(scene.Page, "View: Close All Editors");
        await RunCommandAsync(scene.Page, "Notifications: Clear All Notifications");
        await Task.Delay(800);

        if (Environment.GetEnvironmentVariable("VIDEO_DEBUG_SHOT") is { Length: > 0 } shot)
        {
            await scene.Page.ScreenshotAsync(new() { Path = shot });
        }
    }

    /// <summary>Runs a command of the command palette (F1), such as "View: Toggle Maximized Panel".</summary>
    public static async Task RunCommandAsync(IPage page, string command)
    {
        await page.Keyboard.PressAsync("F1");
        await page.Keyboard.TypeAsync(command);
        await Task.Delay(500);
        await page.Keyboard.PressAsync("Enter");
        await Task.Delay(900);
    }

    /// <summary>Opens a file of the explorer, by its name.</summary>
    public static Task OpenFileAsync(Scene scene, string name, int pauseAfter = 1200)
        => scene.ClickAsync(scene.Page.GetByRole(AriaRole.Treeitem, new() { Name = name }), pauseAfter: pauseAfter);

    /// <summary>Opens the terminal, with the profile of the settings.</summary>
    public static async Task OpenTerminalAsync(Scene scene)
    {
        await scene.Page.Keyboard.PressAsync("Control+Backquote");
        await scene.Page.Locator(".xterm-rows").First.WaitForAsync();
        await Task.Delay(1500);
    }

    /// <summary>Types a command in the terminal, visibly, and runs it.</summary>
    public static async Task TypeInTerminalAsync(Scene scene, string command)
    {
        await scene.Page.Keyboard.TypeAsync(command, new() { Delay = 80 });
        await scene.PauseAsync(350);
        await scene.Page.Keyboard.PressAsync("Enter");
    }

    /// <summary>
    /// Waits until the terminal shows a text, such as "Pipeline succeeded". Use a text of the output that the command
    /// line doesn't contain: the typed command is in the terminal too, and would match at once.
    /// </summary>
    public static Task WaitForTerminalAsync(Scene scene, string text, int timeoutMilliseconds = 120_000)
        => scene.Page.Locator(".xterm-rows").GetByText(text).First.WaitForAsync(new() { Timeout = timeoutMilliseconds });

    /// <summary>
    /// The row of the terminal that matches a pattern, to highlight. The terminal draws its text in rows of
    /// ".xterm-rows > div" (with "terminal.integrated.gpuAcceleration": "off"); a link is a separate span of its row.
    /// </summary>
    public static ILocator TerminalRow(Scene scene, Regex pattern)
        => scene.Page.Locator(".xterm-rows > div").Filter(new() { HasTextRegex = pattern }).Last;

    /// <summary>
    /// Opens a link printed in the terminal, such as the dashboard's URL that "aspire run" prints, and returns its
    /// page: Cmd+click on macOS, Ctrl+click elsewhere. Record it with <see cref="Scene.SwitchToAsync"/>.
    /// </summary>
    public static async Task<IPage> OpenTerminalLinkAsync(Scene scene, Regex pattern)
    {
        var link = scene.Page.Locator(".xterm-rows span").Filter(new() { HasTextRegex = pattern }).First;
        await scene.MoveToAsync(link);
        await scene.PauseAsync(500);
        var popup = scene.Page.Context.WaitForPageAsync();
        var (x, y) = await scene.CenterOfAsync(link);
        await scene.Page.Keyboard.DownAsync("ControlOrMeta");
        await scene.Page.Mouse.ClickAsync((float)x, (float)y);
        await scene.Page.Keyboard.UpAsync("ControlOrMeta");

        // VS Code asks before opening an external link, in some versions.
        var open = scene.Page.GetByRole(AriaRole.Button, new() { Name = "Open", Exact = true });
        try
        {
            await open.WaitForAsync(new() { Timeout = 1500 });
            await open.ClickAsync();
        }
        catch (TimeoutException)
        {
        }

        return await popup;
    }

    /// <summary>
    /// Types the start of a member, waits for IntelliSense, accepts its suggestion, and types the rest, such as
    /// ".withRef" then "(cache)". After an identifier, press Escape first: the list of words of the identifier would
    /// swallow the dot.
    /// </summary>
    public static async Task TypeWithSuggestionAsync(Scene scene, string start, string rest)
    {
        await scene.Page.Keyboard.TypeAsync(start, new() { Delay = 85 });
        var widget = scene.Page.Locator(".suggest-widget.visible");

        try
        {
            await widget.WaitForAsync(new() { Timeout = 2500 });
        }
        catch (TimeoutException)
        {
            // The language server wasn't ready when the dot was typed: asks for the suggestions.
            await scene.Page.Keyboard.PressAsync("Control+Space");
            await widget.WaitForAsync(new() { Timeout = 10_000 });
        }

        await scene.PauseAsync(900);
        await scene.Page.Keyboard.PressAsync("Tab");
        await scene.PauseAsync(250);
        await scene.Page.Keyboard.TypeAsync(rest, new() { Delay = 85 });
        await scene.PauseAsync(300);
    }

    // The box of lines first..last of the editor, from the line numbers, in CSS pixels.
    private static Task<double[]> LinesBoxAsync(Scene scene, int first, int last)
        => scene.Page.EvaluateAsync<double[]>("""
            ([first, last]) => {
              const editor = document.querySelector('.editor-instance .monaco-editor');
              const numbers = [...editor.querySelectorAll('.line-numbers')];
              const top = numbers.find(n => n.textContent.trim() === String(first)).getBoundingClientRect();
              const bottom = numbers.find(n => n.textContent.trim() === String(last)).getBoundingClientRect();
              const lines = editor.querySelector('.view-lines').getBoundingClientRect();
              const rows = [...editor.querySelectorAll('.view-line')].filter(l => {
                const r = l.getBoundingClientRect(); return r.top >= top.top - 1 && r.bottom <= bottom.bottom + 1; });
              const width = Math.max(...rows.map(l => l.firstElementChild ? l.firstElementChild.getBoundingClientRect().right : lines.left)) - lines.left;
              return [lines.left, top.top, width, bottom.bottom - top.top];
            }
            """, new[] { first, last });

    /// <summary>
    /// Draws a ring around lines first..last of the editor (1-based, as numbered), with an optional caption on their
    /// right (or above, when there's no room), and zooms on them like <see cref="Scene.HighlightAsync"/>.
    /// </summary>
    public static async Task HighlightLinesAsync(Scene scene, int first, int last, string caption = null)
    {
        await scene.ClearHighlightsAsync();
        var box = await LinesBoxAsync(scene, first, last);
        await scene.MoveToAsync(box[0] + box[2] + 30, box[1] + box[3] / 2);
        var shown = await scene.Page.EvaluateAsync<double[]>("""
            ({ x, y, w, h, caption }) => {
              const p = 6;
              const ring = document.createElement('div');
              ring.className = 'demo-highlight';
              Object.assign(ring.style, { left: (x - p) + 'px', top: (y - p) + 'px', width: (w + 2 * p) + 'px', height: (h + 2 * p) + 'px' });
              document.documentElement.appendChild(ring);
              let [left, top, right, bottom] = [x - p, y - p, x + w + p, y + h + p];
              if (caption) {
                const label = document.createElement('div');
                label.className = 'demo-caption';
                label.textContent = caption;
                document.documentElement.appendChild(label);
                const after = x + w + 2 * p + 12;
                if (after + label.offsetWidth + 8 < window.innerWidth) {
                  Object.assign(label.style, { left: after + 'px', top: (y + h / 2 - label.offsetHeight / 2) + 'px' });
                } else {
                  Object.assign(label.style, { left: (x - p) + 'px', top: (y - p - label.offsetHeight - 8) + 'px' });
                }
                const r = label.getBoundingClientRect();
                [left, top, right, bottom] = [Math.min(left, r.left), Math.min(top, r.top), Math.max(right, r.right), Math.max(bottom, r.bottom)];
              }
              return [left, top, right - left, bottom - top];
            }
            """, new { x = box[0], y = box[1], w = box[2], h = box[3], caption });

        if (scene.AutoZoom)
        {
            scene.ZoomTo(shown[0], shown[1], shown[2], shown[3]);
        }
    }

    /// <summary>Clicks at the end of a line of the editor (1-based), to type after it.</summary>
    public static async Task ClickLineEndAsync(Scene scene, int line)
    {
        var box = await LinesBoxAsync(scene, line, line);
        await scene.MoveToAsync(box[0] + box[2] + 40, box[1] + box[3] / 2);
        await scene.PauseAsync(150);
        await scene.Page.Mouse.ClickAsync((float)(box[0] + box[2] + 40), (float)(box[1] + box[3] / 2));
        await scene.Page.Keyboard.PressAsync("End");
        await scene.PauseAsync(300);
    }

    /// <summary>
    /// Checks what was typed, once VS Code saved it ("files.autoSave"): a typing take can go wrong while the clip
    /// still records. exact: the whole file, rather than a part of it.
    /// </summary>
    public static async Task ExpectFileAsync(string path, string text, bool exact = false)
    {
        // Compares with "\n" line endings: the files can have "\r\n" on Windows.
        text = text.ReplaceLineEndings("\n");

        for (var attempt = 0; attempt < 20; attempt++)
        {
            await Task.Delay(250);
            var content = File.ReadAllText(path).ReplaceLineEndings("\n");

            if (exact ? content == text : content.Contains(text, StringComparison.Ordinal))
            {
                return;
            }
        }

        throw new InvalidOperationException($"{path} doesn't have what was typed:\n" + File.ReadAllText(path));
    }
}
