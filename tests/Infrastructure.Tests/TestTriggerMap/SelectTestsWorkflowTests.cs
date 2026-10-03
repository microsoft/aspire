// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Xml.Linq;
using Aspire.TestUtilities;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace Infrastructure.Tests.TestTriggerMap;

/// <summary>
/// Guards durable workflow contracts around the SelectTests engine that are not exercised by its
/// command-line and project-graph tests.
/// </summary>
public sealed class SelectTestsWorkflowTests(ITestOutputHelper output)
{
    private static readonly YamlMappingNode s_selectTestsAction = LoadYaml(
        ".github", "actions", "select-tests", "action.yml");
    private static readonly YamlMappingNode s_checkChangedFilesAction = LoadYaml(
        ".github", "actions", "check-changed-files", "action.yml");
    private static readonly YamlMappingNode s_testsWorkflow = LoadYaml(
        ".github", "workflows", "tests.yml");
    private static readonly YamlMappingNode s_testsJobs = Mapping(s_testsWorkflow, "jobs");
    private static readonly YamlMappingNode s_nativeArchivesWorkflow = LoadYaml(
        ".github", "workflows", "build-cli-native-archives.yml");
    private static readonly YamlMappingNode s_nativeDashboardWorkflow = LoadYaml(
        ".github", "workflows", "native-dashboard-validation.yml");

    [Fact]
    public void SelectTestsActionGatesForceAllOnBooleanInput()
    {
        var forceAll = Mapping(Mapping(s_selectTestsAction, "inputs"), "forceAll");
        Assert.Equal("false", Scalar(forceAll, "default"));

        var select = StepById(ActionSteps(s_selectTestsAction), "select");
        Assert.Equal("${{ inputs.forceAll }}", Scalar(Mapping(select, "env"), "FORCE_ALL"));

        // --force-all is a public SelectTests CLI token; the action must forward the boolean input
        // through that command-line contract rather than reinterpret selection itself.
        var script = Scalar(select, "run");
        Assert.Contains("[ \"$FORCE_ALL\" = \"true\" ]", script, StringComparison.Ordinal);
        Assert.Contains("args+=(--force-all)", script, StringComparison.Ordinal);
    }

    [Fact]
    public void SelectTestsActionInstallsPinnedSdkWithArcadeWrapperArguments()
    {
        var versions = XDocument.Load(RepoPath("eng", "Versions.props"));
        Assert.Single(versions.Descendants("DotNetSdkNet10VersionForTesting"));

        var install = Assert.Single(
            ActionSteps(s_selectTestsAction),
            step => Scalar(step, "run")?.Contains("./eng/common/dotnet-install.sh", StringComparison.Ordinal) == true);
        var script = Scalar(install, "run");

        // These literals are the external Arcade installer interface required to install an SDK
        // (not merely a runtime) at the version selected by eng/Versions.props.
        Assert.Contains("<DotNetSdkNet10VersionForTesting>", script, StringComparison.Ordinal);
        Assert.Contains("-runtime sdk", script, StringComparison.Ordinal);
        Assert.Contains("-version \"$sdk_version\"", script, StringComparison.Ordinal);
    }

    [Fact]
    public void TestsWorkflowComputesForceAllFromFullCiLabel()
    {
        var select = StepByUses(Steps(Mapping(s_testsJobs, "setup_for_tests")), "./.github/actions/select-tests");

        Assert.Equal(
            "${{ contains(github.event.pull_request.labels.*.name, 'run-full-ci') }}",
            Scalar(Mapping(select, "with"), "forceAll"));
    }

    [Fact]
    public void TestsWorkflowEnforcesSelectedTestSubset()
    {
        var select = StepByUses(Steps(Mapping(s_testsJobs, "setup_for_tests")), "./.github/actions/select-tests");

        Assert.Equal("true", Scalar(Mapping(select, "with"), "enforce"));
    }

    [Fact]
    public void WindowsArm64NativeArchiveUsesVs2026ArmRunner()
    {
        var testsTarget = Scalar(
            Mapping(Mapping(s_testsJobs, "build_cli_archive_windows_arm64"), "with"),
            "targets");
        AssertWindowsArm64Target(testsTarget);

        var workflowCall = Mapping(Mapping(s_nativeArchivesWorkflow, "on"), "workflow_call");
        var defaultTargets = Scalar(Mapping(Mapping(workflowCall, "inputs"), "targets"), "default");
        AssertWindowsArm64Target(defaultTargets);
    }

    [Fact]
    public void NativeArchiveDependencyPackagesUseMatrixRid()
    {
        var archiveJob = Mapping(Mapping(s_nativeArchivesWorkflow, "jobs"), "build_cli_archives");
        var packageBuild = Assert.Single(
            Steps(archiveJob),
            step => Scalar(step, "run")?.Contains("BuildBundleDepsOnly=true", StringComparison.Ordinal) == true);
        var command = Scalar(packageBuild, "run");

        // These MSBuild properties are the interface that binds each matrix lane to its RID-specific
        // dependency packages.
        Assert.Contains("/p:BuildBundleDepsOnly=true", command, StringComparison.Ordinal);
        Assert.Contains("/p:TargetRids=${{ matrix.targets.rids }}", command, StringComparison.Ordinal);
    }

    [Fact]
    public void NativeDashboardInteractivityOptsIntoOuterloopTests()
    {
        var validationJob = Mapping(Mapping(s_nativeDashboardWorkflow, "jobs"), "validate");
        var interactiveTest = Assert.Single(
            Steps(validationJob),
            step => Scalar(step, "run")?.Contains(
                "NativeDashboard_LoadsInteractivePageWithoutBrowserErrors",
                StringComparison.Ordinal) == true);
        var command = Scalar(interactiveTest, "run");

        Assert.Equal("${{ inputs.rid == 'win-x64' }}", Scalar(interactiveTest, "if"));
        // These are public MSBuild/MTP switches; changing either silently moves this coverage out of
        // the intended execution lane.
        Assert.Contains("/p:RunOuterloopTests=true", command, StringComparison.Ordinal);
        Assert.Contains(
            "--filter-method \"*.NativeDashboard_LoadsInteractivePageWithoutBrowserErrors\"",
            command,
            StringComparison.Ordinal);
        Assert.Contains("--filter-not-trait \"quarantined=true\"", command, StringComparison.Ordinal);
    }

    [Fact]
    public void TestsWorkflowPassesPrHeadShaToSelector()
    {
        var select = StepByUses(Steps(Mapping(s_testsJobs, "setup_for_tests")), "./.github/actions/select-tests");
        Assert.Equal(
            "${{ github.event.pull_request.head.sha }}",
            Scalar(Mapping(select, "with"), "headSha"));

        var headSha = Mapping(Mapping(s_selectTestsAction, "inputs"), "headSha");
        Assert.Equal("${{ github.event.pull_request.head.sha }}", Scalar(headSha, "default"));
    }

    [Fact]
    [RequiresTools(["bash", "git", "jq"])]
    public async Task CheckChangedFilesActionReportsBothSidesOfRenames()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        await RunGitAsync(workspace.Path, "init", "--quiet");
        await RunGitAsync(workspace.Path, "config", "user.email", "workflow-tests@example.invalid");
        await RunGitAsync(workspace.Path, "config", "user.name", "Workflow Tests");
        await RunGitAsync(workspace.Path, "config", "commit.gpgsign", "false");

        var sourceDirectory = workspace.CreateDirectory("src").FullName;
        var sourcePath = Path.Combine(sourceDirectory, "critical.txt");
        await File.WriteAllTextAsync(sourcePath, "content");
        await RunGitAsync(workspace.Path, "add", "--all");
        await RunGitAsync(workspace.Path, "commit", "--quiet", "-m", "base");
        var baseSha = (await RunGitAsync(workspace.Path, "rev-parse", "HEAD")).StandardOutput.Trim();

        var destinationDirectory = Directory.CreateDirectory(
            Path.Combine(workspace.Path, "docs", "skippable")).FullName;
        File.Move(sourcePath, Path.Combine(destinationDirectory, "critical.txt"));
        await RunGitAsync(workspace.Path, "add", "--all");
        await RunGitAsync(workspace.Path, "commit", "--quiet", "-m", "rename");
        var headSha = (await RunGitAsync(workspace.Path, "rev-parse", "HEAD")).StandardOutput.Trim();

        const string patternsFileName = "skippable-patterns.txt";
        await File.WriteAllTextAsync(Path.Combine(workspace.Path, patternsFileName), "docs/**\n");

        var checkFiles = StepById(ActionSteps(s_checkChangedFilesAction), "check_files");
        var script = Assert.IsType<string>(Scalar(checkFiles, "run"))
            .Replace("${{ github.event_name }}", "pull_request", StringComparison.Ordinal)
            .Replace("${{ inputs.patterns_file }}", patternsFileName, StringComparison.Ordinal)
            .Replace("${{ github.event.pull_request.base.sha }}", baseSha, StringComparison.Ordinal)
            .Replace("${{ github.event.pull_request.head.sha }}", headSha, StringComparison.Ordinal);
        var githubOutputPath = Path.Combine(workspace.Path, "github-output");
        var runnerPath = Path.Combine(workspace.Path, "run-check-changed-files.sh");
        await File.WriteAllTextAsync(
            runnerPath,
            $"""
            #!/bin/bash
            set -euo pipefail
            export GITHUB_WORKSPACE={ShellQuote(workspace.Path)}
            export GITHUB_OUTPUT={ShellQuote(githubOutputPath)}
            {script}
            """);

        var result = await ProcessRunner.RunAsync(output, "bash", [runnerPath], workspace.Path);
        Assert.Equal(0, result.ExitCode);

        var outputs = await File.ReadAllTextAsync(githubOutputPath);
        Assert.Contains("only_changed=false", await File.ReadAllLinesAsync(githubOutputPath));
        Assert.Equal(
            ["docs/skippable/critical.txt", "src/critical.txt"],
            ReadJsonOutput(outputs, "changed_files").Order(StringComparer.Ordinal));
        Assert.Equal(["docs/skippable/critical.txt"], ReadJsonOutput(outputs, "matched_files"));
        Assert.Equal(["src/critical.txt"], ReadJsonOutput(outputs, "unmatched_files"));
    }

    private static void AssertWindowsArm64Target(string? json)
    {
        Assert.NotNull(json);
        using var document = JsonDocument.Parse(json);
        var target = Assert.Single(
            document.RootElement.EnumerateArray(),
            candidate => candidate.GetProperty("rids").GetString() == "win-arm64");

        Assert.Equal("windows-latest", target.GetProperty("os").GetString());
        Assert.Equal("windows-11-vs2026-arm", target.GetProperty("runner").GetString());
    }

    private static List<YamlMappingNode> ActionSteps(YamlMappingNode action)
        => Sequence(Mapping(action, "runs"), "steps").Cast<YamlMappingNode>().ToList();

    private static List<YamlMappingNode> Steps(YamlMappingNode job)
        => Sequence(job, "steps").Cast<YamlMappingNode>().ToList();

    private static YamlMappingNode StepById(IEnumerable<YamlMappingNode> steps, string id)
        => Assert.Single(steps, step => Scalar(step, "id") == id);

    private static YamlMappingNode StepByUses(IEnumerable<YamlMappingNode> steps, string uses)
        => Assert.Single(steps, step => Scalar(step, "uses") == uses);

    private static YamlSequenceNode Sequence(YamlMappingNode node, string key)
        => Assert.IsType<YamlSequenceNode>(node.Children[new YamlScalarNode(key)]);

    private static YamlMappingNode Mapping(YamlMappingNode node, string key)
        => Assert.IsType<YamlMappingNode>(node.Children[new YamlScalarNode(key)]);

    private static string? Scalar(YamlMappingNode node, string key)
        => node.Children.TryGetValue(new YamlScalarNode(key), out var value) && value is YamlScalarNode scalar
            ? scalar.Value
            : null;

    private static string RepoPath(params string[] path)
        => Path.Combine([RepoRoot.Path, .. path]);

    private static YamlMappingNode LoadYaml(params string[] path)
    {
        var yaml = new YamlStream();
        using var reader = new StringReader(File.ReadAllText(RepoPath(path)));
        yaml.Load(reader);

        return Assert.IsType<YamlMappingNode>(yaml.Documents[0].RootNode);
    }

    private static string ShellQuote(string value)
        => $"'{value.Replace("'", "'\"'\"'", StringComparison.Ordinal)}'";

    private async Task<ProcessResult> RunGitAsync(string workingDirectory, params string[] arguments)
    {
        var result = await ProcessRunner.RunAsync(output, "git", arguments, workingDirectory);
        Assert.Equal(0, result.ExitCode);
        return result;
    }

    private static string[] ReadJsonOutput(string outputs, string name)
    {
        var startMarker = $"{name}<<EOF\n";
        var start = outputs.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Output '{name}' was not found.");
        start += startMarker.Length;

        var end = outputs.IndexOf("\nEOF", start, StringComparison.Ordinal);
        Assert.True(end >= 0, $"Output '{name}' was not terminated.");

        return JsonSerializer.Deserialize<string[]>(outputs[start..end])!;
    }

}
