// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Text.RegularExpressions;
using Aspire.TestUtilities;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace Infrastructure.Tests;

/// <summary>
/// Guards the always-on PR merge gate that keeps the VS Code extension changelog check red until
/// generated placeholder release notes are replaced with finalized content.
/// </summary>
public sealed class ExtensionChangelogFinalizedWorkflowTests(ITestOutputHelper output) : IDisposable
{
    private const string WorkflowRelativePath = ".github/workflows/extension-changelog-finalized.yml";
    private const string ScriptRelativePath = ".github/workflows/check-extension-changelog-finalized.sh";

    private readonly TemporaryWorkspace _workspace = TemporaryWorkspace.Create(output);

    public void Dispose() => _workspace.Dispose();

    [Fact]
    public void WorkflowRunsOnEveryPullRequestWithLeastPrivilegeAndStableCheckName()
    {
        var workflowText = ReadWorkflowText();
        var trigger = GetTopLevelSection(workflowText, "on");

        Assert.Equal(
            "  pull_request:\n",
            trigger.ReplaceLineEndings("\n"));

        var workflow = LoadWorkflow();
        var root = (YamlMappingNode)workflow.Documents[0].RootNode;
        var permissions = (YamlMappingNode)root.Children[new YamlScalarNode("permissions")];

        Assert.Collection(
            permissions.Children,
            entry =>
            {
                Assert.Equal("contents", ((YamlScalarNode)entry.Key).Value);
                Assert.Equal("read", ((YamlScalarNode)entry.Value).Value);
            });

        var jobs = (YamlMappingNode)root.Children[new YamlScalarNode("jobs")];
        var gateJob = Assert.IsType<YamlMappingNode>(Assert.Single(jobs.Children).Value);

        Assert.Equal("Extension changelog finalized", Scalar(gateJob, "name"));
        Assert.Contains("strict/up-to-date semantics", workflowText, StringComparison.Ordinal);

        var steps = ((YamlSequenceNode)gateJob.Children[new YamlScalarNode("steps")]).Cast<YamlMappingNode>();
        var checkoutStep = Assert.Single(steps, step => Scalar(step, "uses")?.Contains("actions/checkout", StringComparison.Ordinal) == true);
        var checkoutWith = Assert.IsType<YamlMappingNode>(checkoutStep.Children[new YamlScalarNode("with")]);
        Assert.Equal("${{ github.event.pull_request.head.sha }}", Scalar(checkoutWith, "ref"));
        Assert.Equal("1", Scalar(checkoutWith, "fetch-depth"));
        Assert.Equal("false", Scalar(checkoutWith, "persist-credentials"));

        var preloadBaseHistoryStep = Assert.Single(
            steps,
            step => Scalar(step, "name") == "Preload trusted base history for release-branch stale range checks");
        Assert.Equal(
            "${{ startsWith(github.head_ref, 'extension-release/') && github.base_ref == 'main' }}",
            Scalar(preloadBaseHistoryStep, "if"));
        var preloadEnv = Assert.IsType<YamlMappingNode>(preloadBaseHistoryStep.Children[new YamlScalarNode("env")]);
        Assert.Equal("${{ github.base_ref }}", Scalar(preloadEnv, "PR_BASE_REF"));
        Assert.Equal("${{ github.event.pull_request.base.sha }}", Scalar(preloadEnv, "PR_BASE_SHA"));
        Assert.Equal("${{ github.token }}", Scalar(preloadEnv, "GITHUB_TOKEN"));
        var preloadRun = Scalar(preloadBaseHistoryStep, "run");
        Assert.NotNull(preloadRun);
        Assert.Contains("http.extraheader=\"AUTHORIZATION: bearer ${GITHUB_TOKEN}\"", preloadRun, StringComparison.Ordinal);
        Assert.Contains("fetch --no-tags --unshallow origin \"${PR_BASE_REF}:refs/remotes/origin/${PR_BASE_REF}\"", preloadRun, StringComparison.Ordinal);
        Assert.Contains("fetch --no-tags origin \"${PR_BASE_REF}:refs/remotes/origin/${PR_BASE_REF}\"", preloadRun, StringComparison.Ordinal);
        Assert.DoesNotContain("--deepen=", preloadRun, StringComparison.Ordinal);
        Assert.DoesNotContain("${from_sha}", preloadRun, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("${to_sha}", preloadRun, StringComparison.OrdinalIgnoreCase);

        var verifyStep = Assert.Single(
            steps,
            step => Scalar(step, "run")?.Contains("bash .github/workflows/check-extension-changelog-finalized.sh", StringComparison.Ordinal) == true);
        var verifyEnv = Assert.IsType<YamlMappingNode>(verifyStep.Children[new YamlScalarNode("env")]);
        Assert.Equal("${{ github.head_ref }}", Scalar(verifyEnv, "PR_HEAD_REF"));
        Assert.Equal("${{ github.base_ref }}", Scalar(verifyEnv, "PR_BASE_REF"));
        Assert.Equal("${{ github.event.pull_request.base.sha }}", Scalar(verifyEnv, "PR_BASE_SHA"));

        Assert.Contains(
            steps,
            step => Scalar(step, "run")?.Contains("bash .github/workflows/check-extension-changelog-finalized.sh", StringComparison.Ordinal) == true);
    }

    [Theory]
    [InlineData("<!-- aspire-ext-changelog from=123 to=456 base=1.2.3 -->", "pending release-notes marker", 3)]
    [InlineData("_Release notes are being generated automatically and will replace this placeholder shortly._", "autogenerated placeholder prose", 3)]
    [RequiresTools(["bash"])]
    public async Task PlaceholderContentFailsWithActionableAnnotation(string placeholder, string expectedReason, int expectedLine)
    {
        await WriteChangelogAsync($"""
            # Aspire VS Code Extension Changelog

            {placeholder}
            """);

        var result = await RunGateScriptAsync();

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(
            $"::error file=extension/CHANGELOG.md,line={expectedLine},title=Finalize VS Code extension changelog::",
            result.Output,
            StringComparison.Ordinal);
        Assert.Contains(expectedReason, result.Output, StringComparison.Ordinal);
        Assert.Contains(
            "Replace the placeholder with finalized release notes before merging.",
            result.Output,
            StringComparison.Ordinal);
    }

    [Fact]
    [RequiresTools(["bash"])]
    public async Task FinalizedChangelogPasses()
    {
        await WriteChangelogAsync("""
            # Aspire VS Code Extension Changelog

            ## v1.99.0

            ### Features

            - Ship finalized extension release notes.
            """);

        var result = await RunGateScriptAsync();

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("extension/CHANGELOG.md is finalized.\n", result.Output.ReplaceLineEndings("\n"));
    }

    [Fact]
    [RequiresTools(["bash", "git"])]
    public async Task ReleaseBranchWithFinalizedMarkerAndNonExtensionBaseAdvancePasses()
    {
        var repository = await CreateReleaseBranchRepositoryAsync(
            currentSectionBodyFactory: (fromSha, toSha) => $$"""
                <!-- aspire-ext-changelog-finalized from={{fromSha}} to={{toSha}} base=1.98.0 -->
                ### Fixes

                - Ship finalized extension release notes.
                """,
            addExtensionChangeOnMain: false);

        var result = await RunGateScriptAsync(repository.ReleaseBranchEnvironment);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("extension/CHANGELOG.md is finalized.\n", result.Output.ReplaceLineEndings("\n"));
    }

    [Fact]
    [RequiresTools(["bash", "git"])]
    public async Task ReleaseBranchAllowsSyntacticallyValidButMissingFromSha()
    {
        const string missingFromSha = "1111111111111111111111111111111111111111";

        var repository = await CreateReleaseBranchRepositoryAsync(
            currentSectionBodyFactory: (_, toSha) => $$"""
                <!-- aspire-ext-changelog-finalized from={{missingFromSha}} to={{toSha}} base=1.98.0 -->
                ### Fixes

                - Ship finalized extension release notes.
                """,
            addExtensionChangeOnMain: false);

        var result = await RunGateScriptAsync(repository.ReleaseBranchEnvironment);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("extension/CHANGELOG.md is finalized.\n", result.Output.ReplaceLineEndings("\n"));
    }

    [Fact]
    [RequiresTools(["bash", "git"])]
    public async Task ReleaseBranchIgnoresHistoricalFinalizedMarkersOutsideTheCurrentSection()
    {
        var repository = await CreateReleaseBranchRepositoryAsync(
            currentSectionBodyFactory: (fromSha, toSha) => $$"""
                <!-- aspire-ext-changelog-finalized from={{fromSha}} to={{toSha}} base=1.98.0 -->
                ### Fixes

                - Ship finalized extension release notes.
                """,
            addExtensionChangeOnMain: false,
            olderSectionBodyFactory: (fromSha, toSha) => $$"""
                <!-- aspire-ext-changelog-finalized from={{fromSha}} to={{toSha}} base=1.97.0 -->
                ### Fixes

                - Previous finalized release note.
                """);

        var result = await RunGateScriptAsync(repository.ReleaseBranchEnvironment);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("extension/CHANGELOG.md is finalized.\n", result.Output.ReplaceLineEndings("\n"));
    }

    [Fact]
    [RequiresTools(["bash", "git"])]
    public async Task ReleaseBranchWithStaleExtensionBaseAdvanceFails()
    {
        var repository = await CreateReleaseBranchRepositoryAsync(
            currentSectionBodyFactory: (fromSha, toSha) => $$"""
                <!-- aspire-ext-changelog-finalized from={{fromSha}} to={{toSha}} base=1.98.0 -->
                ### Fixes

                - Ship finalized extension release notes.
                """,
            addExtensionChangeOnMain: true);

        var result = await RunGateScriptAsync(repository.ReleaseBranchEnvironment);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Finalized changelog range ends at", result.Output, StringComparison.Ordinal);
        Assert.Contains("Restore the pending placeholder entry", result.Output, StringComparison.Ordinal);
        Assert.Contains("re-add the `vscode-extension-release` label", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    [RequiresTools(["bash", "git"])]
    public async Task ReleaseBranchRequiresExactlyOneFinalizedMarker()
    {
        var repository = await CreateReleaseBranchRepositoryAsync(
            currentSectionBodyFactory: (_, _) => """
                ### Fixes

                - Ship finalized extension release notes.
                """,
            addExtensionChangeOnMain: false);

        var result = await RunGateScriptAsync(repository.ReleaseBranchEnvironment);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Expected exactly one finalized release marker", result.Output, StringComparison.Ordinal);
        Assert.Contains("Restore the pending placeholder entry", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    [RequiresTools(["bash", "git"])]
    public async Task ReleaseBranchCurrentVersionSectionRequiresReleaseNoteBullets()
    {
        var repository = await CreateReleaseBranchRepositoryAsync(
            currentSectionBodyFactory: (fromSha, toSha) => $$"""
                <!-- aspire-ext-changelog-finalized from={{fromSha}} to={{toSha}} base=1.98.0 -->
                """,
            addExtensionChangeOnMain: false);

        var result = await RunGateScriptAsync(repository.ReleaseBranchEnvironment);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("must contain at least one '- ' release-note bullet with non-whitespace content", result.Output, StringComparison.Ordinal);
        Assert.Contains("Restore the pending placeholder entry", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    [RequiresTools(["bash", "git"])]
    public async Task ReleaseBranchRejectsWhitespaceOnlyReleaseNoteBullets()
    {
        var repository = await CreateReleaseBranchRepositoryAsync(
            currentSectionBodyFactory: (fromSha, toSha) => $$"""
                <!-- aspire-ext-changelog-finalized from={{fromSha}} to={{toSha}} base=1.98.0 -->
                {{ "- " }}
                """,
            addExtensionChangeOnMain: false);

        var result = await RunGateScriptAsync(repository.ReleaseBranchEnvironment);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("must contain at least one '- ' release-note bullet with non-whitespace content", result.Output, StringComparison.Ordinal);
        Assert.Contains("Restore the pending placeholder entry", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    [RequiresTools(["bash", "git"])]
    public async Task ReleaseBranchTrustedBasePreloadMakesDeepFinalizedToAvailableInShallowClone()
    {
        var repository = await CreateShallowReleaseBranchCloneAsync(mainCommitsAfterToOnMain: 150);
        var preloadScript = GetWorkflowRunScript("Preload trusted base history for release-branch stale range checks");

        var beforePreload = await RunProcessAsync(
            "git",
            ["cat-file", "-e", $"{repository.ToSha}^{{commit}}"],
            repository.WorkingDirectory);
        Assert.NotEqual(0, beforePreload.ExitCode);

        var preloadResult = await RunInlineBashAsync(preloadScript, repository.WorkingDirectory, repository.ReleaseBranchEnvironment);
        Assert.Equal(0, preloadResult.ExitCode);

        var afterPreload = await RunProcessAsync(
            "git",
            ["cat-file", "-e", $"{repository.ToSha}^{{commit}}"],
            repository.WorkingDirectory);
        Assert.Equal(0, afterPreload.ExitCode);

        var gateResult = await RunGateScriptAsync(repository.WorkingDirectory, repository.ReleaseBranchEnvironment);
        Assert.Equal(0, gateResult.ExitCode);
    }

    private async Task WriteChangelogAsync(string content)
    {
        var changelogPath = Path.Combine(_workspace.Path, "extension", "CHANGELOG.md");
        Directory.CreateDirectory(Path.GetDirectoryName(changelogPath)!);

        await File.WriteAllTextAsync(changelogPath, content.ReplaceLineEndings("\n"));
    }

    private async Task<CommandResult> RunGateScriptAsync(IReadOnlyDictionary<string, string?>? environment = null)
        => await RunGateScriptAsync(_workspace.Path, environment);

    private static async Task<CommandResult> RunGateScriptAsync(string workingDirectory, IReadOnlyDictionary<string, string?>? environment = null)
    {
        var scriptPath = Path.Combine(RepoRoot.Path, ScriptRelativePath);
        Assert.True(File.Exists(scriptPath), $"Expected helper script at '{ScriptRelativePath}'.");

        using var process = new Process();
        process.StartInfo.FileName = "bash";
        process.StartInfo.ArgumentList.Add(scriptPath);
        process.StartInfo.WorkingDirectory = workingDirectory;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.UseShellExecute = false;
        if (environment is not null)
        {
            foreach (var (key, value) in environment)
            {
                process.StartInfo.Environment[key] = value ?? string.Empty;
            }
        }

        process.Start();

        // Read both streams concurrently to avoid deadlock when the shell emits an annotation.
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await process.WaitForExitAsync(timeout.Token);

        return new CommandResult(process.ExitCode, await stdoutTask + await stderrTask);
    }

    private async Task<ReleaseBranchRepository> CreateReleaseBranchRepositoryAsync(
        Func<string, string, string> currentSectionBodyFactory,
        bool addExtensionChangeOnMain,
        Func<string, string, string>? olderSectionBodyFactory = null)
    {
        await InitializeGitRepositoryAsync(_workspace.Path);

        await WritePackageJsonAsync(_workspace.Path, "1.98.0");
        await WriteChangelogAsync(_workspace.Path, """
            # Aspire VS Code Extension Changelog

            ## v1.98.0

            """);
        await WriteWorkspaceFileAsync(_workspace.Path, Path.Combine("extension", "src", "baseline.txt"), "baseline");
        await CommitAllAsync(_workspace.Path, "Create baseline changelog");
        var fromSha = await GetHeadShaAsync();

        await WriteChangelogAsync(
            _workspace.Path,
            $$"""
            # Aspire VS Code Extension Changelog

            ## v1.98.0

            {{olderSectionBodyFactory?.Invoke(fromSha, fromSha) ?? """
            ### Fixes

            - Previous release note.
            """}}
            """);
        await CommitAllAsync(_workspace.Path, "Finalize prior release changelog");

        await WriteWorkspaceFileAsync(_workspace.Path, Path.Combine("extension", "src", "feature.txt"), "feature-one");
        await CommitAllAsync(_workspace.Path, "Add extension feature");
        var toSha = await GetHeadShaAsync();

        await RunGitAsync(_workspace.Path, "checkout", "-b", "extension-release/v1.99.0");

        await WritePackageJsonAsync(_workspace.Path, "1.99.0");
        await WriteChangelogAsync(_workspace.Path, $$"""
            # Aspire VS Code Extension Changelog

            ## v1.99.0

            {{currentSectionBodyFactory(fromSha, toSha)}}
            ## v1.98.0

            {{olderSectionBodyFactory?.Invoke(fromSha, fromSha) ?? """
            ### Fixes

            - Previous release note.
            """}}
            """);
        await CommitAllAsync(_workspace.Path, "Prepare release branch changelog");

        await RunGitAsync(_workspace.Path, "checkout", "main");

        if (addExtensionChangeOnMain)
        {
            await WriteWorkspaceFileAsync(_workspace.Path, Path.Combine("extension", "src", "late-main.txt"), "late extension change");
        }
        else
        {
            await WriteWorkspaceFileAsync(_workspace.Path, Path.Combine("docs", "late-main.md"), "non-extension main change");
        }

        await CommitAllAsync(_workspace.Path, "Advance main");
        var baseSha = await GetHeadShaAsync(_workspace.Path);

        await RunGitAsync(_workspace.Path, "checkout", "extension-release/v1.99.0");

        return new ReleaseBranchRepository(
            _workspace.Path,
            new Dictionary<string, string?>
            {
                ["PR_HEAD_REF"] = "extension-release/v1.99.0",
                ["PR_BASE_REF"] = "main",
                ["PR_BASE_SHA"] = baseSha,
            },
            fromSha,
            toSha,
            baseSha);
    }

    private async Task<ReleaseBranchRepository> CreateShallowReleaseBranchCloneAsync(int mainCommitsAfterToOnMain)
    {
        var seedRepositoryPath = Path.Combine(_workspace.Path, "seed");
        var originRepositoryPath = Path.Combine(_workspace.Path, "origin.git");
        var clonePath = Path.Combine(_workspace.Path, "release-clone");

        Directory.CreateDirectory(seedRepositoryPath);
        await InitializeGitRepositoryAsync(seedRepositoryPath);

        await WritePackageJsonAsync(seedRepositoryPath, "1.98.0");
        await WriteChangelogAsync(seedRepositoryPath, """
            # Aspire VS Code Extension Changelog

            ## v1.98.0

            ### Fixes

            - Previous release note.
            """);
        await WriteWorkspaceFileAsync(seedRepositoryPath, Path.Combine("extension", "src", "baseline.txt"), "baseline");
        await CommitAllAsync(seedRepositoryPath, "Create baseline changelog");
        var fromSha = await GetHeadShaAsync(seedRepositoryPath);

        await WriteWorkspaceFileAsync(seedRepositoryPath, Path.Combine("extension", "src", "feature.txt"), "feature-one");
        await CommitAllAsync(seedRepositoryPath, "Add extension feature");
        var toSha = await GetHeadShaAsync(seedRepositoryPath);

        await RunGitAsync(seedRepositoryPath, "checkout", "-b", "extension-release/v1.99.0");
        await WritePackageJsonAsync(seedRepositoryPath, "1.99.0");
        await WriteChangelogAsync(seedRepositoryPath, $$"""
            # Aspire VS Code Extension Changelog

            ## v1.99.0

            <!-- aspire-ext-changelog-finalized from={{fromSha}} to={{toSha}} base=1.98.0 -->
            ### Fixes

            - Ship finalized extension release notes.

            ## v1.98.0

            ### Fixes

            - Previous release note.
            """);
        await CommitAllAsync(seedRepositoryPath, "Prepare release branch changelog");

        await RunGitAsync(seedRepositoryPath, "checkout", "main");

        for (var index = 1; index <= mainCommitsAfterToOnMain; index++)
        {
            await WriteWorkspaceFileAsync(
                seedRepositoryPath,
                Path.Combine("docs", "late-main.md"),
                $"non-extension main change {index}{Environment.NewLine}");
            await CommitAllAsync(seedRepositoryPath, $"Advance main {index:D3}");
        }

        var baseSha = await GetHeadShaAsync(seedRepositoryPath);

        await RunProcessAsync("git", ["init", "--bare", originRepositoryPath], _workspace.Path);
        await RunGitAsync(seedRepositoryPath, "remote", "add", "origin", originRepositoryPath);
        await RunGitAsync(seedRepositoryPath, "push", "origin", "main", "extension-release/v1.99.0");

        var originUri = new Uri(originRepositoryPath).AbsoluteUri;
        var cloneResult = await RunProcessAsync(
            "git",
            ["clone", "--branch", "extension-release/v1.99.0", "--depth", "1", originUri, clonePath],
            _workspace.Path);
        Assert.True(
            cloneResult.ExitCode == 0,
            $"Failed to create shallow release clone.{Environment.NewLine}{cloneResult.Output}");

        return new ReleaseBranchRepository(
            clonePath,
            new Dictionary<string, string?>
            {
                ["PR_HEAD_REF"] = "extension-release/v1.99.0",
                ["PR_BASE_REF"] = "main",
                ["PR_BASE_SHA"] = baseSha,
                ["GITHUB_TOKEN"] = "test-token",
            },
            fromSha,
            toSha,
            baseSha);
    }

    private async Task InitializeGitRepositoryAsync(string workingDirectory)
    {
        await RunGitAsync(workingDirectory, "init");
        await RunGitAsync(workingDirectory, "checkout", "-b", "main");
        await RunGitAsync(workingDirectory, "config", "user.email", "test@example.com");
        await RunGitAsync(workingDirectory, "config", "user.name", "Test User");
    }

    private static async Task WritePackageJsonAsync(string workingDirectory, string version)
    {
        await WriteWorkspaceFileAsync(
            workingDirectory,
            Path.Combine("extension", "package.json"),
            $$"""
            {
              "name": "aspire-vscode",
              "version": "{{version}}"
            }
            """);
    }

    private static async Task WriteChangelogAsync(string workingDirectory, string content)
        => await WriteWorkspaceFileAsync(workingDirectory, Path.Combine("extension", "CHANGELOG.md"), content);

    private static async Task WriteWorkspaceFileAsync(string workingDirectory, string relativePath, string content)
    {
        var fullPath = Path.Combine(workingDirectory, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllTextAsync(fullPath, content.ReplaceLineEndings("\n"));
    }

    private async Task CommitAllAsync(string workingDirectory, string message)
    {
        await RunGitAsync(workingDirectory, "add", ".");
        await RunGitAsync(workingDirectory, "commit", "-m", message);
    }

    private async Task<string> GetHeadShaAsync()
        => await GetHeadShaAsync(_workspace.Path);

    private async Task<string> GetHeadShaAsync(string workingDirectory)
    {
        var result = await RunProcessAsync("git", ["rev-parse", "HEAD"], workingDirectory);
        Assert.Equal(0, result.ExitCode);
        return result.Output.Trim();
    }

    private async Task RunGitAsync(string workingDirectory, params string[] args)
    {
        var result = await RunProcessAsync("git", args, workingDirectory);
        Assert.True(
            result.ExitCode == 0,
            $"git {string.Join(' ', args)} failed with exit code {result.ExitCode}.{Environment.NewLine}{result.Output}");
    }

    private async Task<CommandResult> RunInlineBashAsync(string script, string workingDirectory, IReadOnlyDictionary<string, string?>? environment = null)
    {
        using var process = new Process();
        process.StartInfo.FileName = "bash";
        process.StartInfo.ArgumentList.Add("-s");
        process.StartInfo.WorkingDirectory = workingDirectory;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.RedirectStandardInput = true;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.UseShellExecute = false;

        if (environment is not null)
        {
            foreach (var (key, value) in environment)
            {
                process.StartInfo.Environment[key] = value ?? string.Empty;
            }
        }

        process.Start();
        await process.StandardInput.WriteAsync(script);
        process.StandardInput.Close();

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await process.WaitForExitAsync(timeout.Token);

        var outputText = await stdoutTask + await stderrTask;
        output.WriteLine(outputText);

        return new CommandResult(process.ExitCode, outputText);
    }

    private async Task<CommandResult> RunProcessAsync(string fileName, IEnumerable<string> args, string workingDirectory)
    {
        using var process = new Process();
        process.StartInfo.FileName = fileName;
        process.StartInfo.WorkingDirectory = workingDirectory;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.UseShellExecute = false;

        foreach (var arg in args)
        {
            process.StartInfo.ArgumentList.Add(arg);
        }

        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await process.WaitForExitAsync(timeout.Token);

        var outputText = await stdoutTask + await stderrTask;
        output.WriteLine(outputText);

        return new CommandResult(process.ExitCode, outputText);
    }

    private static string GetWorkflowRunScript(string stepName)
    {
        var workflow = LoadWorkflow();
        var root = (YamlMappingNode)workflow.Documents[0].RootNode;
        var jobs = (YamlMappingNode)root.Children[new YamlScalarNode("jobs")];

        foreach (var jobEntry in jobs.Children)
        {
            if (jobEntry.Value is not YamlMappingNode job
                || !job.Children.TryGetValue(new YamlScalarNode("steps"), out var stepsNode)
                || stepsNode is not YamlSequenceNode steps)
            {
                continue;
            }

            foreach (var step in steps.Children.OfType<YamlMappingNode>())
            {
                if (Scalar(step, "name") == stepName)
                {
                    var run = Scalar(step, "run");
                    Assert.False(string.IsNullOrEmpty(run), $"Expected step '{stepName}' to define a run script.");
                    return run!;
                }
            }
        }

        Assert.Fail($"Could not find workflow step '{stepName}'.");
        return null!;
    }

    private static YamlStream LoadWorkflow()
    {
        var yaml = new YamlStream();
        using var reader = new StringReader(ReadWorkflowText());
        yaml.Load(reader);

        return yaml;
    }

    private static string ReadWorkflowText()
    {
        var workflowPath = Path.Combine(RepoRoot.Path, WorkflowRelativePath);
        Assert.True(File.Exists(workflowPath), $"Expected workflow file at '{WorkflowRelativePath}'.");

        return File.ReadAllText(workflowPath);
    }

    private static string GetTopLevelSection(string text, string key)
    {
        var match = Regex.Match(
            text,
            $"(?m)^{Regex.Escape(key)}:\\r?\\n(?<value>(?:^[ ].*\\r?\\n)*)",
            RegexOptions.CultureInvariant);

        Assert.True(match.Success, $"Could not find top-level workflow section '{key}'.");
        return match.Groups["value"].Value;
    }

    private static string? Scalar(YamlMappingNode node, string key)
        => node.Children.TryGetValue(new YamlScalarNode(key), out var value) && value is YamlScalarNode scalar
            ? scalar.Value
            : null;

    private sealed record ReleaseBranchRepository(
        string WorkingDirectory,
        IReadOnlyDictionary<string, string?> ReleaseBranchEnvironment,
        string FromSha,
        string ToSha,
        string BaseSha);
    private sealed record CommandResult(int ExitCode, string Output);
}
