# Recording

## Which Aspire runs the app

The video shows the Aspire that runs the sample app: its dashboard, its CLI, its integrations. Pick it in step 0:

- **The installed CLI** (the default): `aspire` on the `PATH`, with the channel it's set to (`aspire --version`).
  Templates follow the channel: a `daily` CLI creates apps on the next preview. When the CLI can't create or run
  the app it made (for example, `aspire run` finds no AppHost), update it (`aspire update --self`), pick the
  version explicitly (`aspire new ... --version <version>`), or run a C# AppHost directly (below).
- **A released version**: `aspire new ... --channel stable`, or `--version <version>`, so the video matches what
  users have.
- **The current branch** of the Aspire repository, to show a feature still in development. From the repository's
  root, build a local hive into its own folder, and put its CLI first on the `PATH` for every command that creates
  or runs the sample app (see "Creating a local Aspire build with `localhive`" in `docs/contributing.md`):

  ```bash
  ./localhive.sh -c Release -n video -o ./artifacts/localhive/video     # .\localhive.ps1 on Windows
  export PATH="$PWD/artifacts/localhive/video/bin:$PATH"
  aspire --version                                                        # the local build: a "local" version
  cd <workspace>                                                          # back to the workspace, outside the repository
  aspire new aspire-starter --channel video ...                           # the app uses the branch's packages
  ```

  The build takes several minutes, and must be redone after each change to the branch that the video shows. Keep
  the sample app in the video workspace, outside the repository, as always. Never say the version on camera:
  describe the feature as it is.

A C# AppHost also runs without the CLI: `dotnet run --project <AppHost> --launch-profile https` prints the same
dashboard login URL. Use it when the CLI can't run the app, with the packages the app already references.

## The app to record

Record a throwaway sample app, never an app with real data or real cloud resources:

- Create it in the workspace with the Aspire CLI, from the starter template unless the subject needs another:
  `aspire new aspire-starter --name VideoDemo --output <workspace>/app --use-redis-cache --test-framework None
  --localhost-tld false --suppress-agent-init --non-interactive`. Its resources are
  `apiservice`, `webfrontend` and, with `--use-redis-cache`, `cache`. Add the integrations the subject needs to its
  AppHost. The project resources keep the ports of their own `launchSettings.json` between runs, but the template
  picks the web frontend's HTTPS port at random (7000 to 7300): pin it, so the commands below reach it. In
  `VideoDemo.Web/Properties/launchSettings.json`, set the `https` profile's `applicationUrl` to
  `https://localhost:7027;http://localhost:5027`.
- Give the dashboard a fixed port, so `VIDEO_BASE_URL` stays the same between runs: in the AppHost's
  `Properties/launchSettings.json`, set the `https` profile's `applicationUrl` to
  `https://localhost:17010;http://localhost:15010`.
- Start it with `aspire run` from `<workspace>/app`, in the background. It prints the dashboard's login URL, with a
  token (`https://localhost:17010/login?t=...`): pass it to the recorder as `VIDEO_LOGIN_URL`, and run
  `recorder.cs setup`. The token changes on every run, so run `setup` again after restarting the app. Don't show the
  token on camera.
- Use realistic but fake data: names such as `apiservice`, `webfrontend`, `cache`, `catalogdb`; values such as
  `noreply@contoso.com`, `Contoso`, `sk_test_51HxQ2`. When the subject needs a cloud service, use its local emulator
  or container (`RunAsEmulator()`, `RunAsContainer()`), never a real subscription. Remove the containers afterwards.
- Let the app warm up before recording: wait until every resource is **Running** and healthy
  (`aspire wait <resource> --status healthy` for each one, or `aspire describe`), and generate some traffic (open the
  web frontend, call the API) so logs, traces and metrics have data.
- **Generate traffic over HTTP/1.1**: `curl -k --http1.1 https://localhost:7027/weather`, or `scene.RequestAsync` in
  a clip. Plain `curl` uses HTTP/2 over TLS, and when it closes the connection Kestrel logs errors ("The encryption
  operation failed"), which show as red error badges on the Resources page. If that happens, restart the app: the
  dashboard keeps telemetry in memory only.
- The starter's web frontend caches its output in Redis for a few seconds: of a burst of requests to `/weather`, only
  the first reaches the API. To show a trace across the three resources, pick the row that names `apiservice`.

### Other things to record

- **aspire.dev pages**: set `VIDEO_BASE_URL=https://aspire.dev`; no `setup` needed.
- **The Aspire CLI**: the recorder only records a browser. Show commands and their output as slides with a code
  panel (a `"code"` of the slide, titled "Terminal", with `"language": "bash"`), with the real output trimmed to what
  the narration talks about, or run them live in the terminal of VS Code in the browser (below).
- **Code in VS Code, and a live terminal**: run `code serve-web --port 8123 --without-connection-token
  --accept-server-license-terms --server-data-dir <workspace>/vscode-data --default-folder <app>` and record
  it with the `VsCode` helpers of the recorder (below): create the scene with `VsCode.Url` as its base URL, and open
  the app with `VsCode.OpenAsync(scene, <app>)`. Read the VS Code section of `references/troubleshooting.md` first:
  without the settings it writes (`vscode-settings.jsonc`), IntelliSense doesn't work. Its terminal profile starts
  with anonymous dashboard access, so `aspire run` in it prints a URL without a token; the profile is set for macOS
  (zsh), Linux (bash) and Windows (PowerShell), and was tested on macOS.

### Snapshots

Clips run in order, each one starting from the state the previous one left. Before recording, put the app in the
starting state (resources running, data seeded) with a `prepare` command that you add to the recorder's
`recorder.cs`, or with a script. To re-record one clip, make sure the app is in the state that clip expects; to
re-record everything, stop the app, remove its containers and volumes, and start it again.

## The recorder

`scripts/recorder` is a Playwright for .NET file-based app (`recorder.cs`, with `Scene.cs` and `Scenes.cs`), copied into
the workspace with the tools (`tools/recorder`). Add your clips to `Scenes.cs`. It builds into a short path in the
user's profile, `~/.aspire-video/recorder/<hash>/bin`, as Playwright's files exceed `MAX_PATH` under deep folders on
Windows.

```bash
export VIDEO_ROOT=<workspace>                    # where state.json, durations.json and clips/ are
export VIDEO_BASE_URL=https://localhost:17010    # the dashboard
export VIDEO_LOGIN_URL='https://localhost:17010/login?t=...'
dotnet run <tools>/recorder/recorder.cs -- install   # once: the browser of this Playwright version
dotnet run <tools>/recorder/recorder.cs -- setup     # logs in, and saves the session in state.json
dotnet run <tools>/recorder/recorder.cs -- probe /structuredlogs "() => document.title"   # inspects a page (PROBE_SHOT=x.png saves a screenshot)
dotnet run <tools>/recorder/recorder.cs -- record all   # or: ... record 02-resources 03-traces
```

In Git Bash on Windows, set `MSYS_NO_PATHCONV=1`, or paths such as `/structuredlogs` are turned into Windows paths.

The dashboard's pages: `/` (Resources), `/consolelogs`, `/structuredlogs`, `/traces`, `/metrics`. Its markup can
change between versions: check every selector with `probe` before recording, and prefer role and text locators
(`GetByRole`, `GetByText`) over CSS classes.

## The scene API (`Scene.cs`)

A clip method creates a scene, prepares the page, starts the capture, and runs one step per narrated step:

```csharp
await using var scene = await NewSceneAsync(browser, directory, durations);
await scene.OpenAsync("/");                       // not recorded
await scene.StartCaptureAsync();                  // recording starts

await scene.StepAsync("resources-2", async () =>
{
    await scene.ClickAsync(scene.Page.GetByRole(AriaRole.Link, new() { Name = "Console" }), pauseAfter: 900);
});
```

| Method | Use |
|---|---|
| `OpenAsync(path)` | Opens a page before the capture starts. |
| `StartCaptureAsync()` | Starts recording, after a still moment. |
| `StepAsync(id, actions)` | Marks the start of a step, runs its actions, and holds until its narration ends. Keep the actions shorter than the narration. |
| `MoveToAsync(locator, offsetX, offsetY)` | Moves the cursor smoothly to an element. |
| `ClickAsync(locator)` / `DoubleClickAsync` / `RightClickAsync` | Moves, then clicks, with a purple ripple. |
| `ClickAndWaitForNavigationAsync(locator)` | Clicks something that loads a new page, and keeps capturing it. |
| `WaitForNavigationAsync(action)` | Runs an action that loads a new page (a key press, a form submit). |
| `NavigateAsync(path)` | Goes to a page during the capture. |
| `TypeAsync(locator, text, delay, zoom: true)` | Zooms on a field, clicks it, and types into it, visibly. |
| `ScrollAsync(dx, dy)` / `DragAsync(locator, x, y)` | Scrolls, drags. |
| `HighlightAsync(locator, caption, captionAbove: false, zoom: true)` / `ClearHighlightsAsync()` | Draws a purple ring, with an optional caption, around what the narration talks about, and zooms on the ring and its caption; clearing zooms back out. The caption goes below, or above with `captionAbove`, when the row below matters. |
| `ZoomAsync(locator, scale)` / `ZoomTo(x, y, w, h)` / `ZoomOut()` | Zooms slightly on an element or a region (CSS pixels), without a highlight, such as the output of a command in a terminal; zooms back out. |
| `AutoZoom` | `false` keeps a scene wide: `HighlightAsync` and `TypeAsync` don't zoom. |
| `SwitchToAsync(page)` | Records another tab, such as an endpoint opened from the Resources page. |
| `PauseAsync(ms)` / `HoldAsync(seconds)` | Waits, while frames keep coming. |

### Helpers for VS Code and the dashboard

For clips in VS Code (`VsCode.cs`) and on the dashboard (`Dashboard.cs`), in any video's `Scenes.cs`:

| Helper | Use |
|---|---|
| `VsCode.OpenAsync(scene, folder)` | Writes the settings, opens a folder, and closes what VS Code opens on its own, before the capture. |
| `VsCode.OpenFileAsync(scene, name)` | Opens a file of the explorer. |
| `VsCode.HighlightLinesAsync(scene, first, last, caption)` | Rings lines of the editor, with a caption, and zooms on them. |
| `VsCode.ClickLineEndAsync(scene, line)` / `VsCode.TypeWithSuggestionAsync(scene, ".withRef", "(cache)")` | Types code with IntelliSense, as a person would. |
| `VsCode.ExpectFileAsync(path, text, exact)` | Checks what was typed, once saved: a bad take stops the recording. |
| `VsCode.OpenTerminalAsync(scene)` / `VsCode.TypeInTerminalAsync(scene, "aspire run")` / `VsCode.WaitForTerminalAsync(scene, text)` | Runs commands in its terminal, and waits for a text of their output that the command line doesn't contain. |
| `VsCode.TerminalRow(scene, pattern)` | A row of the terminal, to highlight. |
| `VsCode.OpenTerminalLinkAsync(scene, pattern)` | Opens a link printed in the terminal (Cmd or Ctrl+click), such as the dashboard's; record it with `scene.SwitchToAsync`. |
| `VsCode.RunCommandAsync(page, command)` | Runs a command of the command palette, such as "View: Toggle Maximized Panel". |
| `Dashboard.NameCell(scene, resource)` / `Dashboard.StateCell` / `Dashboard.Row` | A resource on the Resources page; highlight a cell, not the row. |
| `Dashboard.DismissUnsecuredBannerAsync(scene)` | Hides the banner of a dashboard with anonymous access. |
| `Dashboard.WaitForHealthyAsync(app, resources)` / `Dashboard.AspireAsync(app, "stop")` | Runs `aspire wait` or another command from the app's folder, such as for an app started in a clip. |

## Pitfalls

- **Narrate first.** The recorder reads `durations.json` to hold each step; recording before narrating gives clips
  whose steps are too short. If the narration of a step grows afterwards, record its clip again, unless it still
  ends before the next step starts.
- **Navigation stops the screencast**: a full page load needs `ClickAndWaitForNavigationAsync`,
  `WaitForNavigationAsync` or `NavigateAsync`, or the rest of the clip is frozen. The dashboard is a Blazor app: its
  menu changes pages without a full load, so a plain `ClickAsync` works there.
- **Live data moves**: logs, traces and metrics keep arriving while recording. Pause the stream, filter, or select
  a resource before `StartCaptureAsync` when the list must stay still while the narration describes it.
- **Expand menus and panels before `StartCaptureAsync`** when opening them isn't part of the story.
- **Seed data outside the capture**, with `fetch` calls or a script, rather than by clicking through forms on camera.
- **Highlights are fixed-position**: clear them before scrolling.
- **Zooms**: they're rendered by the assembly, so the contact sheets of `video.cs review` show the page wide. Check them in
  the assembled video. Zoom on what the narration names, about once a step: a zoom on every action makes the video
  restless. A step starts wide: zoom again in each step that needs it. Don't zoom on something larger than about
  two thirds of the page: it gets a smaller zoom, or none. In a grid, highlight a cell (such as the resource's
  name), not the row: a row spans the page.
- **The dashboard's markup** (checked with Aspire 13.6):
  - The rows of its grids have `display: contents`: they have no box of their own. `MoveToAsync`, `ClickAsync` and
    `HighlightAsync` use the union of their cells (`BoxOfAsync`). To point at one cell, use
    `row.GetByRole(AriaRole.Gridcell).Nth(n)`; on the Resources page the columns are Name, State, Start time,
    Source, URLs, Actions.
  - The menu links are named "Resources", "Console", "Structured logs", "Traces" and "Metrics": use
    `GetByRole(AriaRole.Link, new() { Name = "Structured logs", Exact = true })`, and say the same names in the
    narration.
  - The Resources page has `Resources`, `Parameters` and `Graph` tabs: `GetByRole(AriaRole.Tab, new() { NameRegex = new Regex("^Graph") })`.
  - The "Resource" pickers are Fluent UI selects: click `GetByRole(AriaRole.Combobox).First`, then
    `Locator("fluent-option").Filter(new() { HasTextRegex = new Regex("^\\s*webfrontend\\s*$") })`; the options
    have no `option` role.
  - The filter boxes are `fluent-text-input` elements. `GetByPlaceholder("Filter")` matches both the element and the
    input in its shadow root, so it fails: use `Locator("fluent-text-input").First`.
  - Metrics opens without a resource: select one with the picker, or open `/metrics/resource/<name>`.
  - Clicking a row of Traces opens the trace's details; clicking a row of Structured logs opens the entry's details,
    with its `TraceId`.
- **Hide secrets**: the login token, connection strings and environment variables shown in resource details. Mask
  them in the dashboard, or don't open them.
- **Problems with external tools** (VS Code, the Aspire CLI, packages, the voice): see `references/troubleshooting.md`.
- **Look at every contact sheet** (`video.cs review`): an error, an unhealthy resource or an empty list is easy to miss in
  the logs.
- **Restart the app** after changing its code, and run `recorder.cs setup` again with the new login URL.
