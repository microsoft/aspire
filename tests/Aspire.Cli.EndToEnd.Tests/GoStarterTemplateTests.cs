// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using Aspire.Cli.EndToEnd.Tests.Helpers;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

/// <summary>
/// End-to-end tests for the Go starter template (aspire-go-starter).
/// Validates that aspire new creates a Go API + React project with optional Redis and a restored Go SDK
/// and that aspire start runs it successfully.
/// </summary>
public sealed class GoStarterTemplateTests(ITestOutputHelper output)
{
    [CaptureWorkspaceOnFailure]
    [Fact]
    public Task CreateAndRunGoStarterProject() => CreateRestoreAndBuildGoStarterAsync(emulateStableRelease: false);

    [CaptureWorkspaceOnFailure]
    [Fact]
    public Task RestoreAndBuildGoStarterFromStablePackages() => CreateRestoreAndBuildGoStarterAsync(emulateStableRelease: true);

    private async Task CreateRestoreAndBuildGoStarterAsync(bool emulateStableRelease)
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        string? stableVersion = null;
        if (emulateStableRelease)
        {
            Assert.SkipUnless(strategy.Mode == CliInstallMode.LocalHive,
                "Stable Go starter coverage requires a localhive --version X.Y.Z --archive build.");
            Assert.NotNull(strategy.ArchivePath);
            stableVersion = CliE2ETestHelpers.TryGetStableAspireVersionFromArchive(strategy.ArchivePath);
            Assert.SkipWhen(stableVersion is null,
                "Build a stable-shaped local hive and set ASPIRE_E2E_ARCHIVE to its archive.");
        }

        using var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(
            repoRoot,
            strategy,
            output,
            variant: CliE2ETestHelpers.DockerfileVariant.PolyglotGo,
            mountDockerSocket: true,
            workspace: workspace);
        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        await using var terminalRun = CliE2ETestHelpers.StartRun(terminal, workspace, auto, counter, output, TestContext.Current.CancellationToken);

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        if (emulateStableRelease)
        {
            // Do not configure the local channel: package resolution must honor the stable identity
            // and its invocation-scoped hive, rather than a persisted channel or warm NuGet cache.
            await auto.ExtractLocalHiveArchiveAsync("/tmp/aspire-localhive.tar.gz", counter);
            await auto.SourceAspireCliEnvironmentAsync(counter);
            await auto.RunCommandAsync(
                $"export ASPIRE_CLI_CHANNEL=stable ASPIRE_CLI_VERSION={stableVersion} " +
                "ASPIRE_CLI_PACKAGES=\"$HOME/.aspire/hives/local/packages\" " +
                "NUGET_PACKAGES=\"$PWD/.stable-nuget-packages\"",
                counter);
            await auto.TypeAsync("aspire --version");
            await auto.EnterAsync();
            await auto.WaitUntilAsync(
                snapshot => snapshot.ContainsText(stableVersion!) && snapshot.ContainsText("emulating identity"),
                timeout: TimeSpan.FromSeconds(30),
                description: "stable CLI identity before creating the Go starter");
            await auto.WaitForSuccessPromptAsync(counter);
        }
        else
        {
            await auto.InstallAspireCliAsync(strategy, counter);
        }

        await auto.AspireNewAsync("GoStarterApp", counter, template: AspireTemplate.GoStarter);

        // The starter template restores the generated Go SDK as part of aspire new.
        var projectRoot = Path.Combine(workspace.WorkspaceRoot.FullName, "GoStarterApp");
        var aspireModulePath = Path.Combine(projectRoot, ".aspire", "modules", "aspire.go");
        Assert.True(File.Exists(aspireModulePath), $"Expected generated Go SDK file not found: {aspireModulePath}");

        if (emulateStableRelease)
        {
            var config = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(projectRoot, "aspire.config.json")));
            Assert.NotNull(config);
            Assert.Equal(stableVersion, config["packages"]?["Aspire.Hosting.Go"]?.GetValue<string>());
            Assert.Equal(stableVersion, config["packages"]?["Aspire.Hosting.Redis"]?.GetValue<string>());
            Assert.Null(config["channel"]);
        }

        await auto.TypeAsync("cd GoStarterApp");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        if (emulateStableRelease)
        {
            await auto.RunCommandAsync("rm -rf ../.stable-nuget-packages", counter);
        }

        // A second restore must regenerate the SDK, not succeed by reusing the template's output.
        await auto.RunCommandAsync("rm -rf .aspire/modules && aspire restore --non-interactive < /dev/null", counter, TimeSpan.FromMinutes(3));
        Assert.True(File.Exists(aspireModulePath), $"Restore did not regenerate {aspireModulePath}");

        if (emulateStableRelease)
        {
            foreach (var packageId in new[] { "aspire.hosting.go", "aspire.hosting.redis" })
            {
                var metadata = $"../.stable-nuget-packages/{packageId}/{stableVersion}/.nupkg.metadata";
                await auto.RunCommandAsync(
                    $"node -e 'const fs = require(\"node:fs\"), assert = require(\"node:assert/strict\"); " +
                    $"const metadata = JSON.parse(fs.readFileSync(\"{metadata}\", \"utf8\")); " +
                    "const expected = fs.realpathSync(process.env.HOME + \"/.aspire/hives/local/packages\"); " +
                    "assert.equal(fs.realpathSync(metadata.source), expected);'",
                    counter);
            }
        }

        // The workspace is not a git repository, so VCS stamping would fail the build.
        await auto.RunCommandAsync("go build -buildvcs=false ./...", counter, TimeSpan.FromMinutes(3));
        await auto.RunCommandAsync("go vet ./... && (cd .aspire/modules && go vet ./...)", counter, TimeSpan.FromMinutes(3));

        if (!emulateStableRelease)
        {
            // The Go API runs `go mod tidy` before it starts, which downloads the OpenTelemetry modules.
            await auto.AspireStartAsync(counter, startTimeout: TimeSpan.FromMinutes(4));
            try
            {
                await auto.AssertResourcesExistAsync(counter, "cache", "api");
            }
            finally
            {
                await auto.AspireStopAsync(counter);
            }
        }
    }
}
