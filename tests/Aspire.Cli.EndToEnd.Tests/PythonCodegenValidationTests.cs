// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.EndToEnd.Tests.Helpers;
using Aspire.Cli.Tests.Utils;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

/// <summary>
/// End-to-end tests that validate Python SDK code generation: scaffold a Python AppHost,
/// add two integrations, run <c>aspire restore</c>, and verify the generated
/// <c>.modules</c> Python sources are syntactically valid.
/// </summary>
/// <remarks>
/// Replaces the per-integration enumeration in
/// <c>.github/workflows/polyglot-validation/test-python-playground.sh</c> with a single
/// in-test scenario, matching the existing
/// <see cref="JavaCodegenValidationTests"/> / <see cref="TypeScriptCodegenValidationTests"/>
/// pattern.
/// </remarks>
public sealed class PythonCodegenValidationTests(ITestOutputHelper output)
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
            variant: CliE2ETestHelpers.DockerfileVariant.PolyglotPython,
            workspace: workspace);

        var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);
        await auto.EnableExperimentalPythonSupportAsync(counter);

        await auto.TypeAsync("aspire init --language python --non-interactive");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("Created apphost.py", timeout: TimeSpan.FromMinutes(2));
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

        var aspireAppPath = Path.Combine(modulesDir, "aspire_app.py");
        if (!File.Exists(aspireAppPath))
        {
            throw new InvalidOperationException($"Expected generated file not found: {aspireAppPath}");
        }

        var aspireApp = File.ReadAllText(aspireAppPath);
        if (!aspireApp.Contains("add_redis"))
        {
            throw new InvalidOperationException($"{aspireAppPath} does not contain add_redis from Aspire.Hosting.Redis");
        }

        if (!aspireApp.Contains("add_sql_server"))
        {
            throw new InvalidOperationException($"{aspireAppPath} does not contain add_sql_server from Aspire.Hosting.SqlServer");
        }

        // Mirrors the python compile loop from test-python-playground.sh: every generated
        // .py file in .modules plus apphost.py must parse cleanly.
        await auto.RunCommandFailFastAsync(
            "python3 -c \"import pathlib, py_compile; " +
            "files = [pathlib.Path('apphost.py')] + sorted(pathlib.Path('.modules').rglob('*.py')); " +
            "[py_compile.compile(str(f), doraise=True) for f in files]; " +
            "print('[PYTHON-COMPILE-OK]')\"",
            counter,
            TimeSpan.FromMinutes(2));

        await auto.TypeAsync("exit");
        await auto.EnterAsync();

        await pendingRun;
    }

    [Fact]
    [CaptureWorkspaceOnFailure]
    public async Task RestoreAndCompileAllValidationFixtures()
    {
        // Replaces the per-integration enumeration loop in the deleted
        // test-python-playground.sh: for every tests/PolyglotAppHosts/<Integration>/Python
        // fixture, run `aspire restore --apphost apphost.py` and validate that the
        // regenerated `.modules/aspire_app.py` + apphost compile cleanly. Catches
        // codegen regressions specific to individual integrations (Kafka surface,
        // Azure.* surface, etc.) that the single-scenario test above does not exercise.
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(
            repoRoot,
            strategy,
            output,
            variant: CliE2ETestHelpers.DockerfileVariant.PolyglotPython,
            workspace: workspace,
            additionalVolumes: new[] { PolyglotFixtureValidation.GetFixtureVolumeMount(repoRoot) });

        var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromMinutes(30));

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);
        await auto.EnableExperimentalPythonSupportAsync(counter);

        await PolyglotFixtureValidation.RunFixtureLoopAsync(
            auto,
            counter,
            workspace,
            languageSubdir: "Python",
            appHostFileName: "apphost.py",
            // Mirrors the python compile loop from test-python-playground.sh: every generated
            // .py file in .modules plus apphost.py must parse cleanly.
            compileCommand: "python3 -c \"import pathlib, py_compile; "
                + "files = [pathlib.Path('apphost.py')] + sorted(pathlib.Path('.modules').rglob('*.py')); "
                + "[py_compile.compile(str(f), doraise=True) for f in files]\"");

        await auto.TypeAsync("exit");
        await auto.EnterAsync();

        await pendingRun;
    }
}
