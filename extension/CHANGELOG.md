# Aspire VS Code Extension Changelog

## v1.18.0

<!-- aspire-ext-changelog-done from=1c8df90b9860e508e841ee5bae43d9fdd0e6bd60 to=95ba0548da04adf7d7fe6866fff9e3df2d2ee549 base=1.17.0 -->

### Features

- Add a Java hosting integration, including Spring Boot and Quarkus app support, container publishing, and VS Code debugger wiring ([#18033](https://github.com/microsoft/aspire/pull/18033)).
- Add a Rust hosting package with VS Code debugger wiring ([#18906](https://github.com/microsoft/aspire/pull/18906)).

### Fixes

- Remember the selected AppHost folder correctly in multi-root workspaces ([#19342](https://github.com/microsoft/aspire/issues/19342), [#19359](https://github.com/microsoft/aspire/pull/19359)).
- Honor the AppHost launch profile selected in `launch.json` instead of falling back to the default profile's environment and URL ([#19387](https://github.com/microsoft/aspire/issues/19387), [#19400](https://github.com/microsoft/aspire/pull/19400)).
- Scope AppHost start/stop to the current git worktree and fix AppHost launch arguments being dropped or flattened when forwarded through VS Code ([#19357](https://github.com/microsoft/aspire/issues/19357), [#19384](https://github.com/microsoft/aspire/pull/19384)).
- Harden Aspire Skills bundle integrity checks by switching to SHA-512 and hide the remote-fetch preview toggle from user-facing settings ([#19303](https://github.com/microsoft/aspire/pull/19303)).

## v1.17.0

<!-- aspire-ext-changelog-done from=8278bca4a530f0fc513bdf4ed03b10683e36c16e to=d1c7add665f7e6582cdaa1b328c44172f0f96339 base= -->

### Features

- Emit a DCP session termination signal when stopping resources from the Aspire pane ([#19125](https://github.com/microsoft/aspire/pull/19125)).
- Use incremental AppHost discovery so workspace scans no longer re-enumerate every AppHost on each change ([#18443](https://github.com/microsoft/aspire/pull/18443)).
- Unify workspace and global AppHost `describe --follow` streaming ([#18527](https://github.com/microsoft/aspire/pull/18527)).
- Add non-watch debug/F5 parity for project resources ([#18729](https://github.com/microsoft/aspire/pull/18729)).
- Copy the AppHost path to the clipboard when clicking the Path tree item ([#18578](https://github.com/microsoft/aspire/issues/18578), [#18621](https://github.com/microsoft/aspire/pull/18621)).
- Show runtime-unhealthy resources as warnings in VS Code ([#18973](https://github.com/microsoft/aspire/pull/18973)).
- Execute VS Code resource commands without opening a terminal ([#18457](https://github.com/microsoft/aspire/pull/18457)).

### Fixes

- Remember the selected AppHost folder when debugging multi-root workspaces ([#19342](https://github.com/microsoft/aspire/issues/19342)).
- Make C# Dev Kit Hot Reload discoverable while debugging ([#19067](https://github.com/microsoft/aspire/pull/19067)).
- Keep launch-configuration AppHost targets out of the workspace default list ([#19126](https://github.com/microsoft/aspire/pull/19126)).
- Honor `ASPIRE_HOME` for deployment state ([#19244](https://github.com/microsoft/aspire/pull/19244)).
- Respect project server ready action overrides ([#19200](https://github.com/microsoft/aspire/pull/19200)).
- Fix Azure Functions HTTPS launches in VS Code ([#19001](https://github.com/microsoft/aspire/pull/19001)).
- Fix VS Code file AppHost build ownership ([#18984](https://github.com/microsoft/aspire/pull/18984)).
- Use "run" wording for no-debug AppHost launches ([#18987](https://github.com/microsoft/aspire/pull/18987)).
- Fix Windows global-tool Aspire CLI discovery ([#18940](https://github.com/microsoft/aspire/pull/18940)).
- Emit Aspire wire names in VS Code telemetry without losing existing telemetry safeguards ([#18562](https://github.com/microsoft/aspire/pull/18562)).
- Fix stale VS Code global AppHost state after a debug session stops ([#18594](https://github.com/microsoft/aspire/pull/18594)).
- Stop the AppHost debug session before the Aspire parent session ([#18561](https://github.com/microsoft/aspire/pull/18561)).
- Remove the unused Assistant chat/modal/sidebar UI and related code ([#18726](https://github.com/microsoft/aspire/pull/18726)).
- Fix the VS Code extension ignoring a non-zero debuggee exit code ([#18712](https://github.com/microsoft/aspire/pull/18712)).
- Improve extension CLI probe startup behavior ([#18517](https://github.com/microsoft/aspire/pull/18517)).
- Forward `aspireCliExecutablePath` as `AspireCliPath` for MSBuild bundle resolution ([#18073](https://github.com/microsoft/aspire/issues/18073), [#18362](https://github.com/microsoft/aspire/pull/18362)).
- Update npm dependencies to resolve open security advisories, including `js-yaml`, `fast-uri`, `nanoid`, `hono`, `vite`, `undici`, and `protobufjs` ([#19231](https://github.com/microsoft/aspire/pull/19231), [#19122](https://github.com/microsoft/aspire/pull/19122), [#18995](https://github.com/microsoft/aspire/pull/18995), [#18858](https://github.com/microsoft/aspire/pull/18858), [#18735](https://github.com/microsoft/aspire/pull/18735)).

## v1.16.0

### Features

- Add an Attach debugger action for running .NET project resources in the Aspire pane when the C# extension is installed ([#18602](https://github.com/microsoft/aspire/pull/18602)).
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
