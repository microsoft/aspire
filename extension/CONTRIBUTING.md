# Contributing to the Aspire VS Code extension

How to set up your machine, the code layout, and the fastest inner-loop for changes.

Bug fixes, new commands, debugger-language support, walkthrough content, settings, and docs are all welcome. To find a starting point, browse [`area-extension` issues labeled `good first issue` or `help wanted`](https://github.com/microsoft/aspire/issues?q=is%3Aissue+is%3Aopen+label%3Aarea-extension+label%3A%22good+first+issue%22%2C%22help+wanted%22).

## Install prerequisites

- Node.js (LTS version) — `npm` must be on the PATH (it ships with Node.js). The
  build scripts (`build.sh` / `build.ps1`) install a pinned [Corepack](https://github.com/nodejs/corepack)
  via `npm install -g corepack@<version>` from the configured registry and seed
  Corepack's cache with the Yarn release pinned by the `packageManager` field in
  `extension/package.json`. You do **not** need to install Yarn yourself.
- Visual Studio Code (latest) or Visual Studio Code Insiders
- [Aspire CLI](https://aspire.dev/get-started/install-cli/) must be installed and available in the PATH

> No repository write access or credentials are needed to build. Dependencies come from the public `dotnet-public-npm` feed (a pull-through cache of npmjs.org); every version pinned in `yarn.lock` is already cached and served anonymously, so `yarn install` works for everyone. See the [npm mirror note](#updating-the-yarn-version) for the one edge case maintainers hit when bumping pinned tool versions.

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

The build rejects public npmjs.org URLs in `yarn.lock`; ensure regenerated entries resolve through the `dotnet-public-npm` feed (public, so no credentials are needed to consume it).

## Updating the Yarn version

Edit the `"packageManager": "yarn@x.y.z"` field in `extension/package.json`. The next `build.sh` / `build.ps1` run seeds Corepack's cache with that version before calling `corepack yarn …`. No further changes are required in `build.sh`, `build.ps1`, or `extension/Extension.proj`.

> **npm mirror note.** `.npmrc` and the build scripts' `NPM_REGISTRY` route npm downloads through the dnceng `dotnet-public-npm` Azure Artifacts feed, a **public** pull-through cache of npmjs.org. Anonymous reads of cached versions work without credentials, covering everything pinned in `yarn.lock`. Exception: the *first* request for a never-cached version triggers a pull-through fetch that fails with HTTP 401 (subsequent reads succeed). This only affects maintainers bumping the pinned Corepack or Yarn version; pre-seed with credentials via `npm install --global --registry https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-public-npm/npm/registry/ corepack@<version>` or `npm pack --registry https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-public-npm/npm/registry/ yarn@<version>`. Don't point `COREPACK_NPM_REGISTRY` at this feed for Yarn: Corepack requests the `/<package>/<version>` route, which Azure Artifacts returns 404 for even when the package exists.

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
