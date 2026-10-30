// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.TestUtilities;
using Xunit;

namespace Infrastructure.Tests;

/// <summary>
/// Tests for .github/workflows/lint-handwritten-workflows.sh, the file selection shared by
/// the blocking ci.yml actionlint gate and the update-actionlint.yml candidate check.
/// </summary>
[SkipOnPlatform(TestPlatforms.Windows, "The script runs only on Linux runners and the fake actionlint relies on Unix PATH lookup.")]
public sealed class LintHandwrittenWorkflowsScriptTests(ITestOutputHelper output) : IDisposable
{
    private static readonly string s_scriptPath = Path.Combine(
        RepoRoot.Path,
        ".github",
        "workflows",
        "lint-handwritten-workflows.sh");

    private readonly TemporaryWorkspace _workspace = TemporaryWorkspace.Create(output);

    [Fact]
    [RequiresTools(["bash"])]
    public async Task LintsOnlyTopLevelHandwrittenWorkflows()
    {
        CreateFiles(
            ".github/workflows/ci.yml",
            ".github/workflows/release.yaml",
            ".github/workflows/agent.lock.yml",
            ".github/workflows/agent.md",
            ".github/workflows/agentics-maintenance-microsoft-aspire.dev.yml",
            ".github/workflows/helper.js",
            ".github/workflows/nested/inner.yml",
            ".github/actions/local/action.yml");

        var result = await RunScriptAsync(actionlintExitCode: 0);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(
            [
                "-shellcheck=",
                "-pyflakes=",
                ".github/workflows/ci.yml",
                ".github/workflows/release.yaml",
            ],
            await ReadActionlintArgumentsAsync());
    }

    [Fact]
    [RequiresTools(["bash"])]
    public async Task PropagatesActionlintFailure()
    {
        CreateFiles(".github/workflows/ci.yml");

        var result = await RunScriptAsync(actionlintExitCode: 1);

        Assert.Equal(1, result.ExitCode);
    }

    [Fact]
    [RequiresTools(["bash"])]
    public async Task FailsWithoutRunningActionlintWhenNoWorkflowsMatch()
    {
        CreateFiles(".github/workflows/agent.lock.yml");

        var result = await RunScriptAsync(actionlintExitCode: 0);

        Assert.Equal(1, result.ExitCode);
        Assert.False(File.Exists(ArgumentsPath));
    }

    private string ArgumentsPath => Path.Combine(_workspace.Path, "actionlint-arguments.txt");

    private void CreateFiles(params string[] relativePaths)
    {
        foreach (var relativePath in relativePaths)
        {
            var path = Path.Combine(_workspace.Path, "repo", relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "name: test\n");
        }
    }

    private async Task<string[]> ReadActionlintArgumentsAsync() => await File.ReadAllLinesAsync(ArgumentsPath);

    private async Task<ProcessResult> RunScriptAsync(int actionlintExitCode)
    {
        // The fake actionlint records its arguments so the test can assert the exact
        // file set the script selects, without depending on a real actionlint install.
        var binDirectory = Directory.CreateDirectory(Path.Combine(_workspace.Path, "bin")).FullName;
        var fakeActionlint = Path.Combine(binDirectory, "actionlint");
        await File.WriteAllTextAsync(
            fakeActionlint,
            """
            #!/usr/bin/env bash
            printf '%s\n' "$@" > "$FAKE_ACTIONLINT_ARGUMENTS"
            exit "$FAKE_ACTIONLINT_EXIT_CODE"
            """);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(fakeActionlint, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return await ProcessRunner.RunAsync(
            output,
            "bash",
            [s_scriptPath],
            Path.Combine(_workspace.Path, "repo"),
            new Dictionary<string, string>
            {
                ["PATH"] = $"{binDirectory}{Path.PathSeparator}{Environment.GetEnvironmentVariable("PATH")}",
                ["FAKE_ACTIONLINT_ARGUMENTS"] = ArgumentsPath,
                ["FAKE_ACTIONLINT_EXIT_CODE"] = actionlintExitCode.ToString(),
            });
    }

    public void Dispose() => _workspace.Dispose();
}
