# Set up your machine to contribute

These instructions will get you ready to contribute to this project. If you just want to use Aspire, see [using-latest-daily.md](/docs/using-latest-daily.md).

## Prepare the machine

See [machine-requirements.md](/docs/machine-requirements.md).
## Build the repo

`./build.sh` (macOS and Linux) or `.\build.cmd` (Windows)

## Using the `dotnet` CLI

In building and testing, never use the global `dotnet` copy. Use `./dotnet.sh` on Unix, `.\dotnet.cmd` on Windows.

## Run TestShop

This will confirm that you're all set up.

In your shell or in VS Code:

```shell
# Replace "dotnet" with "./dotnet.sh" or ".\dotnet.cmd", as appropriate
dotnet restore playground/TestShop/TestShop.AppHost/TestShop.AppHost.csproj
dotnet run --project playground/TestShop/TestShop.AppHost/TestShop.AppHost.csproj
```

Or, if you are using Visual Studio:

1. Open `Aspire.slnx`
1. Set the Startup Project to be the `AppHost` project (it's under `\playground\TestShop`). Make sure the launch profile is set to "http".
1. <kbd>F5</kbd> to debug, or <kbd>Ctrl+F5</kbd> to launch without debugging.

## Using VS Code

Make sure you [build the repo](#build-the-repo) from command line at least once. Then use `./start-code.sh` (macOS and Linux) or `.\start-code.cmd` to start VS Code.

## Using Visual Studio

Make sure you [build the repo](#build-the-repo) from command line at least once using `.\build.cmd` (Windows). Then use `.\startvs.cmd` to start Visual Studio with the correct environment setup.

## Native build

The default build includes native builds for `Aspire.Cli` which produces Native AOT binaries for some platforms. These projects are in `eng/clipack/Aspire.Cli.*`.

By default it builds the cli native project for the current Runtime Identifier. A specific RIDs can be specified too by setting `$(TargetRids)` to a colon separated list like `/p:TargetRids=osx-x64:osx-arm64`.

Native build can be disabled with `/p:SkipNativeBuild=true`. And to only the native bits use `/p:SkipManagedBuild=true`.

## View Dashboard

When you start the sample app in Visual Studio, it will automatically open your browser to show the dashboard.

Otherwise if you are using the command line, when you have the Aspire app running, open the dashboard URL in your browser. The URL is shown in the app's console output like this: `Now listening on: http://localhost:15888`. You can change the default URL in the launchSettings.json file in the AppHost project.

## Localization

If you are contributing to Aspire.Dashboard, please ensure that all strings are localized. If necessary,
create a new resx file under `Aspire.Dashboard\Resources`. To reference a string, ensure the `IStringLocalizer` for the resx file is
injected. An example is below:

```xml
@inject IStringLocalizer<Resources.ResxFile> Loc
...
<p>@Loc[Resources.ResxFile.YourStringHere]</p>
```

Note that injection doesn't happen until a component's `OnInitialized`, so if you are referencing a string from codebehind, you must wait to do that
until `OnInitialized`.

## Testing

### Running Tests

To run tests, use the build script:

```bash
./build.sh --test  # Linux/macOS
./build.cmd -test # Windows
```

### Quarantined Tests

Flaky tests may be marked as quarantined to prevent them from blocking CI while being investigated and fixed. See [quarantined-tests.md](/docs/quarantined-tests.md) for more information on working with quarantined tests.

When running tests locally or in automated environments, use the quarantine filter to exclude known flaky tests:

```bash
# Replace "dotnet" with "./dotnet.sh" or ".\dotnet.cmd", as appropriate
dotnet test --filter-not-trait "quarantined=true"
```

### Testing Pull Request Changes

To test changes from a specific pull request locally, see [dogfooding-pull-requests.md](/docs/dogfooding-pull-requests.md) for instructions on installing Aspire CLI and NuGet packages built by that PR's CI run.

## Integrations (Formerly Components)

Please check the [Aspire integrations contribution guidelines](/src/Components/README.md) if you intend to make contributions to a new or existing Aspire integration.

## Generating local NuGet packages

If you want to try local changes on a separate Aspire based project or solution it can be useful to generate the NuGet packages
in a local folder and use it as a package source.

To do so simply execute:
`./build.sh -pack` (macOS and Linux) or `.\build.cmd -pack` (Windows)

This will generate all the packages in the folder `./artifacts/packages/Debug/Shipping`. At this point from your solution folder run:

```shell
# Replace "dotnet" with "./dotnet.sh" or ".\dotnet.cmd", as appropriate
dotnet nuget add source my_aspire_folder/artifacts/packages/Debug/Shipping
```

Or edit the `NuGet.config` file and add this line to the `<packageSources>` list:

```xml
<add key="aspire-dev" value="my_aspire_folder/artifacts/packages/Debug/Shipping" />
```

## Tips and known issues

Make sure you have started Docker before trying to run an Aspire app.

For information on who can help in PRs and issues, see the [area owners](/docs/area-owners.md) page.

See the [tips and known issues](/docs/tips-and-known-issues.md) page.

### Package validation

When creating a new integration, package validation will automatically try to download a previous version of the package to ensure you didn't break compat. As a result you might get the following build error:

```shell
error NU1101: Unable to find package [NEW PACKAGE NAME]. No packages exist with this id in source(s): dotnet-eng, dotnet-public, dotnet9, dotnet9-transport. PackageSourceMapping is enabled, the following source(s) were not considered: dotnet-libraries.
```

To prevent this the new package needs this line to be added to the `.csproj`:

```xml
<EnablePackageValidation>false</EnablePackageValidation>
```
