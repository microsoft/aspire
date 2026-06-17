// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Xml.Linq;
using Aspire.Cli.EndToEnd.Tests.Helpers;
using Aspire.Cli.Tests.Utils;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

/// <summary>
/// E2E coverage for <c>aspire init</c>'s C# project-mode path (the workspace contains a
/// <c>.sln</c> or <c>.slnx</c>, so <c>SolutionLocator</c> routes init to
/// <c>DropCSharpProjectSkeletonAsync</c> instead of the single-file branch).
/// </summary>
/// <remarks>
/// <para>
/// The single-file path is exercised by <see cref="SingleFileAppHostInitDotnetRunTests"/>.
/// Every other E2E that invokes <c>aspire init</c> starts in an empty workspace, so
/// init deterministically takes the single-file branch — the project-mode skeleton drop
/// (<c>dotnet new aspire-apphost</c> + workspace <c>nuget.config</c> write + template
/// install) is otherwise only exercised by unit tests with a mocked
/// <c>IDotNetCliRunner</c>. Regressions in <c>TemplateNuGetConfigService</c>,
/// <c>NuGetConfigMerger</c> write semantics, the <c>aspire-apphost</c> template's
/// <c>restore</c> post-action, or <c>SolutionLocator</c>'s <c>.sln</c>/<c>.slnx</c>
/// discovery would slip through unit coverage but be caught here.
/// </para>
/// <para>
/// On a non-stable CLI channel (<c>pr-&lt;N&gt;</c>, locally-built <c>local</c>,
/// <c>staging</c>, <c>daily</c>), <c>Aspire.AppHost.Sdk/&lt;version&gt;</c> exists only
/// in the channel feed and not on nuget.org. If the workspace <c>nuget.config</c> isn't
/// written to the solution dir before <c>dotnet new aspire-apphost</c>'s built-in
/// <c>restore</c> post-action runs (or before a follow-up <c>dotnet build</c>), SDK
/// resolution fails with <c>error MSB4236: The SDK 'Aspire.AppHost.Sdk/...' could not
/// be found</c>. That is the exact failure mode of the bug fixed by
/// https://github.com/microsoft/aspire/issues/17104.
/// </para>
/// </remarks>
public sealed class CSharpProjectModeInitTests(ITestOutputHelper output)
{
    /// <summary>
    /// Runs <c>aspire init</c> in a workspace containing a stub solution file, then
    /// verifies that the generated <c>Test.AppHost/Test.AppHost.csproj</c> can be
    /// built against the channel-matched hive.
    /// </summary>
    /// <remarks>
    /// The <c>dotnet build</c> step is the regression assertion: without the workspace
    /// <c>nuget.config</c> written by <c>DropCSharpProjectSkeletonAsync</c>, MSBuild
    /// cannot resolve <c>Aspire.AppHost.Sdk/&lt;version&gt;</c> from the local-archive
    /// or PR hive and fails with MSB4236. On the stable channel the same write happens
    /// (with <c>&lt;clear/&gt;</c> + nuget.org as the only source), so the assertion
    /// still meaningfully exercises the new code path.
    /// </remarks>
    [CaptureWorkspaceOnFailure]
    [Theory]
    [InlineData("Test.sln")]
    [InlineData("Test.slnx")]
    public async Task AspireInit_SolutionFile_BuildsAgainstChannelHive(string solutionFileName)
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        var workspace = TemporaryWorkspace.Create(output);

        // Pre-create a stub solution file so SolutionLocator routes init to project mode.
        // The contents are not inspected — SolutionLocator only looks at the extension —
        // and the follow-up `dotnet build` is invoked on the generated .csproj directly.
        var solutionPath = Path.Combine(workspace.WorkspaceRoot.FullName, solutionFileName);
        File.WriteAllText(solutionPath, "Fake solution file");

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, mountDockerSocket: false, workspace: workspace);
        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        await using var terminalRun = CliE2ETestHelpers.StartRun(terminal, workspace, auto, counter, output, TestContext.Current.CancellationToken);

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);

        // `AspireInitAsync` runs `aspire init --language csharp` and handles any NuGet.config /
        // URLs / agent-init prompts that may appear. Routing to project-mode happens after
        // language selection via SolutionLocator regardless of how the language was chosen,
        // so the --language flag is equivalent to accepting the interactive '> C#' default
        // for the purposes of this regression.
        await auto.AspireInitAsync(counter);

        var workspaceNuGetConfig = Path.Combine(workspace.WorkspaceRoot.FullName, "nuget.config");
        var appHostCsproj = Path.Combine(workspace.WorkspaceRoot.FullName, "Test.AppHost", "Test.AppHost.csproj");

        Assert.True(
            File.Exists(workspaceNuGetConfig),
            $"Expected workspace nuget.config next to the solution at: {workspaceNuGetConfig}. "
            + "Without it, the channel-pinned Aspire.AppHost.Sdk/<version> cannot be resolved on non-stable CLI channels.");
        Assert.True(File.Exists(appHostCsproj), $"Expected AppHost csproj at: {appHostCsproj}");

        // Sanity that the workspace nuget.config actually carries package sources. Full
        // structural assertions (<clear/>, packageSourceMapping shape, channel-feed name)
        // live in InitCommand_ProjectMode_NoChannelOverride_* unit tests — this E2E only
        // needs to confirm a real file was written before letting `dotnet build` exercise
        // it for real.
        var nuGetConfigDoc = XDocument.Load(workspaceNuGetConfig);
        Assert.NotEmpty(nuGetConfigDoc.Root!.Elements("packageSources").Elements("add"));

        // The regression assertion: without the channel-pinned workspace nuget.config,
        // `dotnet build` fails with `error MSB4236: The SDK 'Aspire.AppHost.Sdk/...'
        // could not be found.` 3 minutes is enough headroom for a cold restore + build on
        // CI; the cache-hit case (the template's `restore` post-action already populated
        // ~/.nuget/packages during init) finishes well under 30 seconds. A build failure
        // surfaces immediately via the shell's ERR prompt instead of timing out.
        await auto.RunCommandAsync(
            "dotnet build Test.AppHost/Test.AppHost.csproj",
            counter,
            TimeSpan.FromMinutes(3));
    }

    /// <summary>
    /// Rerun-recovery: when a previous broken CLI run left the AppHost dir scaffolded
    /// but no workspace <c>nuget.config</c>, re-running <c>aspire init</c> should write
    /// the missing config and exit successfully without disturbing files left in the
    /// AppHost dir.
    /// </summary>
    /// <remarks>
    /// Guards the placement of the <c>CreateOrUpdateNuGetConfigWithoutPromptAsync</c>
    /// call BEFORE the <c>Directory.Exists(appHostDirPath)</c> early return in
    /// <c>DropCSharpProjectSkeletonAsync</c>. A refactor that moves the helper call back
    /// below the early return would leave users on a broken workspace with no recovery
    /// path short of deleting the half-scaffolded AppHost dir manually.
    /// </remarks>
    [CaptureWorkspaceOnFailure]
    [Fact]
    public async Task AspireInit_ExistingAppHostDir_RecreatesNuGetConfigKeepsFiles()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        var workspace = TemporaryWorkspace.Create(output);

        // Simulate the broken state a previous CLI version could leave: a solution +
        // half-scaffolded AppHost dir with a user-touched file, but no workspace
        // nuget.config. The recovery path must (a) write the missing nuget.config, (b)
        // leave the AppHost dir alone, and (c) exit successfully.
        var solutionPath = Path.Combine(workspace.WorkspaceRoot.FullName, "Test.sln");
        File.WriteAllText(solutionPath, "Fake solution file");
        var appHostDir = Directory.CreateDirectory(Path.Combine(workspace.WorkspaceRoot.FullName, "Test.AppHost"));
        var leftoverPath = Path.Combine(appHostDir.FullName, "leftover.txt");
        const string LeftoverContent = "user file from a previous broken init";
        File.WriteAllText(leftoverPath, LeftoverContent);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, mountDockerSocket: false, workspace: workspace);
        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        await using var terminalRun = CliE2ETestHelpers.StartRun(terminal, workspace, auto, counter, output, TestContext.Current.CancellationToken);

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);

        // The early-return path still chains through the common agent-init prompt at the
        // end of the init flow, so `AspireInitAsync` reaches its terminal state cleanly.
        await auto.AspireInitAsync(counter);

        var workspaceNuGetConfig = Path.Combine(workspace.WorkspaceRoot.FullName, "nuget.config");

        Assert.True(
            File.Exists(workspaceNuGetConfig),
            $"Expected workspace nuget.config next to the solution at: {workspaceNuGetConfig}. "
            + "The CreateOrUpdateNuGetConfigWithoutPromptAsync call must run BEFORE the "
            + "Directory.Exists(appHostDirPath) early return so reruns recover the missing config.");
        Assert.True(File.Exists(leftoverPath), $"Pre-existing AppHost file should be preserved: {leftoverPath}");
        Assert.Equal(LeftoverContent, File.ReadAllText(leftoverPath));
    }
}
