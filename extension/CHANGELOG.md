# Aspire VS Code Extension Changelog

## v1.11.0

### Features

- Show discovered AppHosts in the Aspire pane so you can launch them without a workspace `launch.json` ([#17506](https://github.com/microsoft/aspire/pull/17506)).
- Add support for `launchUrl` in `launchSettings.json` so browser auto-launch targets the configured URL ([#17634](https://github.com/microsoft/aspire/pull/17634)).
- Add VS Code Go debugging support for Go services running under Aspire ([#17406](https://github.com/microsoft/aspire/pull/17406)).

### Fixes

- Fix AppHost launch path resolution so the extension correctly locates the AppHost project on disk ([#17408](https://github.com/microsoft/aspire/pull/17408)).

### Changes

- Resource data has been removed from `aspire ps`; the extension now streams resource state via `aspire describe` for more accurate and real-time updates ([#17479](https://github.com/microsoft/aspire/pull/17479)).
