# Helix

The Helix CI job builds `tests/helix/send-to-helix-ci.proj`, which in turns builds the `Test` target on `tests/helix/send-to-helix-inner.proj`. This inner project uses the Helix SDK to construct `@(HelixWorkItem)`s, and send them to Helix to run.

- `tests/helix/send-to-helix-basictests.targets` - this prepares all the tests that don't need special preparation
- `tests/helix/send-to-helix-endtoendtests.targets` - this is for end-to-end scenario tests that require Docker

## Controlling test runs on CI

- Tests on pull-requests run in GitHub Actions. Individual test projects can be opted-out by setting appropriate MSBuild properties:
  - `<RunOnGithubActionsWindows>false</RunOnGithubActionsWindows>` and/or
  - `<RunOnGithubActionsLinux>false</RunOnGithubActionsLinux>`.

- Tests for rolling builds run on the build machine and Helix.
Individual test projects can be opted-out by setting appropriate MSBuild properties:
  - `<RunOnAzdoCIWindows>false</RunOnAzdoCIWindows>` and/or
  - `<RunOnAzdoCILinux>false</RunOnAzdoCILinux>` and/or
  - `<RunOnAzdoHelixWindows>false</RunOnAzdoHelixWindows>` and/or
  - `<RunOnAzdoHelixLinux>false</RunOnAzdoHelixLinux>`.

## Controlling local command line test runs

- Use `--filter-method`, `--filter-class`, or `--filter-namespace` (after `--`) to run specific tests.
- Set `TestCaptureOutput=false` as an environment variable to see the output on the command line.
- Set `MSBUILDTERMINALLOGGER=false` to disable MSBuild's terminal logger so live output can be seen.

Example: `MSBUILDTERMINALLOGGER=false dotnet test --project tests/Aspire.Cli.EndToEnd.Tests/Aspire.Cli.EndToEnd.Tests.csproj --no-launch-profile -- --filter-class "*.TemplateVariantSmokeTests"`
