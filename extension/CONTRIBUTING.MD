# Set up your machine to contribute

## Install Prerequisites

- Node.js (LTS version)
- NPM and Corepack accessible in the PATH. Use Corepack to enable the Yarn version pinned in `package.json`.
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

The extension is built with both **npm** and **yarn**, so `package.json` carries two parallel pin mechanisms:

- `overrides` — consumed by npm when generating `package-lock.json`
- `resolutions` — consumed by yarn when generating `yarn.lock`

When pinning a transitive dependency (e.g. to address a security advisory), add the pin to **both** sections and regenerate **both** lockfiles in the same change:

```bash
npm install
yarn install
```

Keeping the two sections and the two lockfiles in sync prevents drift between the npm-resolved and yarn-resolved dependency graphs.