# Set up your machine to contribute

## Install Prerequisites

- Node.js (LTS version) — `npm` must be on the PATH (it ships with Node.js). The
  build scripts (`build.sh` / `build.ps1`) install a pinned [Corepack](https://github.com/nodejs/corepack)
  via `npm install -g corepack@<version>` from the configured registry and seed
  Corepack's cache with the Yarn release pinned by the `packageManager` field in
  `extension/package.json`. You do **not** need to install Yarn yourself.
- Visual Studio Code (latest) or Visual Studio Code Insiders
- [Aspire CLI](https://aspire.dev/get-started/install-cli/) must be installed and available in the PATH

## Build CLI

Run `build.ps1` (Windows) or `build.sh` (Mac/Linux) to compile the Aspire CLI, install dependencies, and localize.

## Run extension locally

- Open the extension folder in Visual Studio Code.
- Launch either the `Run Extension` or `Run Extension (cli stop on entry)` launch configuration. The latter will set an environment variable that causes the CLI to wait until a debugger is attached to execute its logic.

### Optional: set the CLI path

If you want to effectively debug the Aspire CLI together with the Aspire VS Code extension, you must set the `Aspire Cli Executable Path` setting to the Aspire CLI output path. The output path, relative to the Aspire repository root directory, is `artifacts/bin/Aspire.Cli/Debug/net8.0/aspire`.

You may also want to use the `Run Extension (cli stop on entry)` launch configuration, as `Run Extension` does not prevent the Aspire CLI from executing immediately.

You can use the `Aspire: Extension settings` command to open VS Code settings directly to the Aspire extension category.

## Updating dependency overrides

The extension is built with **yarn**, pinned to the version recorded in `packageManager` of `package.json`. `package.json` uses `resolutions` for transitive dependency pins and `yarn.lock` is the authoritative lockfile.

When pinning a transitive dependency (e.g. to address a security advisory), add the pin to `resolutions` and regenerate `yarn.lock` in the same change:

```bash
corepack yarn install
```

The build rejects public npm registry URLs in `yarn.lock`; ensure regenerated entries resolve through the internal `dotnet-public-npm` feed.

## Updating the Yarn version

Edit the `"packageManager": "yarn@x.y.z"` field in `extension/package.json`. The next `build.sh` / `build.ps1` run seeds Corepack's cache with that version before calling `corepack yarn …`. No further changes are required in `build.sh`, `build.ps1`, or `extension/Extension.proj`.

> **Heads up about the internal npm mirror.** The repo's `.npmrc` and the build scripts' `NPM_REGISTRY` default route npm package downloads through the dnceng `dotnet-public-npm` Azure Artifacts feed, which is a pull-through cache of npmjs.org. The first time anyone asks the feed for a version that has never been requested, the feed has to fetch it from npmjs.org — and anonymous pull-through fetches fail with HTTP 401. Subsequent anonymous reads work fine. If you bump the pinned Corepack or Yarn version, pre-seed the feed with credentials using `npm install --global --registry https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-public-npm/npm/registry/ corepack@<version>` or `npm pack --registry https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-public-npm/npm/registry/ yarn@<version>`. Do not point `COREPACK_NPM_REGISTRY` at this Azure Artifacts feed for Yarn: Corepack requests the `/<package>/<version>` npm metadata route, and Azure Artifacts returns 404 for that route even when the package and tarball exist.

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
