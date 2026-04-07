# Aspire.EndToEnd.Tests

The purpose of the `Aspire.EndToEnd.Tests` project is to run end-to-end tests against pre-built NuGet packages (nupkgs). These tests validate the ability to build, run, and interact with Aspire projects in both `in-repo` and `outside-of-repo` modes, ensuring compatibility with CI pipelines and local development environments.

For pull-requests in CI the tests are run via GitHub actions defined in `tests-integration.yml`.

## TL;DR or How do I use this?

### Steps to prepare for `outside-of-repo` runs

1. [Install the SDK](../Aspire.Template.Tests/README.md#install-the-sdk)

### Using it from VS

- For switching to `outside-of-repo` set `<_BuildForTestsRunningOutsideOfRepo>true</_BuildForTestsRunningOutsideOfRepo>` in the project file.
    - tests cannot be run at this point as they will fail complaining about `artifacts/bin/dotnet-latest` being missing
    - Install the SDK following the steps above
    - Run/debug the tests normally now, and they will be using the SDK
    - Also note that in this case the testproject is run from the bindir for `Aspire.EndToEnd.Tests`, so a path like `artifacts/bin/Aspire.EndToEnd.Tests/Debug/net8.0/testassets/testproject/`

### Using it from command line

- When running the tests you can either:
    - set `<TestsRunningOutsideOfRepo>true</TestsRunningOutsideOfRepo>` to `tests/Aspire.EndToEnd.Tests/Directory.props` before any imports
    - or set the environment variable `TestsRunningOutsideOfRepo=true`

## Adding tests for new integrations

The following changes need to be made to when adding a new integration:

* Add a new `TestResourceNames` [enum entry](../testproject/Common/TestResourceNames.cs).
* Add ProjectReference to the new resource/integration from the [TestProject.AppHost](../testproject/TestProject.AppHost/TestProject.AppHost.csproj) and [TestProject.IntegrationServiceA](../testproject/TestProject.IntegrationServiceA/TestProject.IntegrationServiceA.csproj) projects.
  * Add PackageVersion entries to the new packages in [Directory.Packages.Helix.props](../Shared/RepoTesting/Directory.Packages.Helix.props)
* Add entries to the AppHost.cs/Program.cs of both the AppHost and IntegrationServiceA projects.
* Add a test in [IntegrationServicesTests](../Aspire.EndToEnd.Tests/IntegrationServicesTests.cs)
  * If the integration's container starts in a reasonable time, the new test can just be a new `[InlineData]` entry to the existing `VerifyComponentWorks` test.
  * If the container takes a long time to start, or is flaky, add a separate test scenario (similar to Oracle and CosmosDb).

See https://github.com/microsoft/aspire/pull/4179 for an example.

## (details) What is the goal here?

1. We want to run some EndToEnd tests on CI, which can `dotnet run` an Aspire project,
and allow individual tests to interact with the services.
This requires:

    - Ability to build, and run an Aspire project - in other words, an SDK with the necessary components installed.
    - `docker`

2. Also, allow using `TestProject.*` in `tests/testproject`, in two modes:
- `in-repo` test run which directly reference Aspire projects, and repo targets
- `outside-of-repo` test runs which uses a SDK based on local build output

## Solution:

### SDK

[SDK](../Aspire.Template.Tests/README.md#solution-sdk)

### TestProject

- This can switch between the two test run modes using the msbuild property `$(TestsRunningOutsideOfRepo)`
    - when running `in-repo` the test project directly references hosting targets, and Aspire projects via `ProjectReference`
    - when running `outside-of-repo` the `ProjectReferences` and imports are replaced with `PackageReferences` to the Aspire nugets
- Default is to run `in-repo`

### Helix

- The tests are built on the build machine
- The testproject, and the SDK is sent to Helix
  - where the tests run using `dotnet` from the SDK path
- Since `docker` is needed to Helix, this is enabled only for `Linux` in this PR. Blocked on https://github.com/dotnet/dnceng/issues/2067 for windows support.
