# Aspire VS Code Extension Changelog

## v1.17.0

### Features

- Add Hot Reload discoverability for .NET resources while debugging: the extension now surfaces C# Dev Kit's Hot Reload controls for Aspire-managed .NET projects instead of hiding them ([#19067](https://github.com/microsoft/aspire/pull/19067)).
- Show runtime-unhealthy resources as warnings in the Aspire pane instead of silently reporting them as running ([#18973](https://github.com/microsoft/aspire/pull/18973)).
- Add a "Copy AppHost path" action when clicking the Path tree item in the AppHosts view ([#18578](https://github.com/microsoft/aspire/issues/18578), [#18621](https://github.com/microsoft/aspire/pull/18621)).
- Add non-watch debug/F5 parity for plain executables carrying project metadata (e.g. `DotnetProjectResource`), so they debug and render like a project resource ([#18729](https://github.com/microsoft/aspire/pull/18729)).
- Execute resource start/stop/restart and custom commands from the Aspire pane without opening an integrated terminal ([#18457](https://github.com/microsoft/aspire/pull/18457)).
- Use incremental, streaming AppHost discovery instead of a full rescan on every workspace change, reducing discovery latency ([#18443](https://github.com/microsoft/aspire/pull/18443)).

### Fixes

- Fix AppHost commands (start/stop/restart) not terminating the underlying DCP session, which could leave orphaned processes behind ([#19125](https://github.com/microsoft/aspire/pull/19125)).
- Honor `ASPIRE_HOME` when resolving deployment state instead of always using the default location ([#19244](https://github.com/microsoft/aspire/pull/19244)).
- Keep AppHost targets configured via `launch.json` out of the workspace's default AppHost list ([#19126](https://github.com/microsoft/aspire/pull/19126)).
- Fix an intermittent failure generating self-signed development certificates caused by DER-invalid serial numbers ([#19176](https://github.com/microsoft/aspire/pull/19176)).
- Respect project-level server-ready action overrides instead of always using the extension default ([#19200](https://github.com/microsoft/aspire/pull/19200)).
- Fix Azure Functions projects failing to launch over HTTPS from VS Code ([#19001](https://github.com/microsoft/aspire/pull/19001)).
- Fix incorrect build ownership for file-based AppHosts, which could cause builds to be skipped or duplicated ([#18984](https://github.com/microsoft/aspire/pull/18984)).
- Use "run" wording instead of "debug" wording when launching an AppHost without debugging ([#18987](https://github.com/microsoft/aspire/pull/18987)).
- Fix Aspire CLI discovery failing to find a global .NET tool install on Windows ([#18940](https://github.com/microsoft/aspire/pull/18940)).
- Fix stale global AppHost entries lingering in the Aspire pane after a debug session stops ([#18594](https://github.com/microsoft/aspire/pull/18594)).
- Stop the AppHost's own debug session before stopping its parent Aspire debug session, avoiding duplicate/out-of-order stop attempts ([#18561](https://github.com/microsoft/aspire/pull/18561)).
- Improve reliability of the AppHost status stream: fix a broken exponential backoff after restarts and make the CLI compatibility banner accurate across multiple open AppHosts ([#18527](https://github.com/microsoft/aspire/pull/18527)).
- Fix the extension ignoring a non-zero debuggee exit code, which could mask a crashed AppHost as a clean exit ([#18712](https://github.com/microsoft/aspire/pull/18712)).
- Improve extension CLI probe startup reliability: avoid discovery races and stray CLI output during startup ([#18517](https://github.com/microsoft/aspire/pull/18517)).
- Forward `aspire.aspireCliExecutablePath` as `AspireCliPath` so MSBuild bundle resolution picks up a configured dev CLI path ([#18073](https://github.com/microsoft/aspire/issues/18073), [#18362](https://github.com/microsoft/aspire/pull/18362)).
- Update `js-yaml` to 4.3.1 to resolve [GHSA-5p4m-2wfm-xmqj](https://github.com/advisories/GHSA-5p4m-2wfm-xmqj) ([#19231](https://github.com/microsoft/aspire/pull/19231)).

## v1.16.0

### Features

- Flatten single-AppHost group nodes in the AppHosts tree view so a lone running or idle AppHost is surfaced directly at the top level instead of under a redundant `(1)` wrapper ([#18420](https://github.com/microsoft/aspire/issues/18420), [#18523](https://github.com/microsoft/aspire/pull/18523)).
- Update the Marketplace page with focused AppHost-view, debug-session, and dashboard screenshots, and add AppHost telemetry signals for discovery, launch, and running-state metrics; all events respect `telemetry.telemetryLevel` ([#17898](https://github.com/microsoft/aspire/pull/17898)).

### Fixes

- Fix the Get Started walkthrough's Install Aspire CLI step to use a package-manager picker (WinGet, Homebrew, npm, .NET tool, mise) instead of shell-specific piped scripts, resolving failures on Windows when the default shell is `cmd.exe` ([#18459](https://github.com/microsoft/aspire/issues/18459), [#18522](https://github.com/microsoft/aspire/pull/18522)).
- Fix stale global AppHosts appearing in the Aspire pane when switching back to a workspace view; global AppHosts are now cleared and re-filtered immediately on view switch ([#18506](https://github.com/microsoft/aspire/issues/18506), [#18516](https://github.com/microsoft/aspire/pull/18516)).

## v1.15.0

### Features

- Add MAUI platform debugging support (iOS simulator/device, Mac Catalyst, Android emulator/device, and Windows) for MAUI resources running under Aspire when the VS Code MAUI extension is installed ([#17853](https://github.com/microsoft/aspire/issues/17853), [#17857](https://github.com/microsoft/aspire/pull/17857)).
- Expose AppHost query and resource management APIs from the Aspire extension for programmatic integration by tools such as C# Dev Kit v2 ([#17705](https://github.com/microsoft/aspire/pull/17705)).

### Fixes

- Fix stale AppHost running state in the Aspire pane after a debug session ends ([#17946](https://github.com/microsoft/aspire/issues/17946), [#17965](https://github.com/microsoft/aspire/pull/17965)).
- Stop the Aspire panel from showing a false CLI upgrade prompt for non-compatibility errors such as a missing container runtime ([#18337](https://github.com/microsoft/aspire/issues/18337), [#18358](https://github.com/microsoft/aspire/pull/18358)).
- Extend the AppHost debug startup timeout for extension-managed debug sessions so breakpoints hit before `builder.Build()` no longer cause the CLI to terminate the session ([#18021](https://github.com/microsoft/aspire/issues/18021), [#18353](https://github.com/microsoft/aspire/pull/18353)).

## v1.14.0

### Features

- Stop opening the Aspire Dashboard automatically by default. Use the Aspire: Dashboard Browser setting or a launch.json `dashboardBrowser` value to opt into notifications, an external browser, the integrated browser, or browser debugging ([#17923](https://github.com/microsoft/aspire/issues/17923)).
- Add Bun debugging support for Bun services running under Aspire ([#17848](https://github.com/microsoft/aspire/pull/17848)).
- Improve parameter display in the resource tree and AppHost CodeLens: secrets are masked, long values are truncated, and missing parameter values are shown explicitly ([#17193](https://github.com/microsoft/aspire/issues/17193), [#17881](https://github.com/microsoft/aspire/pull/17881)).

### Fixes

- Fix excessive AppHost discovery requests that could flood the workspace with redundant file-system scans ([#17897](https://github.com/microsoft/aspire/pull/17897)).
- Show a compatibility error in the Aspire pane when the running AppHost returns empty `describe` output ([#17925](https://github.com/microsoft/aspire/pull/17925)).
- Harden terminal commands against shell injection by routing Aspire CLI arguments through structured shell quoting ([#17930](https://github.com/microsoft/aspire/pull/17930)).
- Update npm dependencies to resolve open security advisories: `undici` ([#17868](https://github.com/microsoft/aspire/pull/17868)) and `ws`, `fast-uri`, `qs`, `@nevware21/ts-utils` ([#17951](https://github.com/microsoft/aspire/pull/17951)).

## v1.13.0

### Features

- Add Aspire pane support for resource commands, including command visibility, enabled/disabled state, argument prompts, and terminal execution from resource tree items ([#17698](https://github.com/microsoft/aspire/pull/17698)).

## v1.12.0

### Features

- Add VS Code telemetry signals for engagement, AppHost launches, command invocations, debug sessions, and dashboard telemetry passthrough; all events respect the VS Code `telemetry.telemetryLevel` setting ([#17721](https://github.com/microsoft/aspire/issues/17721), [#17723](https://github.com/microsoft/aspire/pull/17723)).

## v1.11.0

### Features

- Show discovered AppHosts in the Aspire pane so you can launch them without a workspace `launch.json` ([#17506](https://github.com/microsoft/aspire/pull/17506)).
- Add support for `launchUrl` in `launchSettings.json` so browser auto-launch targets the configured URL ([#17634](https://github.com/microsoft/aspire/pull/17634)).
- Add VS Code Go debugging support for Go services running under Aspire ([#17406](https://github.com/microsoft/aspire/pull/17406)).

### Fixes

- Fix AppHost launch path resolution so the extension correctly locates the AppHost project on disk ([#17408](https://github.com/microsoft/aspire/pull/17408)).

### Changes

- Resource data has been removed from `aspire ps`; the extension now streams resource state via `aspire describe` for more accurate and real-time updates ([#17479](https://github.com/microsoft/aspire/pull/17479)).
