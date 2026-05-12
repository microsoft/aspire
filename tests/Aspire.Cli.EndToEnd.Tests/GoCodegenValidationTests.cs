// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.EndToEnd.Tests.Helpers;
using Aspire.Cli.Tests.Utils;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

/// <summary>
/// End-to-end tests that validate Go SDK code generation: scaffold a Go AppHost, add
/// two integrations, run <c>aspire restore</c>, and verify the generated <c>.modules</c>
/// sources compile with <c>go build</c>.
/// </summary>
/// <remarks>
/// Replaces the per-integration enumeration in
/// <c>.github/workflows/polyglot-validation/test-go-playground.sh</c> with a single
/// in-test scenario, matching the existing
/// <see cref="JavaCodegenValidationTests"/> / <see cref="TypeScriptCodegenValidationTests"/>
/// pattern.
/// </remarks>
public sealed class GoCodegenValidationTests(ITestOutputHelper output)
{
    [Fact]
    [CaptureWorkspaceOnFailure]
    public async Task RestoreGeneratesSdkFiles()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(
            repoRoot,
            strategy,
            output,
            variant: CliE2ETestHelpers.DockerfileVariant.PolyglotGo,
            workspace: workspace);

        var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);
        await auto.EnableExperimentalGoSupportAsync(counter);

        await auto.TypeAsync("aspire init --language go --non-interactive");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("Created apphost.go", timeout: TimeSpan.FromMinutes(2));
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.TypeAsync("aspire add Aspire.Hosting.Redis");
        await auto.EnterAsync();
        await auto.WaitForAspireAddSuccessAsync(counter, TimeSpan.FromMinutes(2));

        await auto.TypeAsync("aspire add Aspire.Hosting.SqlServer");
        await auto.EnterAsync();
        await auto.WaitForAspireAddSuccessAsync(counter, TimeSpan.FromMinutes(2));

        await auto.TypeAsync("aspire restore");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("SDK code restored successfully", timeout: TimeSpan.FromMinutes(3));
        await auto.WaitForSuccessPromptAsync(counter);

        var modulesDir = Path.Combine(workspace.WorkspaceRoot.FullName, ".modules");
        if (!Directory.Exists(modulesDir))
        {
            throw new InvalidOperationException($".modules directory was not created at {modulesDir}");
        }

        var aspireGoPath = Path.Combine(modulesDir, "aspire.go");
        if (!File.Exists(aspireGoPath))
        {
            throw new InvalidOperationException($"Expected generated file not found: {aspireGoPath}");
        }

        var aspireGo = File.ReadAllText(aspireGoPath);
        if (!aspireGo.Contains("AddRedis"))
        {
            throw new InvalidOperationException($"{aspireGoPath} does not contain AddRedis from Aspire.Hosting.Redis");
        }

        if (!aspireGo.Contains("AddSqlServer"))
        {
            throw new InvalidOperationException($"{aspireGoPath} does not contain AddSqlServer from Aspire.Hosting.SqlServer");
        }

        // Mirrors `go build -buildvcs=false ./...` from test-go-playground.sh: the
        // generated .modules sources plus apphost.go must compile as one module.
        await auto.RunCommandFailFastAsync(
            "go build -buildvcs=false ./... && echo '[GO-BUILD-OK]'",
            counter,
            TimeSpan.FromMinutes(3));

        await auto.TypeAsync("exit");
        await auto.EnterAsync();

        await pendingRun;
    }

    [Fact]
    [CaptureWorkspaceOnFailure]
    public async Task RestoreAndCompileAllValidationFixtures()
    {
        // Replaces the per-integration enumeration loop in the deleted
        // test-go-playground.sh: for every tests/PolyglotAppHosts/<Integration>/Go fixture,
        // run `aspire restore --apphost apphost.go` and validate that the regenerated
        // .modules + apphost compile under `go build`. Catches codegen regressions
        // specific to individual integrations that the single-scenario test above does
        // not exercise.
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(
            repoRoot,
            strategy,
            output,
            variant: CliE2ETestHelpers.DockerfileVariant.PolyglotGo,
            workspace: workspace,
            additionalVolumes: new[] { PolyglotFixtureValidation.GetFixtureVolumeMount(repoRoot) });

        var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromMinutes(30));

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);
        await auto.EnableExperimentalGoSupportAsync(counter);

        await PolyglotFixtureValidation.RunFixtureLoopAsync(
            auto,
            counter,
            workspace,
            languageSubdir: "Go",
            appHostFileName: "apphost.go",
            compileCommand: "go build -buildvcs=false ./...");

        await auto.TypeAsync("exit");
        await auto.EnterAsync();

        await pendingRun;
    }
}
