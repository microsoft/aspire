// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Templates.Tests;
using Aspire.TestUtilities;
using Xunit;

namespace Infrastructure.Tests;

/// <summary>
/// Tests for .github/workflows/cli-e2e-recording-comment.sh.
/// </summary>
[SkipOnPlatform(TestPlatforms.Windows | TestPlatforms.OSX, "CLI E2E recording comment script runs on Linux in CI.")]
public sealed class CliE2ERecordingCommentWorkflowTests : IDisposable
{
    private readonly TestTempDirectory _tempDirectory = new();
    private readonly string _scriptPath;
    private readonly ITestOutputHelper _output;

    public CliE2ERecordingCommentWorkflowTests(ITestOutputHelper output)
    {
        _output = output;

        _scriptPath = Path.Combine(TestUtils.RepoRoot, ".github", "workflows", "cli-e2e-recording-comment.sh");
    }

    public void Dispose() => _tempDirectory.Dispose();

    [Fact]
    [RequiresTools(["jq", "yq"])]
    public async Task ScriptRendersFailedTheoryRecording()
    {
        var workspace = CreateWorkflowWorkspace();
        CreateRecordingArtifact(
            workspace,
            "type-script-codegen",
            "RestoreGeneratesSdkFiles_WithConfiguredToolchain",
            new TestTrxCase(
                CanonicalTestName: "Aspire.Cli.EndToEnd.Tests.TypeScriptCodegenValidationTests.RestoreGeneratesSdkFiles_WithConfiguredToolchain",
                DisplayName: "Aspire.Cli.EndToEnd.Tests.TypeScriptCodegenValidationTests.RestoreGeneratesSdkFiles_WithConfiguredToolchain(toolchain: \"yarn\")",
                Outcome: "Failed"),
            new TestTrxCase(
                CanonicalTestName: "Aspire.Cli.EndToEnd.Tests.TypeScriptCodegenValidationTests.RestoreGeneratesSdkFiles_WithConfiguredToolchain",
                DisplayName: "Aspire.Cli.EndToEnd.Tests.TypeScriptCodegenValidationTests.RestoreGeneratesSdkFiles_WithConfiguredToolchain(toolchain: \"pnpm\")",
                Outcome: "Failed"),
            new TestTrxCase(
                CanonicalTestName: "Aspire.Cli.EndToEnd.Tests.TypeScriptCodegenValidationTests.RestoreGeneratesSdkFiles_WithConfiguredToolchain",
                DisplayName: "Aspire.Cli.EndToEnd.Tests.TypeScriptCodegenValidationTests.RestoreGeneratesSdkFiles_WithConfiguredToolchain(toolchain: \"bun\")",
                Outcome: "Passed"));

        await RunParseStepAsync(workspace);
        var comment = await RunPostCommentStepAsync(workspace);

        AssertStepOutput(workspace, "has_outcomes", "true");
        AssertStepOutput(workspace, "parse_warning", "false");
        Assert.Contains("❌ **CLI E2E Test Recordings** — 1 test(s) failed, 1 recordings uploaded", comment);
        Assert.Contains("### Failed Tests", comment);
        Assert.Contains("- ❌ **RestoreGeneratesSdkFiles_WithConfiguredToolchain**", comment);
        Assert.Contains("| ❌ | RestoreGeneratesSdkFiles_WithConfiguredToolchain |", comment);
        Assert.DoesNotContain("❔", comment);
        AssertCommentMarkdownStartsAtColumnZero(comment);
    }

    [Fact]
    [RequiresTools(["jq", "yq"])]
    public async Task ScriptRendersPassedHelperNamedRecording()
    {
        var workspace = CreateWorkflowWorkspace();
        CreateRecordingArtifact(
            workspace,
            "otel-logs",
            "OtelLogsReturnsStructuredLogsFromStarterAppCore",
            new TestTrxCase(
                CanonicalTestName: "Aspire.Cli.EndToEnd.Tests.OtelLogsTests.OtelLogsReturnsStructuredLogsFromStarterApp",
                DisplayName: "Aspire.Cli.EndToEnd.Tests.OtelLogsTests.OtelLogsReturnsStructuredLogsFromStarterApp",
                Outcome: "Passed"),
            new TestTrxCase(
                CanonicalTestName: "Aspire.Cli.EndToEnd.Tests.OtelLogsTests.OtelLogsReturnsStructuredLogsFromStarterAppIsolated",
                DisplayName: "Aspire.Cli.EndToEnd.Tests.OtelLogsTests.OtelLogsReturnsStructuredLogsFromStarterAppIsolated",
                Outcome: "Passed"));

        await RunParseStepAsync(workspace);
        var comment = await RunPostCommentStepAsync(workspace);

        AssertStepOutput(workspace, "has_outcomes", "true");
        AssertStepOutput(workspace, "parse_warning", "false");
        Assert.Contains("🎬 **CLI E2E Test Recordings** — 1 recordings uploaded", comment);
        Assert.Contains("| ✅ | OtelLogsReturnsStructuredLogsFromStarterAppCore |", comment);
        Assert.DoesNotContain("❔", comment);
        AssertCommentMarkdownStartsAtColumnZero(comment);
    }

    [Fact]
    [RequiresTools(["jq", "yq"])]
    public async Task ScriptRendersWarningWhenTrxFilesProduceNoOutcomes()
    {
        var workspace = CreateWorkflowWorkspace();
        var artifactDirectory = Directory.CreateDirectory(Path.Combine(workspace, "recordings", "extracted_empty"));
        var castPath = Path.Combine(artifactDirectory.FullName, "Empty.cast");
        File.WriteAllText(castPath, string.Empty);
        File.Copy(castPath, Path.Combine(workspace, "cast_files", "Empty.cast"));

        var trxPath = Path.Combine(artifactDirectory.FullName, "empty.trx");
        File.WriteAllText(
            trxPath,
            """
            <?xml version="1.0" encoding="utf-8"?>
            <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <Results />
            </TestRun>
            """);
        File.Copy(trxPath, Path.Combine(workspace, "trx_files", "empty.trx"));

        await RunParseStepAsync(workspace);
        var comment = await RunPostCommentStepAsync(workspace);

        AssertStepOutput(workspace, "has_outcomes", "true");
        AssertStepOutput(workspace, "parse_warning", "true");
        Assert.Contains("Could not determine pass/fail status", comment);
        Assert.Contains("| ❔ | Empty |", comment);
        AssertCommentMarkdownStartsAtColumnZero(comment);
    }

    [Fact]
    [RequiresTools(["jq"])]
    public async Task ScriptReplacesExistingGithubActionsBotComment()
    {
        var workspace = CreateWorkflowWorkspace();
        CreateRecordingArtifact(
            workspace,
            "existing-comment",
            "ExistingComment",
            new TestTrxCase("Infrastructure.Tests.ExistingComment", "Infrastructure.Tests.ExistingComment", "Passed"));

        var log = await RunPostCommentStepWithMockGitHubAsync(workspace);

        Assert.Contains("delete /repos/microsoft/aspire/issues/comments/987654", log);
        Assert.Contains("comment 16472", log);
        Assert.Contains("upload", log);
    }

    [Fact]
    [RequiresTools(["jq"])]
    public async Task ScriptSkipsStaleWorkflowRunComment()
    {
        var workspace = CreateWorkflowWorkspace();
        var outputPath = Path.Combine(workspace, "comment.md");
        CreateRecordingArtifact(
            workspace,
            "stale-run",
            "StaleRun",
            new TestTrxCase("Infrastructure.Tests.StaleRun", "Infrastructure.Tests.StaleRun", "Passed"));

        var log = await RunPostCommentStepWithMockGitHubAsync(workspace, currentHeadShas: ["fedcba9876543210"], outputPath: outputPath);

        Assert.Contains("pr view", log);
        Assert.DoesNotContain("upload", log);
        Assert.DoesNotContain("comment 16472", log);
        Assert.False(File.Exists(outputPath));
    }

    [Fact]
    [RequiresTools(["jq"])]
    public async Task ScriptSkipsCommentUpdateWhenWorkflowRunBecomesStaleAfterUpload()
    {
        var workspace = CreateWorkflowWorkspace();
        CreateRecordingArtifact(
            workspace,
            "stale-after-upload",
            "StaleAfterUpload",
            new TestTrxCase("Infrastructure.Tests.StaleAfterUpload", "Infrastructure.Tests.StaleAfterUpload", "Passed"));

        var log = await RunPostCommentStepWithMockGitHubAsync(workspace, currentHeadShas: ["0123456789abcdef", "fedcba9876543210"]);

        Assert.Equal(2, CountOccurrences(log, "gh pr view"));
        Assert.Contains("upload", log);
        Assert.DoesNotContain("delete /repos/microsoft/aspire/issues/comments/987654", log);
        Assert.DoesNotContain("comment 16472", log);
    }

    private string CreateWorkflowWorkspace()
    {
        var workspace = Path.Combine(_tempDirectory.Path, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(workspace, "recordings"));
        Directory.CreateDirectory(Path.Combine(workspace, "cast_files"));
        Directory.CreateDirectory(Path.Combine(workspace, "trx_files"));
        return workspace;
    }

    private static void CreateRecordingArtifact(string workspace, string artifactName, string recordingName, params TestTrxCase[] testCases)
    {
        var artifactDirectory = Directory.CreateDirectory(Path.Combine(workspace, "recordings", $"extracted_{artifactName}"));

        var castFileName = $"{recordingName}.cast";
        var artifactCastPath = Path.Combine(artifactDirectory.FullName, castFileName);
        File.WriteAllText(artifactCastPath, string.Empty);
        File.Copy(artifactCastPath, Path.Combine(workspace, "cast_files", castFileName));

        var trxFileName = $"{artifactName}.trx";
        var artifactTrxPath = Path.Combine(artifactDirectory.FullName, trxFileName);
        TestTrxBuilder.CreateTrxFile(artifactTrxPath, testCases);
        File.Copy(artifactTrxPath, Path.Combine(workspace, "trx_files", trxFileName));
    }

    private async Task RunParseStepAsync(string workspace)
    {
        await RunScriptAsync(
            workspace,
            "parse",
            new Dictionary<string, string>
            {
                ["GITHUB_OUTPUT"] = Path.Combine(workspace, "github-output.txt")
            });
    }

    private async Task<string> RunPostCommentStepAsync(string workspace)
    {
        var outputPath = Path.Combine(workspace, "comment.md");
        await RunScriptAsync(
            workspace,
            "post-comment",
            new Dictionary<string, string>
            {
                ["ASCIINEMA_BASE_URL"] = "https://example.invalid/a",
                ["COMMENT_OUTPUT_PATH"] = outputPath,
                ["DRY_RUN"] = "true",
                ["GITHUB_REPOSITORY"] = "microsoft/aspire",
                ["HAS_OUTCOMES"] = ReadStepOutput(workspace, "has_outcomes"),
                ["HEAD_SHA"] = "0123456789abcdef",
                ["PARSE_WARNING"] = ReadStepOutput(workspace, "parse_warning"),
                ["PR_NUMBER"] = "16472",
                ["RUN_ID"] = "123456789"
            });

        return await File.ReadAllTextAsync(outputPath);
    }

    private async Task<string> RunPostCommentStepWithMockGitHubAsync(
        string workspace,
        string[]? currentHeadShas = null,
        string? outputPath = null)
    {
        outputPath ??= Path.Combine(workspace, "comment.md");
        var logPath = Path.Combine(workspace, "mock-tools.log");
        var mockToolDirectory = CreateMockToolDirectory(workspace, logPath, currentHeadShas ?? ["0123456789abcdef", "0123456789abcdef"]);

        await RunScriptAsync(
            workspace,
            "post-comment",
            new Dictionary<string, string>
            {
                ["COMMENT_OUTPUT_PATH"] = outputPath,
                ["GITHUB_EVENT_REPO_NAME"] = "aspire",
                ["GITHUB_REPOSITORY"] = "microsoft/aspire",
                ["GITHUB_REPOSITORY_OWNER"] = "microsoft",
                ["HAS_OUTCOMES"] = "false",
                ["HEAD_SHA"] = "0123456789abcdef",
                ["PATH"] = $"{mockToolDirectory}:{Environment.GetEnvironmentVariable("PATH")}",
                ["PR_NUMBER"] = "16472",
                ["RUN_ID"] = "123456789"
            });

        return await File.ReadAllTextAsync(logPath);
    }

    private static string CreateMockToolDirectory(string workspace, string logPath, string[] currentHeadShas)
    {
        var mockToolDirectory = Path.Combine(workspace, "mock-tools");
        Directory.CreateDirectory(mockToolDirectory);
        var headShasPath = Path.Combine(workspace, "mock-head-shas.txt");
        File.WriteAllLines(headShasPath, currentHeadShas);

        TestFileHelpers.WriteExecutable(
            Path.Combine(mockToolDirectory, "gh"),
            $$"""
            #!/usr/bin/env bash
            set -euo pipefail
            LOG={{ScriptCommand.QuoteBashArgument(logPath)}}
            HEAD_SHAS={{ScriptCommand.QuoteBashArgument(headShasPath)}}

            printf 'gh' >> "$LOG"
            printf ' %q' "$@" >> "$LOG"
            printf '\n' >> "$LOG"

            case "${1:-}" in
              pr)
                case "${2:-}" in
                  view)
                    head_sha=$(head -n 1 "$HEAD_SHAS")
                    tail -n +2 "$HEAD_SHAS" > "${HEAD_SHAS}.tmp"
                    mv "${HEAD_SHAS}.tmp" "$HEAD_SHAS"
                    echo "$head_sha"
                    ;;
                  comment)
                    echo "comment ${3:-}" >> "$LOG"
                    ;;
                  *)
                    echo "Unexpected gh pr command: $*" >&2
                    exit 1
                    ;;
                esac
                ;;
              api)
                if [ "${2:-}" = "graphql" ]; then
                  if [[ "$*" != *"github-actions[bot]"* ]]; then
                    echo "Expected comment query to filter github-actions[bot]" >&2
                    exit 1
                  fi
                  echo "987654"
                elif [ "${2:-}" = "--method" ] && [ "${3:-}" = "DELETE" ]; then
                  echo "delete ${@: -1}" >> "$LOG"
                else
                  echo "Unexpected gh api command: $*" >&2
                  exit 1
                fi
                ;;
              *)
                echo "Unexpected gh command: $*" >&2
                exit 1
                ;;
            esac
            """);

        TestFileHelpers.WriteExecutable(
            Path.Combine(mockToolDirectory, "asciinema"),
            $$"""
            #!/usr/bin/env bash
            set -euo pipefail
            echo "upload ${2:-}" >> {{ScriptCommand.QuoteBashArgument(logPath)}}
            echo "https://asciinema.org/a/mockrecording"
            """);

        TestFileHelpers.WriteExecutable(
            Path.Combine(mockToolDirectory, "pip"),
            $$"""
            #!/usr/bin/env bash
            set -euo pipefail
            echo "pip $*" >> {{ScriptCommand.QuoteBashArgument(logPath)}}
            """);

        return mockToolDirectory;
    }

    private static int CountOccurrences(string value, string substring)
    {
        var count = 0;
        var startIndex = 0;
        while (true)
        {
            var matchIndex = value.IndexOf(substring, startIndex, StringComparison.Ordinal);
            if (matchIndex < 0)
            {
                return count;
            }

            count++;
            startIndex = matchIndex + substring.Length;
        }
    }

    private async Task RunScriptAsync(string workspace, string command, Dictionary<string, string> environment)
    {
        using var script = new ScriptCommand(_scriptPath, _output, "cli-e2e-recording-comment")
            .WithWorkingDirectory(workspace)
            .WithTimeout(TimeSpan.FromMinutes(1));

        foreach (var (key, value) in environment)
        {
            script.WithEnvironmentVariable(key, value);
        }

        var result = await script.ExecuteAsync(command);
        result.EnsureSuccessful();
    }

    private static void AssertStepOutput(string workspace, string key, string expectedValue)
    {
        Assert.Equal(expectedValue, ReadStepOutput(workspace, key));
    }

    private static string ReadStepOutput(string workspace, string key)
    {
        var outputPath = Path.Combine(workspace, "github-output.txt");
        Assert.True(File.Exists(outputPath), $"Expected '{outputPath}' to exist.");

        var prefix = $"{key}=";
        var output = File.ReadLines(outputPath).SingleOrDefault(line => line.StartsWith(prefix, StringComparison.Ordinal));
        Assert.NotNull(output);
        return output![prefix.Length..];
    }

    private static void AssertCommentMarkdownStartsAtColumnZero(string comment)
    {
        var blockPrefixes = new[]
        {
            "### ",
            "- ",
            "|",
            "---",
            "<details>",
            "<summary>",
            "</details>",
            "> "
        };

        foreach (var line in comment.Split('\n'))
        {
            var trimmedLine = line.TrimStart(' ');
            if (line.Length - trimmedLine.Length >= 4 && blockPrefixes.Any(prefix => trimmedLine.StartsWith(prefix, StringComparison.Ordinal)))
            {
                Assert.Fail($"Expected markdown block line to start at column 0: '{line}'");
            }
        }

        Assert.Contains("\n<details>\n", comment);
        Assert.Contains("\n| Status | Test | Recording |\n", comment);
        Assert.Contains("\n|--------|------|-----------|\n", comment);
        Assert.Contains("\n---\n", comment);
        Assert.Contains("\n</details>", comment);
    }

}
