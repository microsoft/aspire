# Set up your machine to contribute

These instructions will get you ready to contribute to this project. If you just want to use Aspire, see [using-latest-daily.md](/docs/using-latest-daily.md).

## Contents

- [Getting set up](#getting-set-up)
  - [Prepare the machine](#prepare-the-machine)
  - [Using the `dotnet` CLI](#using-the-dotnet-cli)
  - [Build the repo](#build-the-repo)
- [Verify your setup](#verify-your-setup)
  - [Run TestShop](#run-testshop)
  - [View Dashboard](#view-dashboard)
- [Testing](#testing)
  - [Running tests](#running-tests)
  - [Quarantined and outerloop tests](#quarantined-and-outerloop-tests)
  - [Testing pull request changes](#testing-pull-request-changes)
- [Coding Agents](#coding-agents)
  - [`code-review`](#code-review)
  - [`pr-testing`](#pr-testing)
  - [`cli-e2e-testing`](#cli-e2e-testing)
  - [`ci-test-failures`](#ci-test-failures)
- [Development environments](#development-environments)
  - [Using VS Code](#using-vs-code)
  - [Using Visual Studio](#using-visual-studio)
- [Area-specific guidance](#area-specific-guidance)
  - [Localization](#localization)
  - [Integrations](#integrations)
  - [Native build](#native-build)
  - [Building the VS Code extension](#building-the-vs-code-extension)
- [Trying your changes locally](#trying-your-changes-locally)
  - [Generating local NuGet packages](#generating-local-nuget-packages)
  - [Creating a local Aspire build with `localhive`](#creating-a-local-aspire-build-with-localhive)
- [Tips and known issues](#tips-and-known-issues)
  - [Package validation](#package-validation)

## Getting set up

### Prepare the machine

See [machine-requirements.md](/docs/machine-requirements.md).

### Using the `dotnet` CLI

After restore, `dotnet` commands run from this repo use the repo-local SDK because `global.json` includes `.dotnet` in the SDK search path. Use `./dotnet.sh` on Unix or `.\dotnet.cmd` on Windows when you need to force the repo-local SDK explicitly.

### Build the repo

First run `./restore.sh` (macOS and Linux) or `.\restore.cmd` (Windows) to install the repo-local .NET SDK. Then build with `./build.sh` (macOS and Linux) or `.\build.cmd` (Windows).

## Verify your setup

### Run TestShop

This will confirm that you're all set up.

In your shell or in VS Code:

```shell
dotnet restore playground/TestShop/TestShop.AppHost/TestShop.AppHost.csproj
dotnet run --project playground/TestShop/TestShop.AppHost/TestShop.AppHost.csproj
```

Or, if you are using Visual Studio:

1. Open `Aspire.slnx`
2. Set the Startup Project to be the `AppHost` project (it's under `\playground\TestShop`). Make sure the launch profile is set to "http".
3. <kbd>F5</kbd> to debug, or <kbd>Ctrl+F5</kbd> to launch without debugging.

### View Dashboard

When you start the sample app in Visual Studio, it will automatically open your browser to show the dashboard.

Otherwise if you are using the command line, when you have the Aspire app running, open the dashboard URL in your browser. The URL is shown in the app's console output like this: `Now listening on: http://localhost:15888`. You can change the default URL in the launchSettings.json file in the AppHost project.

## Testing

### Running tests

To run tests, use the build script:

```bash
./build.sh --test  # Linux/macOS
.\build.cmd --test # Windows
```

### Quarantined and outerloop tests

Flaky tests may be marked as quarantined to prevent them from blocking CI while being investigated and fixed. See [quarantined-tests.md](/docs/quarantined-tests.md) for more information on working with quarantined tests.

Long-running or resource-intensive tests may be marked as outerloop. See [outerloop-tests.md](/docs/outerloop-tests.md) for more information.

When running tests locally or in automated environments, use the test filters to exclude known flaky and outerloop tests:

```bash
dotnet test --no-launch-profile -- \
  --filter-not-trait "quarantined=true" \
  --filter-not-trait "outerloop=true"
```

### Testing pull request changes

To test changes from a specific pull request locally, see [dogfooding-pull-requests.md](/docs/dogfooding-pull-requests.md) for instructions on installing Aspire CLI and NuGet packages built by that PR's CI run.

## Coding Agents

Aspire uses GitHub Copilot automatic code review on pull requests. We expect Copilot review comments to be reviewed and addressed before merging, either by making the requested change or by explaining why a suggested change is not needed.

The Aspire repository also includes custom Copilot skills that team members and automation may run on PRs, even when the PR author is not using an AI coding agent. Contributors can get a head start by running the key skills before requesting review:

> [!NOTE]
> AI-based code review and end-to-end testing can help find issues earlier, but they are not a replacement for human review and manual testing. Contributors are still responsible for understanding their changes and verifying the scenarios they affect.

### [`code-review`](/.agents/skills/code-review/SKILL.md)

Reviews a PR for high-confidence problems only, such as bugs, security issues, correctness errors, performance regressions, missing boundary error handling, concurrency or resource issues, flaky test patterns, and repository convention violations. It avoids style nits and duplicate review comments.

### [`pr-testing`](/.agents/skills/pr-testing/SKILL.md)

Installs the Aspire CLI and packages from a PR's dogfood build, verifies the installed CLI matches the PR head commit, analyzes changed areas, proposes targeted happy-path and negative test scenarios, runs the selected scenarios locally or in the repo container runner, captures evidence, and can produce a PR testing report.

### [`cli-e2e-testing`](/.agents/skills/cli-e2e-testing/SKILL.md)

Guides Aspire CLI end-to-end test authoring and debugging with Hex1b terminal automation. It covers test structure, local `localhive` archive workflows, Docker-based execution, install modes, prompt detection, and asciinema recordings for failures.

### [`ci-test-failures`](/.agents/skills/ci-test-failures/SKILL.md)

Guides GitHub Actions test-failure diagnosis. It covers downloading failed job logs and artifacts, extracting failed tests from runs, investigating failures without filing issues, and creating or updating failing-test issues with the repo tooling.

Other repo skills can help with specialized work, but these are the main skills the Aspire team uses to evaluate PR quality, dogfoodability, CLI end-to-end coverage, and CI test failures.

## Development environments

### Using VS Code

Make sure you [build the repo](#build-the-repo) from command line at least once. Then use `./start-code.sh` (macOS and Linux) or `.\start-code.cmd` to start VS Code.

### Using Visual Studio

Make sure you [build the repo](#build-the-repo) from command line at least once using `.\build.cmd` (Windows). Then use `.\startvs.cmd` to start Visual Studio with the correct environment setup.

## Area-specific guidance

### Localization

If you are contributing to Aspire.Dashboard, please ensure that all strings are localized. If necessary,
create a new resx file under `src/Aspire.Dashboard/Resources`. To reference a string, ensure the `IStringLocalizer` for the resx file is
injected. An example is below:

```xml
@inject IStringLocalizer<Resources.ResxFile> Loc
...
<p>@Loc[Resources.ResxFile.YourStringHere]</p>
```

Note that injection doesn't happen until a component's `OnInitialized`, so if you are referencing a string from codebehind, you must wait to do that
until `OnInitialized`.

The `*.Designer.cs` files are checked in with the matching `*.resx` files. If you add, remove, or rename resources, update the matching designer file too. If the project has an `xlf` directory, run `dotnet build /t:UpdateXlf <path-to-project.csproj>` to update localization files instead of editing `*.xlf` files manually.

### Integrations

Please check the [Aspire integrations contribution guidelines](/src/Components/README.md) if you intend to make contributions to a new or existing Aspire integration.

### Native build

The default build includes native builds for `Aspire.Cli` which produces Native AOT binaries for some platforms. These projects are in `eng/clipack/Aspire.Cli.*`.

By default it builds the CLI native project for the current Runtime Identifier. Specific RIDs can be specified by setting `$(TargetRids)` to a colon separated list like `/p:TargetRids=osx-x64:osx-arm64`.

Native build can be disabled with `/p:SkipNativeBuild=true`. To build only the native bits, use `/p:SkipManagedBuild=true`.

### Building the VS Code extension

The Aspire VS Code extension lives under `extension/`. To build the extension through the repo build, make sure Node.js, yarn, and `vsce` are on your PATH, then run:

```bash
./build.sh --build-extension  # macOS/Linux
.\build.cmd /p:BuildExtension=true # Windows
```

This runs the `extension/Extension.proj` build, installs extension dependencies with the checked-in `yarn.lock`, compiles the extension, and creates the VSIX artifacts under `artifacts/packages/Debug/vscode`.

For extension inner-loop development, you can work directly in the extension folder:

```bash
cd extension
yarn install --frozen-lockfile --non-interactive
yarn compile
```

Use `yarn watch` while editing TypeScript. When adding or changing user-facing extension text, keep the strings localized in both `extension/package.nls.json` and `extension/src/loc/strings.ts`. For VSIX signing and release packaging details, see [extension-signing.md](/docs/extension-signing.md).

## Trying your changes locally

### Generating local NuGet packages

If you only need package outputs, it can be useful to generate the NuGet packages in a local folder and use it as a package source from a separate Aspire-based project or solution. If you want to validate a complete locally-built Aspire product, including the CLI, templates, package hive, and bundle payload, use [`localhive`](#creating-a-local-aspire-build-with-localhive) instead.

To do so simply execute:
`./build.sh --pack` (macOS and Linux) or `.\build.cmd --pack` (Windows)

This will generate all the packages in the folder `./artifacts/packages/Debug/Shipping`. At this point from your solution folder run:

```shell
dotnet nuget add source my_aspire_folder/artifacts/packages/Debug/Shipping
```

Or edit the `NuGet.config` file and add this line to the `<packageSources>` list:

```xml
<add key="aspire-dev" value="my_aspire_folder/artifacts/packages/Debug/Shipping" />
```

### Creating a local Aspire build with `localhive`

Use `localhive` when you want a fully usable Aspire product from your local source tree, not just a folder of NuGet packages. The script builds and packs the Aspire packages, creates an Aspire hive, builds the bundle payload, and installs a locally-built Aspire CLI. The CLI then discovers the hive as a channel, so commands like `aspire new`, `aspire add`, and `aspire init` use the packages produced by your custom build.

Prefer using an explicit output directory so your custom build stays isolated and does not overwrite the default Aspire install under `$HOME/.aspire`:

```bash
./localhive.sh -c Release -n my-feature -o ./artifacts/localhive/my-feature
export PATH="$PWD/artifacts/localhive/my-feature/bin:$PATH"
aspire --version
```

On Windows:

```powershell
.\localhive.ps1 -c Release -n my-feature -o .\artifacts\localhive\my-feature
$env:PATH = "$(Resolve-Path .\artifacts\localhive\my-feature\bin);$env:PATH"
aspire --version
```

To create a standalone portable build that can be copied to another machine, add a target RID and the archive flag:

```bash
./localhive.sh -c Release -n my-feature -o ./artifacts/localhive/linux-x64 -r linux-x64 --archive
```

On Windows:

```powershell
.\localhive.ps1 -c Release -n my-feature -o .\artifacts\localhive\win-x64 -r win-x64 -Archive
```

The archive contains the CLI, local package hive, and bundle payload needed for that build. After extracting it on the target machine, run the `aspire` binary from the extracted `bin` directory.

## Tips and known issues

Make sure you have started Docker before trying to run an Aspire app.

For information on who can help in PRs and issues, see the [area owners](/docs/area-owners.md) page.

See the [tips and known issues](/docs/tips-and-known-issues.md) page.

### Package validation

When creating a new integration, package validation will automatically try to download a previous version of the package to ensure you didn't break compat. As a result you might get the following build error:

```shell
error NU1101: Unable to find package [NEW PACKAGE NAME]. No packages exist with this id in source(s): dotnet-eng, dotnet-public, dotnet9, dotnet10, dotnet9-transport. PackageSourceMapping is enabled, the following source(s) were not considered: dotnet-libraries.
```

To prevent this the new package needs this line to be added to the `.csproj`:

```xml
<EnablePackageValidation>false</EnablePackageValidation>
```
