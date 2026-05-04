// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.EndToEnd.Tests.Helpers;
using Aspire.Cli.Tests.Utils;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

/// <summary>
/// End-to-end tests for the Python empty AppHost template (aspire-py-empty).
/// Validates that aspire new exposes the dedicated "Empty (Python AppHost)"
/// top-level entry when experimental Python polyglot support is enabled and
/// scaffolds a working Python AppHost project.
/// </summary>
public sealed class PythonEmptyAppHostTemplateTests(ITestOutputHelper output)
{
    [Fact]
    [CaptureWorkspaceOnFailure]
    public async Task CreateAndScaffoldPythonEmptyAppHostProject()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, mountDockerSocket: true, workspace: workspace);

        var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);
        await auto.EnableExperimentalPythonSupportAsync(counter);

        // Drives the interactive `aspire new` flow with the new top-level
        // "Empty (Python AppHost)" template entry. AspireNewAsync asserts the
        // highlighted "> Empty (Python AppHost)" selection appears before
        // confirming, which is the primary behavior under test for #16662.
        await auto.AspireNewAsync("PythonEmptyApp", counter, template: AspireTemplate.PythonEmptyAppHost);

        // Verify the scaffolder produces the expected .gitignore (parity with
        // Java/TypeScript empty AppHost scaffolds).
        GitIgnoreAssertions.AssertContainsEntry(
            Path.Combine(workspace.WorkspaceRoot.FullName, "PythonEmptyApp"),
            ".aspire/");

        // Note: aspire start/stop coverage for the Python empty AppHost is
        // intentionally omitted here. Python AppHost cold-start (microvenv
        // creation + dependency install from PyPI) can exceed the CLI's
        // hard-coded 120s "wait for AppHost to start" timeout in resource-
        // constrained CI runners. That is a separate product concern; this
        // test focuses on the new selection-prompt + scaffolding behavior
        // introduced for #16662.

        await auto.TypeAsync("exit");
        await auto.EnterAsync();

        await pendingRun;
    }
}
