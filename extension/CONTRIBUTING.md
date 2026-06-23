# Contributing to the Aspire VS Code extension

How to set up your machine, the code layout, and the fastest inner-loop for changes.

Bug fixes, new commands, debugger-language support, walkthrough content, settings, and docs are all welcome. To find a starting point, browse [`area-vscode-extension` issues labeled `good first issue` or `help wanted`](https://github.com/microsoft/aspire/issues?q=is%3Aissue+is%3Aopen+label%3Aarea-vscode-extension+label%3A%22good+first+issue%22%2C%22help+wanted%22).

## Install prerequisites

- Node.js (LTS version) — `npm` must be on the PATH (it ships with Node.js). The
  build scripts (`build.sh` / `build.ps1`) install a pinned [Corepack](https://github.com/nodejs/corepack)
  via `npm install -g corepack@<version>` from the configured registry and seed
  Corepack's cache with the Yarn release pinned by the `packageManager` field in
  `extension/package.json`. You do **not** need to install Yarn yourself.
- Visual Studio Code (latest) or Visual Studio Code Insiders
- [Aspire CLI](https://aspire.dev/get-started/install-cli/) must be installed and available in the PATH

> No repository write access or credentials are needed to build. Dependencies come from the public `dotnet-public-npm` Azure Artifacts feed; every version pinned in `yarn.lock` is already cached and served anonymously, so `yarn install` works for everyone. See the [npm mirror note](#updating-the-yarn-version) for the one edge case maintainers hit when bumping pinned tool versions.

## Quick start: extension-only changes

For TypeScript/UI changes that don't require debugging the Aspire CLI itself, skip the full repository build (and its .NET prerequisites) and use any Aspire CLI on your PATH (install one with the **Aspire: Install Aspire CLI (stable)** command).

From `extension/`:

```bash
corepack yarn install   # restore dependencies
```

Open `extension/` in VS Code and launch **Run Extension** (`F5`) to start an Extension Development Host with your build. The launch config runs the `tasks: watch extension` preLaunchTask (which executes `yarn watch`) to keep `dist/` up to date while you edit. After rebuilds, re-launch or run **Developer: Reload Window** in the host to pick up changes.

## Project structure

Source lives under `extension/src/`:

| Directory | Contents |
|-----------|----------|
| `commands/` | Command Palette commands (`Aspire: …`) and handlers |
| `views/` | Aspire sidebar tree views and resource UI |
| `debugger/` | Debug session orchestration; `debugger/languages/` adds per-language support (C#, Python, Node.js, …) |
| `dcp/` | Integration with the orchestrator (Developer Control Plane) |
| `server/` | RPC server the Aspire CLI talks to |
| `services/` | Long-lived services (CLI discovery, telemetry, settings, …) |
| `mcp/` | Model Context Protocol server registration |
| `editor/` | Editor features; `editor/parsers/` parses apphost files for CodeLens and validation |
| `loc/` | Localized string definitions (`strings.ts`) |
| `utils/` | Shared helpers |
| `test/` | `*.test.ts` unit tests run by `@vscode/test-electron` |

Also: `package.json` declares commands, settings, and contribution points; `walkthrough/` holds the Get Started Markdown; `package.nls.json` (+ `package.nls.*.json`) hold localized `package.json` strings.

## Building the full repository (extension + CLI)

Run `build.ps1` (Windows) or `build.sh` (Mac/Linux) from the repository root to compile the CLI, install extension dependencies, and localize. Use this when debugging the extension and CLI together. See [docs/contributing.md](/docs/contributing.md) and [docs/machine-requirements.md](/docs/machine-requirements.md) for the .NET prerequisites.

## Run extension locally

- Open the extension folder in Visual Studio Code.
- Launch either the `Run Extension` or `Run Extension (cli stop on entry)` launch configuration. The latter will set an environment variable that causes the CLI to wait until a debugger is attached to execute its logic.

### Optional: set the CLI path

If you want to effectively debug the Aspire CLI together with the Aspire VS Code extension, you must set the `Aspire Cli Executable Path` setting to the Aspire CLI output path. The output path, relative to the Aspire repository root directory, is `artifacts/bin/Aspire.Cli/Debug/net10.0/aspire`.

You may also want to use the `Run Extension (cli stop on entry)` launch configuration, as `Run Extension` does not prevent the Aspire CLI from executing immediately.

You can use the `Aspire: Extension settings` command to open VS Code settings directly to the Aspire extension category.

## Running tests

Unit tests are `*.test.ts` files under `src/test/`, run via `@vscode/test-electron`. From `extension/`:

```bash
corepack yarn test
```

This compiles tests and sources, lints, then runs the suite (`corepack yarn lint` lints only). Add or update tests for behavior changes, and ensure tests and lint pass before opening a PR.

To run a single unit-test file or a filtered subset, compile the tests first and pass Mocha selectors through `unit-test`:

```bash
corepack yarn compile-tests
corepack yarn compile
corepack yarn unit-test --run out/test/configInfoProvider.test.js
corepack yarn unit-test --grep "parseConfigInfoOutput"
corepack yarn unit-test --run out/test/configInfoProvider.test.js --run out/test/extensionApi.test.js
```

### End-to-end tests

UI end-to-end tests live under `src/test-e2e`. They run a packaged VSIX in a real VS Code instance through ExTester, using a real Aspire CLI and a generated AppHost workspace.

Run the full local E2E suite from `extension/`:

```bash
ASPIRE_EXTENSION_E2E_CLI_PATH=/path/to/aspire corepack yarn test:e2e
```

On Linux, run the E2E command under `xvfb-run -a` when no desktop session is available. On Windows, `ASPIRE_EXTENSION_E2E_CLI_PATH` can point at an `.exe` or `.cmd` wrapper, including paths with spaces. Set `ASPIRE_EXTENSION_E2E_VSIX=/path/to/aspire-extension.vsix` to test an existing package instead of letting the runner create one. The runner defaults to VS Code 1.122.1 and the ExTester version pinned in `package.json` and `yarn.lock`; override with `ASPIRE_EXTENSION_E2E_VSCODE_VERSION` when you need to investigate VS Code-specific behavior. To investigate another ExTester version, update the pinned `vscode-extension-tester` package and regenerate `yarn.lock` from `dotnet-public-npm`. The VS Code user data is forced to English (`locale.json` plus `VSCODE_NLS_CONFIG`) so UI text assertions are deterministic across machines.

Some extension E2E tests intentionally cover bugs fixed by the current repo-built CLI. When running the extension suite against an older published CLI to check backward compatibility, set `ASPIRE_EXTENSION_E2E_SKIP_CURRENT_CLI_REGRESSIONS=true` so those current-CLI-only regressions are skipped instead of failing on the older CLI bug.

The suite can be sharded by running separate VS Code windows/processes, which is how CI keeps the long UI paths parallel instead of relying on Mocha-level parallelism inside one extension host:

```bash
ASPIRE_EXTENSION_E2E_SHARD=command-palette ASPIRE_EXTENSION_E2E_SPEC=out/test-e2e/test-e2e/commandPalette.e2e.test.js ASPIRE_EXTENSION_E2E_CLI_PATH=/path/to/aspire corepack yarn test:e2e
ASPIRE_EXTENSION_E2E_SHARD=settings-files ASPIRE_EXTENSION_E2E_SPEC=out/test-e2e/test-e2e/settingsFiles.e2e.test.js ASPIRE_EXTENSION_E2E_CLI_PATH=/path/to/aspire corepack yarn test:e2e
ASPIRE_EXTENSION_E2E_SHARD=discovery-configuration ASPIRE_EXTENSION_E2E_SPEC=out/test-e2e/test-e2e/discoveryConfiguration.e2e.test.js ASPIRE_EXTENSION_E2E_CLI_PATH=/path/to/aspire corepack yarn test:e2e
ASPIRE_EXTENSION_E2E_SHARD=apphost-tree ASPIRE_EXTENSION_E2E_SPEC=out/test-e2e/test-e2e/appHostTree.e2e.test.js ASPIRE_EXTENSION_E2E_CLI_PATH=/path/to/aspire corepack yarn test:e2e
ASPIRE_EXTENSION_E2E_SHARD=tree-actions ASPIRE_EXTENSION_E2E_SPEC=out/test-e2e/test-e2e/treeActions.e2e.test.js ASPIRE_EXTENSION_E2E_CLI_PATH=/path/to/aspire corepack yarn test:e2e
ASPIRE_EXTENSION_E2E_SHARD=debug-dashboard ASPIRE_EXTENSION_E2E_SPEC=out/test-e2e/test-e2e/debugDashboard.e2e.test.js ASPIRE_EXTENSION_E2E_CLI_PATH=/path/to/aspire corepack yarn test:e2e
ASPIRE_EXTENSION_E2E_SHARD=zero-to-running ASPIRE_EXTENSION_E2E_SPEC=out/test-e2e/test-e2e/zeroToRunning.e2e.test.js ASPIRE_EXTENSION_E2E_CLI_PATH=/path/to/aspire corepack yarn test:e2e
ASPIRE_EXTENSION_E2E_SHARD=package-surface ASPIRE_EXTENSION_E2E_SPEC=out/test-e2e/test-e2e/packageSurface.e2e.test.js ASPIRE_EXTENSION_E2E_CLI_PATH=/path/to/aspire corepack yarn test:e2e
ASPIRE_EXTENSION_E2E_SHARD=edge-cases ASPIRE_EXTENSION_E2E_SPEC=out/test-e2e/test-e2e/edgeCases.e2e.test.js ASPIRE_EXTENSION_E2E_CLI_PATH=/path/to/aspire corepack yarn test:e2e
```

`ASPIRE_EXTENSION_E2E_SPEC` accepts either one compiled spec path or a glob, so local runs can target one spec or a small subset without editing the test runner:

```bash
corepack yarn compile-e2e
ASPIRE_EXTENSION_E2E_SHARD=debug-dashboard-local ASPIRE_EXTENSION_E2E_SPEC=out/test-e2e/test-e2e/debugDashboard.e2e.test.js ASPIRE_EXTENSION_E2E_CLI_PATH=/path/to/aspire corepack yarn test:e2e
ASPIRE_EXTENSION_E2E_SHARD=fast-subset ASPIRE_EXTENSION_E2E_SPEC='out/test-e2e/test-e2e/{commandPalette,settingsFiles}.e2e.test.js' ASPIRE_EXTENSION_E2E_CLI_PATH=/path/to/aspire corepack yarn test:e2e
ASPIRE_EXTENSION_E2E_SHARD=tree-subset ASPIRE_EXTENSION_E2E_SPEC='out/test-e2e/test-e2e/*Tree.e2e.test.js' ASPIRE_EXTENSION_E2E_CLI_PATH=/path/to/aspire corepack yarn test:e2e
```

The current shards cover command palette and terminal routing, settings-file creation/opening with an isolated Aspire home, workspace AppHost discovery/configuration changes, AppHost run/stop/resource rendering, tree action commands for copy/open/log/resource operations, debug/dashboard lifecycle, a zero-to-running flow that routes the Aspire new/add terminal commands, creates a C# AppHost through the CLI, adds an integration package, registers a source breakpoint, and debugs the generated AppHost, the `package.json` contribution surface including exact activation events, command registration, menu/view/settings inventory, JSON validation, walkthrough command registration, CodeLens routing, and debug launch command routing, plus negative-path edge cases for invalid control payloads, missing tree targets, CLI-independent settings commands, and launch-state cleanup.

Diagnostics are left under `extension/.test-results`, `extension/.test-storage`, and `extension/.test-workspaces`, with shard-specific subdirectories when `ASPIRE_EXTENSION_E2E_SHARD` is set. The runner also sets `ASPIRE_HOME` to an isolated per-run directory and copies it into diagnostics before cleanup so settings-file failures do not touch or depend on the real user profile. These folders are ignored by git and are uploaded by CI when the E2E job runs.

Linux CI shards also record the Xvfb display with `ffmpeg`. The default workflow mode is `ASPIRE_EXTENSION_E2E_RECORDING_MODE=always`, which keeps and uploads `extension/.test-recordings/<shard>/*.mp4` for both successful and failing Linux shards. Set `ASPIRE_EXTENSION_E2E_RECORDING_MODE=failure` when you only want failed-run videos, or `off` to disable recording. The default capture size is `1280x1024`, matching the hosted Xvfb display; override with `ASPIRE_EXTENSION_E2E_RECORDING_SIZE` if you run under a larger display. Recording is intentionally Linux-only because the Windows E2E jobs do not run under Xvfb and hosted desktop capture is less reliable.

E2E tests should avoid fixed sleeps for readiness. Prefer the observation state written by the extension test bridge, ExTester wait APIs, unique generated workspaces, explicit per-phase timeouts, and cleanup through `aspire stop --apphost`. This is intentionally stricter than a normal smoke test because the suite runs a real VS Code, CLI, AppHost, terminal, and dashboard path. See https://github.com/microsoft/aspire/issues/17727 for the original tracking issue.

## Localizing user-facing strings

All user-facing text must be localized:

- Strings shown from extension code: add to **both** `src/loc/strings.ts` and `package.nls.json`.
- `package.json` contribution strings (command titles, setting descriptions, …): use a `%placeholder%` key defined in `package.nls.json`.

Edit only the base `package.nls.json` / `strings.ts`. The translated `package.nls.*.json` files are generated by a separate workflow — don't hand-edit them.

## Updating dependency overrides

The extension is built with **yarn**, pinned to the version recorded in `packageManager` of `package.json`. `package.json` uses `resolutions` for transitive dependency pins and `yarn.lock` is the authoritative lockfile.

When pinning a transitive dependency (e.g. to address a security advisory), add the pin to `resolutions` and regenerate `yarn.lock` in the same change:

```bash
corepack yarn install
```

The build rejects public registry URLs in `yarn.lock`; ensure regenerated entries resolve through the `dotnet-public-npm` feed (public, so no credentials are needed to consume it).

## Updating the Yarn version

Edit the `"packageManager": "yarn@x.y.z"` field in `extension/package.json`. The next `build.sh` / `build.ps1` run seeds Corepack's cache with that version before calling `corepack yarn …`. No further changes are required in `build.sh`, `build.ps1`, or `extension/Extension.proj`.

> **npm mirror note.** `.npmrc` and the build scripts' `NPM_REGISTRY` route npm downloads through the dnceng `dotnet-public-npm` Azure Artifacts feed. Anonymous reads of cached versions work without credentials, covering everything pinned in `yarn.lock`. Exception: the *first* request for a never-cached version triggers a pull-through fetch that fails with HTTP 401 (subsequent reads succeed). This only affects maintainers bumping the pinned Corepack or Yarn version; pre-seed with credentials via `npm install --global --registry https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-public-npm/npm/registry/ corepack@<version>` or `npm pack --registry https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-public-npm/npm/registry/ yarn@<version>`. Don't point `COREPACK_NPM_REGISTRY` at this feed for Yarn: Corepack requests the `/<package>/<version>` route, which Azure Artifacts returns 404 for even when the package exists.

## Troubleshooting

### `EACCES` from `npm install --global` on Linux/macOS

The build scripts run `npm install --global corepack@<version>` to pin the Corepack version. On systems where Node.js is installed from a package manager (apt, yum, the official `.pkg`), the npm global prefix is typically `/usr/lib/node_modules` or `/usr/local/lib/node_modules`, which is root-owned. The install will fail with `EACCES`. The cleanest fix is to use a Node version manager that puts the npm prefix in your home directory:

- [`nvm`](https://github.com/nvm-sh/nvm) — installs Node and configures the npm prefix automatically.
- [`fnm`](https://github.com/Schniz/fnm), [`asdf`](https://asdf-vm.com/), [`volta`](https://volta.sh/) — same idea, different tradeoffs.

Alternatively, point npm at a user-writable prefix without changing your Node install:

```bash
mkdir -p ~/.npm-global
npm config set prefix ~/.npm-global
export PATH="$HOME/.npm-global/bin:$PATH"   # add to ~/.bashrc or ~/.zshrc
```

### `corepack version mismatch` from `build.sh` / `build.ps1`

This means the `corepack` resolved from `PATH` is not the one we just installed via `npm install -g`. Most often the system Node install (`/usr/bin/corepack`, `%ProgramFiles%\nodejs\corepack.cmd`) is sitting in front of the npm global bin directory. On Windows, ensure `%APPDATA%\npm` comes before `%ProgramFiles%\nodejs` on `PATH`. On Linux/macOS, follow the `EACCES` remediation above and the npm prefix will be on `PATH` ahead of the system Node directory.
