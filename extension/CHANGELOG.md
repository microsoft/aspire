# Aspire VS Code Extension Changelog

## v1.14.0

### Features

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
