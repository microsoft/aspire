---
name: vscode-extension
description: "Use when investigating, developing, debugging, testing, or reviewing Aspire VS Code extension behavior under extension/, including extension UI, command, debugger, RPC, DCP, MCP, and CLI-integration issues or features."
---

# Aspire VS Code Extension

This skill covers working on the Aspire VS Code extension, which lives entirely under
[extension/](../../../extension). It is a TypeScript extension built with **yarn** (pinned via
`packageManager` in `extension/package.json`) and **webpack**, and it communicates with the Aspire
CLI over RPC.

## Orientation

All commands below are run from the `extension/` directory unless noted.

| Path | What it is |
|------|------------|
| `extension/src/extension.ts` | Activation entry point |
| `extension/src/commands/` | Command implementations |
| `extension/src/views/` | Tree views, welcome views, panel UI |
| `extension/src/debugger/` | Debug adapter + per-language debuggers (dotnet, node, python, go) |
| `extension/src/server/`, `extension/src/mcp/`, `extension/src/dcp/` | RPC server, MCP provider, DCP integration |
| `extension/src/loc/strings.ts` | Localized strings (TypeScript-side) |
| `extension/package.nls.json` | Localized strings referenced from `package.json` (`%key%`) |
| `extension/src/test/` | Mocha unit tests (`*.test.ts`) run via `vscode-test` |
| `extension/package.json` | Manifest: `contributes`, `activationEvents`, scripts |

## Prerequisites

**Do NOT run the repo-root `./build.sh` or `./restore.sh` when you are only working on the Aspire
CLI and/or the VS Code extension.** The root scripts build the entire product (all of `src/`, native
AOT, packages) and are slow and unnecessary for this work. Instead, **build the extension and CLI
together with the extension build script.** From `extension/`, run `./build.sh` (Linux/macOS) or
`./build.ps1` (Windows). This script builds the Aspire CLI
(`dotnet build ../src/Aspire.Cli/Aspire.Cli.csproj`) **and** the extension, and installs extension
dependencies (seeds Corepack and runs `corepack yarn install`). You do not install Yarn yourself —
it is pinned via `packageManager`.

Use **yarn** for extension commands (`corepack yarn ...` or `yarn run ...`), not `npm` / `npx`.
The dependency graph and scripts are pinned for Yarn via `extension/package.json` and `yarn.lock`.

If `corepack yarn install` fails with registry 401/404 or `EACCES`, see the Troubleshooting section
of [extension/CONTRIBUTING.md](../../../extension/CONTRIBUTING.md) — these are environment/npm-mirror
issues, not code issues.

## Extension ↔ CLI compatibility (IMPORTANT)

The extension and the Aspire CLI are tightly coupled: **new features or changes in the extension
often require corresponding changes in the CLI.** When you add or modify extension behavior, expect
to also touch `src/Aspire.Cli/` and rebuild both with `extension/build.sh` / `extension/build.ps1`.

When you change a CLI contract or expectation (command output shape, JSON schema, new flags, new
behavior the extension relies on), **you must remain backwards compatible and keep working with older
Aspire CLI versions.** A user may have a newer extension paired with an older `aspire` CLI on their
PATH. Do not assume the CLI supports a capability just because the current build does.

Detect what the installed CLI supports at runtime instead of assuming, by reading capabilities from extension functionality that wraps `aspire config info`.

## Develop / modify the extension

- Prefer **TypeScript** (`.ts`) files; never create `.js` source files for extension code.
- Use **static imports**, not dynamic imports, unless dynamic import is absolutely necessary.
- Use the latest TypeScript features consistent with the existing code style.
- New user-facing commands, views, settings, or debugger config go in the `contributes` section of
  `extension/package.json`.

### Localization (REQUIRED for any user-facing string)

Every string shown to the user must be localized. There are two surfaces:

- **TypeScript code** → add to `extension/src/loc/strings.ts` using `vscode.l10n.t(...)`. Follow the
  existing pattern (plain `const` for static strings, an arrow function for strings with
  placeholders like `{0}`). Import from `loc/strings` rather than inlining literals.
- **`package.json` manifest** (command titles, view names, setting descriptions, debugger
  properties) → use a `%key%` reference and define the key in `extension/package.nls.json`.

Do not hand-edit generated localization bundles (the `l10n/` output, `*.xlf`, or
`*/localize/templatestrings.*.json`); translation is handled by a dedicated workflow. The npm
scripts `l10n:export` / `l10n:import` (run automatically by `precompile`/`prepackage`/`prewatch`)
regenerate the l10n bundle from `package.nls.json` and the `vscode.l10n.t` calls.

## Build

From `extension/`:

```bash
yarn run compile      # one-off webpack build to ./dist
yarn run watch        # incremental webpack build (use while iterating)
yarn run package      # production build (minified, hidden source maps)
```

`watch`, `compile`, and `package` are preceded by `prewatch`/`precompile`/`prepackage`, which run
`generate-version`, `generate-schema`, and the l10n export/import. The VS Code task
`yarn: watch extension` (the default build task) runs `yarn run watch`.

To produce a `.vsix`, the full MSBuild path is `extension/Extension.proj` (used by CI/signing); for
local iteration `yarn run package` plus the launch configs below is usually enough.

## Test

The extension has Mocha unit tests under `extension/src/test/` executed by `@vscode/test-cli`.

```bash
yarn run compile-tests   # tsc -> ./out (or watch-tests to keep rebuilding)
yarn run test            # alias for unit-test (vscode-test); runs lint+compile via pretest
yarn run lint            # eslint src
```

`pretest` runs `compile-tests`, `compile`, and `lint`, so `yarn run test` is the single command that
builds and runs everything. When iterating on one area, keep `watch-tests` running and re-run
`yarn run test`.

Add new tests as `extension/src/test/<area>.test.ts` mirroring nearby tests (e.g.
`appHostDiscovery.test.ts`, `strings.test.ts`). There is a `strings.test.ts` that guards
localization conventions — run it after touching `loc/strings.ts` or `package.nls.json`.

When adding, changing, or investigating user-visible UI behavior or interactions, strongly prefer
adding or updating VS Code extension E2E coverage under `extension/src/test-e2e/` so the behavior is
reproducible and protected from regressions. Skip this only with a strong, explicit justification.
If you run these tests against an older published CLI for compatibility validation, set
`ASPIRE_EXTENSION_E2E_SKIP_CURRENT_CLI_REGRESSIONS=true` to skip tests that intentionally cover bugs
fixed only by the current repo-built CLI.

## Debug / run locally

1. Open the `extension/` folder in VS Code.
2. Launch the **Run Extension** launch configuration to start an Extension Development Host, or
   **Run Extension (cli stop on entry)** to make the CLI wait for a debugger to attach (use this to
   debug the CLI and the extension together).
3. To debug against a locally built CLI, set the `Aspire Cli Executable Path` setting to the build
   output, e.g. `artifacts/bin/Aspire.Cli/Debug/net10.0/aspire` (relative to the repo root). The
   `Aspire: Extension settings` command opens settings filtered to the extension.

RPC and debugger issues usually live in `src/server/`, `src/debugger/`, or `src/dcp/`.

## Reproducing issues yourself (do this FIRST for root-cause / "can you repro" requests)

When asked to **find the root cause of** or **reproduce** an issue, do not stop at reading source
code. Strongly prefer a reproducible VS Code extension E2E test under `extension/src/test-e2e/` that
opens a workspace, drives the extension through its public UI/commands, and captures the failing
behavior in the E2E state file, VS Code logs, screenshots, or assertions.

Use Playwright or an Extension Development Host only as exploratory help when the right E2E shape is
not yet clear. If that exploration reproduces the issue, convert the scenario into an E2E test before
fixing the bug unless there is a strong, explicit reason not to. You can test a custom CLI build by
pointing the `Aspire Cli Executable Path` setting at your locally built `aspire`.

## Pull request evidence

When opening a PR for a user-visible VS Code extension change, include proof in the PR body. Prefer
before/after screenshots for changed UI, notifications, tree views, browser flows, or error states.
If you include a before screenshot, include an actual after screenshot of the changed state too;
focused tests, E2E state files, logs, or command output can support the after case, but they are not
a substitute for the after visual. Include a short video or recording when the behavior is
interactive, timing-sensitive, or difficult to understand from static images. If screenshots or video
are not appropriate, say why and include the closest useful evidence instead. Redact tokens, secrets,
local usernames, private paths, private URLs, and any other sensitive details before putting evidence
in a public PR body.

## Gotchas

- Do not modify `package.json` / `yarn.lock` / `package-lock.json` unless explicitly asked; when you
  must pin a transitive dependency, add it to `resolutions` and regenerate `yarn.lock` with
  `corepack yarn install` (see CONTRIBUTING).
- `yarn.lock` must resolve through the internal `dotnet-public-npm` feed; the build rejects public
  npmjs.org URLs.
- Generated files (`l10n/` bundle, schema, version) are produced by the pre-scripts — edit the
  sources (`package.nls.json`, `loc/strings.ts`, schema/version generator scripts), not the output.
