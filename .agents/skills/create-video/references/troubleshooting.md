# Troubleshooting external tools

Problems met with the tools a video depends on, with their symptom, cause and fix. Check here before debugging, and
add what you find. The recorder's helpers apply these fixes: `scripts/recorder/VsCode.cs` (with
`vscode-settings.jsonc`) and `scripts/recorder/Dashboard.cs`.

## VS Code in the browser (`code serve-web`)

**For any video that shows VS Code, use the recorder's `VsCode` helpers** (`VsCode.OpenAsync`, the typing and terminal
helpers), which write `scripts/recorder/vscode-settings.jsonc` into VS Code before each scene.

| Symptom | Cause | Fix |
|---|---|---|
| IntelliSense shows only words of the file (the "abc" icon), or "No suggestions." after `builder.`; typing ends as `addDocker ("compose")` | The TypeScript extension isn't running with the project. (1) It doesn't run in Restricted Mode (`untrustedWorkspaces: false`). (2) In the browser it runs in a web worker, in a partial mode that doesn't resolve imports, such as `.aspire/modules/aspire.mjs` | Write the user settings into the browser before the workbench loads: `"security.workspace.trust.enabled": false` and `"remote.extensionKind": { "vscode.typescript-language-features": ["workspace"] }` (see below) |
| Settings in `<server-data-dir>/data/User/settings.json` have no effect | In the browser, user settings live in IndexedDB, not on the server | Put them in IndexedDB: database `vscode-web-db` (version 3; stores `vscode-userdata-store`, `vscode-logs-store`, `vscode-filehandles-store`), store `vscode-userdata-store`, key `/User/settings.json`, value the UTF-8 bytes. Do it from any page of the VS Code origin (`/manifest.json` works, even as a 404), before opening `/?folder=...`. Each scene is a new browser context: do it in every scene |
| Terminal profile, `env` and trust ignored from `.vscode/settings.json` or `data/Machine/settings.json` | Restricted settings (terminal profiles and env) aren't read in Restricted Mode; trust and `remote.extensionKind` are application settings | Put every setting in the user settings above, written to IndexedDB: trust off, the look of the editor, and the terminal profile |
| How to check where TypeScript runs | | `ls <server-data-dir>/data/logs/<latest>/exthost*/`: a `vscode.typescript-language-features` folder means it runs on the server. Without it, it runs in the browser |
| The first suggestions are slow, or missing | The TypeScript server loads the generated `aspire.mts` (tens of thousands of lines) | Open the file a few seconds before the capture; `VsCode.TypeWithSuggestionAsync` presses Ctrl+Space when the list doesn't come |
| After an identifier (`await builder`), the dot doesn't open the member list | The list of words for the identifier is still open | Press Escape, pause, then type `.addDocker` |
| Before checking the generated API, make sure the method exists | | `aspire restore`, then `grep -n "addDockerComposeEnvironment(" .aspire/modules/aspire.mts` (it should be on `DistributedApplicationBuilder`) |
| A typing take is wrong, but the clip records anyway | | Check the file on disk after typing (`VsCode.ExpectFileAsync`), with `files.autoSave` on; Cmd+S isn't reliable |
| The "Trust" button of the trust editor can't be clicked; the trust editor stays open as an overlay | It isn't a `button` role; it opens as a modal editor | Avoid it: turn trust off in the user settings. Otherwise Cmd+Enter trusts, Escape closes it |
| Text typed into a modal editor (the user settings JSON) instead of running F1 or Cmd+S | Modal editors take those keys | Don't edit settings through the UI: use IndexedDB |
| Adding COOP/COEP headers through `Context.RouteAsync` (to isolate the page for the web TypeScript server) leaves the workbench unloaded | | Don't. `?vscode-coi=` doesn't add the headers in serve-web either. Run TypeScript on the server instead |
| A Welcome tab, extension recommendations, the chat panel | Defaults of VS Code and of the Aspire templates | `"workbench.startupEditor": "none"`, `"chat.disableAIFeatures": true`, `"extensions.ignoreRecommendations": true`, delete the template's `.vscode/extensions.json`, run "View: Close All Editors" and "Notifications: Clear All Notifications" before the capture |
| The terminal's text can't be found or highlighted | The terminal draws on a canvas | `"terminal.integrated.gpuAcceleration": "off"`: rows are in `.xterm-rows > div`. A URL is a separate `span` from its label, so match and highlight the row div |
| A long prompt, or the user's shell setup | | A terminal profile: `{ "path": "/bin/zsh", "args": ["-f"], "env": { "PS1": "%F{141}contoso%f %F{245}$%f " } }` |
| The app started in the terminal stops between clips | The terminal belongs to the page; `"terminal.integrated.enablePersistentSessions": false` | Keep `aspire run` and the dashboard in one clip: Cmd+click the link, `Context.WaitForPageAsync()`, `scene.SwitchToAsync(page)`. Stop the app with Ctrl+C in the first page after the last step |

## Aspire CLI and AppHost

| Symptom | Cause | Fix |
|---|---|---|
| `aspire add postgres --non-interactive`: "No exact match" | Non-interactive mode needs the exact package or friendly name | `aspire add Aspire.Hosting.PostgreSQL` |
| The login token of the dashboard shows on camera | `aspire run` prints the login URL | `ASPIRE_DASHBOARD_UNSECURED_ALLOW_ANONYMOUS=true` in the terminal profile's env: it prints `https://localhost:17010`. Dismiss the dashboard's "Endpoint is unsecured" banner after it opens |
| `aspire describe --format Json` isn't parseable | | Parse the table: count `Running │ Healthy` |
| `aspire deploy` to Docker Compose: a .NET service exits with `database "db" does not exist` (3D000) | Aspire creates databases only during `aspire run` | Let the service create its database (connect to `postgres`, `CREATE DATABASE`) |
| `aspire deploy` prints "No public endpoints" for the frontend | | `.withExternalHttpEndpoints()` on it; the port changes on every deploy |
| A Vite frontend has to call two services once deployed | `publishAsStaticWebsite` takes one `apiPath` and `apiTarget` | Give the frontend one backend (the .NET API calls the catalog) |
| `docker compose -f aspire-output/docker-compose.yaml down` fails ("neither an image nor a build context") | The file relies on the deploy's variables | `docker compose ls`, then `docker compose -p <name> down` |
| The first `aspire deploy` is slow (about 50 s) | Image builds | Deploy once before recording: later ones take about 20 s |

## .NET packages

| Symptom | Cause | Fix |
|---|---|---|
| `NU1605` package downgrade of `OpenTelemetry.Extensions.Hosting` | `Aspire.Npgsql` 13.6 needs OpenTelemetry 1.17 | Reference the OpenTelemetry packages at 1.17.0 |

## The Aspire dashboard (also see `recording.md`)

| Symptom | Cause | Fix |
|---|---|---|
| `GetByRole(AriaRole.Button, ...)` doesn't find the "Actions" button or the dialog's "Yes" | They are `fluent-button` elements | `row.Locator("fluent-button[aria-label='Actions']")`; `Locator("fluent-dialog fluent-button").Filter(new() { HasTextRegex = new Regex("^\\s*Yes\\s*$") })`. Menu items do have the `menuitem` role |
| Clicking "Resources" in the menu shows the Graph again | The page remembers its last tab | Then click the `Resources` tab |
| A highlight lands on a header instead of a log row | Row filters match the grid's outer rows | Highlight the `Gridcell` that has the text |
| No health column | Health shows in State: a green check for healthy, "Running (Unhealthy)" otherwise | |

## The voice (`msedge-tts`)

| Symptom | Cause | Fix |
|---|---|---|
| Custom SSML (`rawToStream`, such as a `<lang>` element) returns no audio | The free service rejects it | Only plain text with the prosody of `tts.mjs` |
| Every request fails with a 403 | The service's handshake changed | Update `msedge-tts` (see SKILL.md) |
| Voice samples | | Send them to the user as files; never play them on the user's speakers |
| A requested voice (`en-US-PhoebeMultilingualNeural`) fails with "Stream closed before the synthesis completed" | Azure-only voice: the free Edge service doesn't offer it | List the available voices with `new MsEdgeTTS().getVoices()` and offer the closest one (`en-US-AvaMultilingualNeural`, `en-US-EmmaMultilingualNeural`) |

## The recorder and the shell

| Symptom | Cause | Fix |
|---|---|---|
| `recorder.cs probe` fails: "storage state file does not exist" | | Fixed: it runs without `state.json` (anonymous dashboards, VS Code) |
| `timeout: command not found` | macOS has no `timeout` | Use the tool's own timeout |

## Recording native desktop apps (macOS menu bar, a remote Windows desktop)

The browser recorder can't record native UI. For a tray or menu bar app, record the screen with
`screencapture -v -C -R<x,y,w,h>` while a small Swift program posts CGEvents (smooth moves, clicks, keys)
and logs when each step starts, then extract frames and write `timeline.json` with the same shape as the
recorder's (`steps`, `frames`, `end`). A remote Windows desktop shown full screen by Windows App records and
takes input the same way.

| Symptom | Cause | Fix |
|---|---|---|
| Captures show only the wallpaper and a black menu bar | The process that runs `screencapture` (the agent's or the terminal's binary, which can be a separate TCC client from its app) has no Screen Recording permission | Have the user add that binary under Screen Recording, then fully quit and reopen the app |
| Posted mouse events do nothing; `AXIsProcessTrusted()` is false | Same binary, Accessibility not granted | Calling an AX API once makes macOS prompt for it; the user enables it under Accessibility (no restart needed) |
| `screencapture -v` produces a 0.08 s video | Without a terminal it stops at end of file on stdin | Give it a pipe as stdin and write a character to stop it |
| A new recording silently keeps the old video ("Failed to save to final location") | `screencapture` doesn't overwrite | Delete the file before each recording |
| A click on a menu bar item does nothing (the item only highlights) | A synthetic Cmd key press left Command in the HID event state, and Command-click on a menu bar item starts rearranging it | Create events from `CGEventSource(stateID: .hidSystemState)` and set `event.flags = []` on mouse events; pause about 0.5 s on the item before clicking |
| The first left click on a status item after its native menu closed (Escape) does nothing | AppKit swallows that synthetic mouse-down; the action is never called | Click again until the app's window is on screen (the `open` op of the tray demo driver retries) |
| A background-only (`LSUIElement`) app can't be driven by desktop automation tools | They only list apps with a Dock icon | Drive it with CGEvents as above |
| Privacy | The real menu bar, desktop icons and window titles appear | Blur them in post-processing (crop, `boxblur`, overlay), and hide other apps with `NSRunningApplication.hide()` |
