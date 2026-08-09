# Aspire.Template.Tests

The purpose of the `Aspire.Template.Tests` project is to run end-to-end tests against pre-built NuGet packages (nupkgs). These tests validate the ability to create projects from templates just like a user would, and then build, run, and interact with Aspire projects to ensure compatibility with CI pipelines and local development environments.

For pull-requests in CI the tests are run via GitHub actions defined in `tests-templates.yml`.

## TL;DR or How do I use this?

1. [Install the SDK](#install-the-sdk)
2. Run/debug the tests normally now, and they will be using the SDK

## (details) What are *template* tests?

The individual tests need to create projects from templates just like a user would, and then run, and validate them. For this we need:
    - a SDK installation with the necessary components installed
    - the components should use packages from the locally built NuGet packages
    - and the NuGet packages should be used from the locally built ones

### Solution (SDK):

- SDK is installed with `$(SdkVersionForTemplateTesting)` set to the version in `global.json` by default.
- The necessary Aspire components are installed using NuGet packages from `artifacts/packages/*/Shipping`
- Then, with a custom `nuget.config` which points to the built NuGet packages in `artifacts`, the SDK is configured to use local packages
    - which installs the components using the NuGet packages from the `artifacts` into `artifacts/bin/dotnet-tests`
- This simulates the SDK being installed on a user's machine, and being independent of the aspire repo.
- At this point the SDK is usable from outside the repo by using `source /path-to-aspire-repo/dogfood.sh`
- The nuget versions for the locally built packages are like `8.0.0-dev` or `8.0.0-ci`.

### Helix

- The SDK is sent to helix, and used by all the tests for creating/running projects.

## Install the SDK

1. `.\build.cmd -pack` - to build all the NuGet packages (or `./build.sh -pack`)
2. `dotnet build tests/workloads.proj /p:Configuration=<config>`
    - this will install the SDK, and the necessary components using the NuGet packages from `artifacts/packages/*/Shipping` into `artifacts/bin/dotnet-tests`
    - note: `artifacts/bin/dotnet-none` contains the SDK with component manifests but NO components

The SDK in `artifacts/bin/dotnet-tests` is usable outside the repo at this point.

## Using the SDK outside the repo

- Follow the steps to [install the SDK](#install-the-sdk).

- The environment needs to be set up to use this. It can be done manually with:
    - Add `/path-to-aspire-repo/artifacts/bin/dotnet-tests` to `PATH`.
    - Add `artifacts/packages/$(Configuration)/Shipping` as a NuGet source for your projects, so the locally built packages can be picked up.
        - `tests/Shared/TemplateTesting/data/nuget8.config` can be used as a template for this, or you can add it manually to your `nuget.config`
        - This `nuget8.config` uses the environment variable `BUILT_NUGETS_PATH`, so set `BUILT_NUGETS_PATH=/path-to-aspire/artifacts/packages/$(Configuration)/Shipping`

- An alternative way is to use `source /path-to-aspire-repo/dogfood.sh` on Linux/macOS.
    - Copy `tests/Shared/TemplateTesting/data/nuget8.config nuget.config`
    - and set `BUILT_NUGETS_PATH=/path-to-aspire/artifacts/packages/$(Configuration)/Shipping`

## Inner loop tips

- The sdk+workload is never updated automatically. In other words, once installed the workload packs don't get overwritten even when the source binaries changes in `artifacts`. This may change in future.

There are three categories of NuGet packages used by the workload:

1. `Aspire.Dashboard.Sdk.osx-arm64`, `Aspire.TerminalHost.Sdk.osx-arm64`, `Aspire.Hosting.Orchestration.osx-arm64`, and `Aspire.AppHost.Sdk`
    - these are installed in `artifacts/bin/dotnet-tests/packs/`
    - Once the workload is installed, these are never updated automatically, so any changes made locally won't show up in the tests

2. All the other `Aspire` NuGet packages
    - These are not part of the workload itself, but can be referenced from the user projects.
    - When tests build a project referencing such a NuGet package, it gets resolved from the `artifacts`.
    - And a local tests-specific cache is used for this like `/path-to-aspire-repo/artifacts/bin/Aspire.Template.Tests/Release/net8.0/nuget-cache-Net80`
        - this is printed at the start of the test suite run

3. Project templates installed in `artifacts/bin/dotnet-tests/template-packs`.

### Testing with local changes

- If there are changes to bits that would be part of the workload packs, then those would need to be updated (copied over) manually
- The project templates don't follow the above model though. The NuGet packages for those need to be built, and then copied to `artifacts/bin/dotnet-tests/template-packs/aspire.projecttemplates.*.nupkg` for the changes to be usable in the tests

- For changes related to other NuGet packages, there are a couple of options:
    1. rebuild the NuGet package; delete the unpacked NuGet package from the cache
    2. Since the cache is never automatically deleted, copy over any changed files directly in the NuGet cache.

    Subsequent test runs would pick up the changes.
