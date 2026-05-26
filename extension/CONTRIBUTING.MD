# Set up your machine to contribute

## Install Prerequisites

- Node.js (LTS version)
- Yarn accessible in the PATH
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

The extension is built with **yarn**, so `package.json` uses `resolutions` for transitive dependency pins and `yarn.lock` is the authoritative lockfile.

When pinning a transitive dependency (e.g. to address a security advisory), add the pin to `resolutions` and regenerate `yarn.lock` in the same change:

```bash
yarn install
```

The build rejects public npm registry URLs in `yarn.lock`; ensure regenerated entries resolve through the internal `dotnet-public-npm` feed.