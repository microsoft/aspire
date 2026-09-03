// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Text.Json;
using Aspire.TestUtilities;
using Xunit;

namespace Infrastructure.Tests;

public sealed class AnalyzeCiFailureWorkflowTests(ITestOutputHelper output) : IDisposable
{
    private const string ValidationScriptRelativePath = ".github/workflows/analyze-ci-failure-validation.sh";
    private const string HistoryScriptRelativePath = ".github/workflows/analyze-ci-failure-history.sh";
    private const string CandidatesScriptRelativePath = ".github/workflows/analyze-ci-failure-candidates.sh";
    private const string IssueScriptRelativePath = ".github/workflows/analyze-ci-failure-issue.sh";
    private const string PersistenceScriptRelativePath = ".github/workflows/analyze-ci-failure-persistence.sh";
    private const string CommentScriptRelativePath = ".github/workflows/analyze-ci-failure-comment.sh";

    private static readonly string s_sourceWorkflow = ReadWorkflow("analyze-ci-failure.md");
    private static readonly string s_validationScript = File.ReadAllText(
        Path.Combine(RepoRoot.Path, ValidationScriptRelativePath));
    private static readonly string s_candidatesScript = File.ReadAllText(
        Path.Combine(RepoRoot.Path, CandidatesScriptRelativePath));
    private static readonly string s_issueScript = File.ReadAllText(
        Path.Combine(RepoRoot.Path, IssueScriptRelativePath));

    private static readonly string[] s_executableWorkflows =
    [
        s_sourceWorkflow,
        ReadWorkflow("analyze-ci-failure.lock.yml"),
    ];

    private readonly TemporaryWorkspace _workspace = TemporaryWorkspace.Create(output);

    public void Dispose() => _workspace.Dispose();

    [Fact]
    public void RunScopeComesFromAnalyzedRunMetadata()
    {
        ForEachExecutableWorkflow(workflow =>
        {
            Assert.Contains("RUN_EVENT=$(jq -r '.event // \"\"' ci-failure-data/run.json)", workflow, StringComparison.Ordinal);
            Assert.Contains("case \"${RUN_EVENT}:${HEAD_BRANCH}\" in", workflow, StringComparison.Ordinal);
            Assert.Contains("push:main)", workflow, StringComparison.Ordinal);
            Assert.Contains("pull_request:*|pull_request_target:*)", workflow, StringComparison.Ordinal);
            Assert.Contains("RUN_SCOPE=\"main\"", workflow, StringComparison.Ordinal);
            Assert.Contains("RUN_SCOPE=\"pull-request\"", workflow, StringComparison.Ordinal);
            var scopeCase = GetSection(workflow, "case \"${RUN_EVENT}:${HEAD_BRANCH}\" in", "esac");
            Assert.Contains(
                "*)\necho \"::notice::Unsupported run scope: event=${RUN_EVENT}, branch=${HEAD_BRANCH}. Skipping analysis.\"\necho \"has_work=false\" >> \"$GITHUB_OUTPUT\"\nexit 0",
                scopeCase,
                StringComparison.Ordinal);
            Assert.Contains("run_scope: $run_scope", workflow, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void MainRunContextTreatsTriggeringMergeAsNonCausal()
    {
        ForEachExecutableWorkflow(workflow =>
        {
            var checkoutStep = GetSection(
                workflow,
                "- name: Checkout data collection helpers",
                "- name: Collect CI failure data");
            Assert.Contains(CandidatesScriptRelativePath, checkoutStep, StringComparison.Ordinal);
            Assert.Contains("last-successful-main-run.json", workflow, StringComparison.Ordinal);
            Assert.Contains("candidate-merges.json", workflow, StringComparison.Ordinal);
            Assert.Contains(
                "Unable to find the last successful main run. Continuing without a candidate merge range.",
                workflow,
                StringComparison.Ordinal);
            Assert.Contains("candidate-merge-history-status.json", workflow, StringComparison.Ordinal);
            Assert.Contains("Candidate merge history is unavailable.", workflow, StringComparison.Ordinal);
            Assert.Contains("Candidate merge history is incomplete.", workflow, StringComparison.Ordinal);
            Assert.Contains(
                "bash .github/workflows/analyze-ci-failure-history.sh",
                workflow,
                StringComparison.Ordinal);
            Assert.Contains(
                "bash .github/workflows/analyze-ci-failure-candidates.sh",
                workflow,
                StringComparison.Ordinal);
            Assert.Contains(
                "Triggering merge PR (context only, not necessarily causal)",
                workflow,
                StringComparison.Ordinal);
        });
        Assert.Contains(
            "consider the complete candidate merge range since the last successful main run",
            s_sourceWorkflow,
            StringComparison.Ordinal);
        Assert.Contains("RECEIVED_COMMIT_COUNT", s_candidatesScript, StringComparison.Ordinal);
        Assert.Contains("TOTAL_COMMIT_COUNT", s_candidatesScript, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(
        """
        [
          {"number":17,"merged_at":null,"base":{"repo":{"full_name":"microsoft/aspire"},"ref":"main"}},
          {"number":42,"merged_at":"2026-08-31T12:00:00Z","base":{"repo":{"full_name":"microsoft/aspire"},"ref":"main"}}
        ]
        """,
        42)]
    [InlineData(
        """
        [
          {"number":17,"merged_at":null,"base":{"repo":{"full_name":"microsoft/aspire"},"ref":"main"}},
          {"number":18,"merged_at":"2026-08-31T12:00:00Z","base":{"repo":{"full_name":"other/repo"},"ref":"main"}}
        ]
        """,
        null)]
    [RequiresTools(["jq"])]
    public async Task TriggeringMergeSelectorUsesOnlyMergedPrsTargetingMain(
        string associatedPullRequests,
        int? expectedNumber)
    {
        foreach (var workflow in s_executableWorkflows)
        {
            var selector = ExtractTriggeringMergeSelector(workflow);
            var result = await RunJqAsync(selector, associatedPullRequests);

            Assert.Equal(0, result.ExitCode);
            using var selected = JsonDocument.Parse(result.Output);
            if (expectedNumber is null)
            {
                Assert.Empty(selected.RootElement.EnumerateObject());
            }
            else
            {
                Assert.Equal(expectedNumber, selected.RootElement.GetProperty("number").GetInt32());
            }
        }
    }

    [Fact]
    [RequiresTools(["bash", "jq"])]
    public async Task AnalysisValidatorRejectsMismatchedTrustedScope()
    {
        await WriteValidationFixtureAsync(
            """{"run_id":123,"run_scope":"pull-request","verdict":"code-issue","pr":42,"failed_jobs":[],"failed_tests":[],"causes":[]}""",
            """{"run_id":123,"run_scope":"main"}""",
            "[]");

        var result = await RunValidationScriptAsync(Path.Combine(_workspace.Path, "output.json"));

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(
            "::error::Analysis result does not match trusted run context",
            result.Output,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(
        """{"run_id":123,"run_scope":"main","verdict":"main-repository-breakage","pr":42,"failed_jobs":[],"failed_tests":[],"causes":[]}""",
        """{"run_id":123,"run_scope":"main"}""",
        "[]",
        "::error::Main run analysis must not identify a subject PR")]
    [InlineData(
        """{"run_id":123,"run_scope":"pull-request","verdict":"code-issue","pr":{"number":42},"failed_jobs":[{"id":456,"classification":"code-issue"}],"failed_tests":[],"causes":[]}""",
        """{"run_id":123,"run_scope":"pull-request","pr_numbers":"42"}""",
        """[{"id":123,"name":"Tests"}]""",
        "::error::Analysis failed-job IDs do not match the trusted failed jobs")]
    [InlineData(
        """{"run_id":123,"run_scope":"pull-request","verdict":"transient-infra","pr":{"number":42},"failed_jobs":[{"id":123,"classification":"transient-infra"}],"failed_tests":[],"causes":[]}""",
        """{"run_id":123,"run_scope":"pull-request","pr_numbers":"42"}""",
        """[{"id":123,"name":"Tests"}]""",
        "::error::A transient-infra verdict requires every failed job and cause to be an infrastructure failure")]
    [InlineData(
        """{"run_id":123,"run_scope":"pull-request","verdict":"code-issue","pr":{"number":999},"failed_jobs":[{"id":123,"classification":"code-issue"}],"failed_tests":[],"causes":[]}""",
        """{"run_id":123,"run_scope":"pull-request","pr_numbers":"42"}""",
        """[{"id":123,"name":"Tests"}]""",
        "::error::Pull request analysis must identify a trusted subject PR")]
    [InlineData(
        """{"run_id":123,"run_scope":"pull-request","verdict":"code-issue","pr":{},"failed_jobs":[{"id":123,"classification":"code-issue"}],"failed_tests":[],"causes":[]}""",
        """{"run_id":123,"run_scope":"pull-request","pr_numbers":""}""",
        """[{"id":123,"name":"Tests"}]""",
        "::error::Pull request analysis must identify a trusted subject PR")]
    [RequiresTools(["bash", "jq"])]
    public async Task AnalysisValidatorRejectsUntrustedAssociations(
        string analysis,
        string runContext,
        string trustedFailedJobs,
        string expectedError)
    {
        await WriteValidationFixtureAsync(analysis, runContext, trustedFailedJobs);

        var result = await RunValidationScriptAsync(Path.Combine(_workspace.Path, "output.json"));

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(expectedError, result.Output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(
        """{"run_id":123,"run_scope":"pull-request","verdict":"transient-infra","pr":{"number":42},"failed_jobs":[{"id":123,"classification":"transient-infra"}],"causes":["nuget-timeout"]}""")]
    [InlineData(
        """{"run_id":123,"run_scope":"pull-request","verdict":"transient-infra","pr":{"number":42},"failed_jobs":[{"id":123,"classification":"transient-infra"}],"failed_tests":{},"causes":["nuget-timeout"]}""")]
    [InlineData(
        """{"run_id":123,"run_scope":"pull-request","verdict":"transient-infra","pr":{"number":42},"failed_jobs":[{"id":123,"classification":"transient-infra"}],"failed_tests":["not-an-object"],"causes":["nuget-timeout"]}""")]
    [InlineData(
        """{"run_id":123,"run_scope":"pull-request","verdict":"transient-infra","pr":{"number":42},"failed_jobs":[{"id":123,"classification":"transient-infra"}],"failed_tests":[{"name":"Tests.Flaky","job":"Tests","error":"boom","stack_trace":123,"classification":"flaky","reason":"Intermittent"}],"causes":["nuget-timeout"]}""")]
    [InlineData(
        """{"run_id":123,"run_scope":"pull-request","verdict":"transient-infra","pr":{"number":42},"failed_jobs":[{"id":123,"classification":"transient-infra"}],"failed_tests":[{"name":"Tests.Flaky","job":"Tests","error":"boom","stack_trace":"","classification":"maybe","reason":"Intermittent"}],"causes":["nuget-timeout"]}""")]
    [InlineData(
        """{"run_id":123,"run_scope":"pull-request","verdict":"transient-infra","pr":{"number":42},"failed_jobs":[{"id":123,"classification":"transient-infra"}],"failed_tests":[{"name":123,"job":"Tests","error":"boom","stack_trace":"","classification":"flaky","reason":"Intermittent"}],"causes":["nuget-timeout"]}""")]
    [InlineData(
        """{"run_id":123,"run_scope":"pull-request","verdict":"transient-infra","pr":{"number":42},"failed_jobs":[{"id":123,"classification":"transient-infra"}],"failed_tests":[{"name":"Tests.Flaky","job":{},"error":"boom","stack_trace":"","classification":"flaky","reason":"Intermittent"}],"causes":["nuget-timeout"]}""")]
    [InlineData(
        """{"run_id":123,"run_scope":"pull-request","verdict":"transient-infra","pr":{"number":42},"failed_jobs":[{"id":123,"classification":"transient-infra"}],"failed_tests":[{"name":"Tests.Flaky","job":"Tests","error":[],"stack_trace":"","classification":"flaky","reason":"Intermittent"}],"causes":["nuget-timeout"]}""")]
    [InlineData(
        """{"run_id":123,"run_scope":"pull-request","verdict":"transient-infra","pr":{"number":42},"failed_jobs":[{"id":123,"classification":"transient-infra"}],"failed_tests":[{"name":"Tests.Flaky","job":"Tests","error":"boom","stack_trace":"","classification":"flaky","reason":false}],"causes":["nuget-timeout"]}""")]
    [RequiresTools(["bash", "jq"])]
    public async Task AnalysisValidatorRejectsMalformedFailedTests(string analysis)
    {
        await WriteValidationFixtureAsync(
            analysis,
            """{"run_id":123,"run_scope":"pull-request","pr_numbers":"42"}""",
            """[{"id":123,"name":"Tests"}]""",
            "nuget-timeout.json",
            """{"id":"nuget-timeout","type":"infra-failure","title":"NuGet timeout","error_pattern":"Request timed out"}""");

        var result = await RunValidationScriptAsync(Path.Combine(_workspace.Path, "output.json"));

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(
            "::error::Analysis failed_tests must match the safe field schema",
            result.Output,
            StringComparison.Ordinal);
    }

    [Fact]
    [RequiresTools(["bash", "jq"])]
    public async Task AnalysisValidatorRejectsFailedTestsInTransientInfraVerdict()
    {
        await WriteValidationFixtureAsync(
            """{"run_id":123,"run_scope":"pull-request","verdict":"transient-infra","pr":{"number":42},"failed_jobs":[{"id":123,"classification":"transient-infra"}],"failed_tests":[{"name":"Tests.Flaky","job":"Tests","error":"boom","stack_trace":"","classification":"flaky","reason":"Intermittent"}],"causes":["nuget-timeout"]}""",
            """{"run_id":123,"run_scope":"pull-request","pr_numbers":"42"}""",
            """[{"id":123,"name":"Tests"}]""",
            "nuget-timeout.json",
            """{"id":"nuget-timeout","type":"infra-failure","title":"NuGet timeout","error_pattern":"Request timed out"}""");

        var result = await RunValidationScriptAsync(Path.Combine(_workspace.Path, "output.json"));

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(
            "::error::Analysis failed_tests are incompatible with verdict transient-infra",
            result.Output,
            StringComparison.Ordinal);
    }

    [Fact]
    [RequiresTools(["bash", "jq"])]
    public async Task AnalysisValidatorRejectsCodeIssueTestInFlakyVerdict()
    {
        await WriteValidationFixtureAsync(
            """{"run_id":123,"run_scope":"pull-request","verdict":"flaky-test","pr":{"number":42},"failed_jobs":[{"id":123,"classification":"flaky-test"}],"failed_tests":[{"name":"Tests.Deterministic","job":"Tests","error":"boom","stack_trace":"","classification":"code-issue","reason":"Deterministic"}],"causes":["flaky-failure"]}""",
            """{"run_id":123,"run_scope":"pull-request","pr_numbers":"42"}""",
            """[{"id":123,"name":"Tests"}]""",
            "flaky-failure.json",
            """{"id":"flaky-failure","type":"flaky-test","title":"Flaky test","error_pattern":"Tests.Deterministic"}""");

        var result = await RunValidationScriptAsync(Path.Combine(_workspace.Path, "output.json"));

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(
            "::error::Analysis failed_tests are incompatible with verdict flaky-test",
            result.Output,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(
        """{"run_id":123,"run_scope":"pull-request","verdict":"transient-infra","pr":{"number":42},"failed_jobs":[{"id":123,"classification":"transient-infra"}],"failed_tests":[],"causes":["nuget-timeout"]}""",
        """{"run_id":123,"run_scope":"pull-request","pr_numbers":"42"}""",
        "nuget-timeout.json",
        """{"id":"nuget-timeout","type":"infra-failure","title":"NuGet timeout","error_pattern":"Request timed out"}""")]
    [InlineData(
        """{"run_id":123,"run_scope":"pull-request","verdict":"transient-infra","pr":null,"failed_jobs":[{"id":123,"classification":"transient-infra"}],"failed_tests":[],"causes":["nuget-timeout"]}""",
        """{"run_id":123,"run_scope":"pull-request","pr_numbers":"42"}""",
        "nuget-timeout.json",
        """{"id":"nuget-timeout","type":"infra-failure","title":"NuGet timeout","error_pattern":"Request timed out"}""")]
    [InlineData(
        """{"run_id":123,"run_scope":"pull-request","verdict":"transient-infra","pr":null,"failed_jobs":[{"id":123,"classification":"transient-infra"}],"failed_tests":[],"causes":["nuget-timeout"]}""",
        """{"run_id":123,"run_scope":"pull-request","pr_numbers":""}""",
        "nuget-timeout.json",
        """{"id":"nuget-timeout","type":"infra-failure","title":"NuGet timeout","error_pattern":"Request timed out"}""")]
    [InlineData(
        """{"run_id":123,"run_scope":"main","verdict":"main-repository-breakage","pr":null,"failed_jobs":[{"id":123,"classification":"main-repository-breakage"}],"failed_tests":[],"causes":["main-build-break"]}""",
        """{"run_id":123,"run_scope":"main","pr_numbers":""}""",
        "main-build-break.json",
        """{"id":"main-build-break","type":"main-repository-breakage","title":"Main build break","error_pattern":"Compilation failed"}""")]
    [InlineData(
        """{"run_id":123,"run_scope":"pull-request","verdict":"flaky-test","pr":{"number":42},"failed_jobs":[{"id":123,"classification":"flaky-test"}],"failed_tests":[{"name":"Tests.Flaky","job":"Tests","error":"boom","stack_trace":"","classification":"flaky","reason":"Intermittent"}],"causes":["flaky-failure"]}""",
        """{"run_id":123,"run_scope":"pull-request","pr_numbers":"42"}""",
        "flaky-failure.json",
        """{"id":"flaky-failure","type":"flaky-test","title":"Flaky test","error_pattern":"Tests.Flaky"}""")]
    [InlineData(
        """{"run_id":123,"run_scope":"pull-request","verdict":"flaky-test","pr":{"number":42},"failed_jobs":[{"id":123,"classification":"flaky-test"}],"failed_tests":[{"name":"Tests.Flaky","job":"Tests","error":"boom","stack_trace":null,"classification":"flaky","reason":"Intermittent"}],"causes":["flaky-failure"]}""",
        """{"run_id":123,"run_scope":"pull-request","pr_numbers":"42"}""",
        "flaky-failure.json",
        """{"id":"flaky-failure","type":"flaky-test","title":"Flaky test","error_pattern":"Tests.Flaky"}""")]
    [InlineData(
        """{"run_id":123,"run_scope":"pull-request","verdict":"flaky-test","pr":{"number":42},"failed_jobs":[{"id":123,"classification":"flaky-test"}],"failed_tests":[{"name":"Tests.Flaky","job":"Tests","error":"boom","classification":"flaky","reason":"Intermittent"}],"causes":["flaky-failure"]}""",
        """{"run_id":123,"run_scope":"pull-request","pr_numbers":"42"}""",
        "flaky-failure.json",
        """{"id":"flaky-failure","type":"flaky-test","title":"Flaky test","error_pattern":"Tests.Flaky"}""")]
    [RequiresTools(["bash", "jq"])]
    public async Task AnalysisValidatorAcceptsValidResults(
        string analysis,
        string runContext,
        string causeFileName,
        string cause)
    {
        await WriteValidationFixtureAsync(
            analysis,
            runContext,
            """[{"id":123,"name":"Tests"}]""",
            causeFileName,
            cause);

        var result = await RunValidationScriptAsync(Path.Combine(_workspace.Path, "output.json"));

        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData(
        """
        {
          "verdict": "transient-infra",
          "failed_jobs": [
            {
              "id": 123,
              "name": "Forged job name",
              "classification": "transient-infra",
              "reason": "Request timed out"
            }
          ],
          "failed_tests": []
        }
        """)]
    [InlineData(
        """
        {
          "verdict": "transient-infra",
          "failed_jobs": [
            {
              "id": 123,
              "classification": "transient-infra",
              "reason": "Request timed out"
            }
          ],
          "failed_tests": []
        }
        """)]
    [RequiresTools(["bash", "jq"])]
    public async Task CommentRendererUsesTrustedFailedJobNames(string analysis)
    {
        var analysisPath = Path.Combine(_workspace.Path, "analysis.json");
        var trustedJobsPath = Path.Combine(_workspace.Path, "failed-jobs.json");
        await File.WriteAllTextAsync(analysisPath, analysis);
        await File.WriteAllTextAsync(
            trustedJobsPath,
            """[{"id":123,"name":"Build and Test (ubuntu-latest)"}]""");

        var result = await RunBashScriptAsync(
            Path.Combine(RepoRoot.Path, CommentScriptRelativePath),
            [analysisPath, trustedJobsPath, "https://github.com/microsoft/aspire/actions/runs/123"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(
            "- `Build and Test (ubuntu-latest)` — Request timed out (transient-infra)",
            Assert.Single(result.Output.Split('\n'), line => line.StartsWith("- `", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("Forged job name", "- `Tests.Flaky`")]
    [InlineData(
        "Build and Test (ubuntu-latest)",
        "- `Tests.Flaky` in job `Build and Test (ubuntu-latest)`")]
    [RequiresTools(["bash", "jq"])]
    public async Task CommentRendererDisplaysOnlyTrustedFailedTestJobName(
        string reportedJobName,
        string expectedTestLine)
    {
        var analysisPath = Path.Combine(_workspace.Path, "analysis.json");
        var trustedJobsPath = Path.Combine(_workspace.Path, "failed-jobs.json");
        await File.WriteAllTextAsync(
            analysisPath,
            $$"""
            {
              "verdict": "flaky-test",
              "failed_jobs": [
                {
                  "id": 123,
                  "classification": "flaky-test",
                  "reason": "Known intermittent signature"
                }
              ],
              "failed_tests": [
                {
                  "name": "Tests.Flaky",
                  "job": "{{reportedJobName}}",
                  "error": "boom",
                  "stack_trace": "",
                  "classification": "flaky",
                  "reason": "Known intermittent signature"
                }
              ]
            }
            """);
        await File.WriteAllTextAsync(
            trustedJobsPath,
            """[{"id":123,"name":"Build and Test (ubuntu-latest)"}]""");

        var result = await RunBashScriptAsync(
            Path.Combine(RepoRoot.Path, CommentScriptRelativePath),
            [analysisPath, trustedJobsPath, "https://github.com/microsoft/aspire/actions/runs/123"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(
            expectedTestLine,
            Assert.Single(result.Output.Split('\n'), line => line.StartsWith("- `Tests.Flaky`", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("flaky-test")]
    [InlineData("mixed")]
    [RequiresTools(["bash", "jq"])]
    public async Task CommentRendererIncludesFailedJobsAndFlakyTestDetails(string verdict)
    {
        var analysisPath = Path.Combine(_workspace.Path, "analysis.json");
        var trustedJobsPath = Path.Combine(_workspace.Path, "failed-jobs.json");
        await File.WriteAllTextAsync(
            analysisPath,
            $$"""
            {
              "verdict": "{{verdict}}",
              "failed_jobs": [
                {
                  "id": 123,
                  "classification": "flaky-test",
                  "reason": "Known intermittent signature"
                },
                {
                  "id": 456,
                  "classification": "transient-infra",
                  "reason": "Runner disconnected"
                }
              ],
              "failed_tests": [
                {
                  "name": "Tests.Flaky",
                  "job": "Tests",
                  "error": "boom",
                  "stack_trace": "",
                  "classification": "flaky",
                  "reason": "Known intermittent signature"
                }
              ]
            }
            """);
        await File.WriteAllTextAsync(
            trustedJobsPath,
            """
            [
              {"id":123,"name":"Tests"},
              {"id":456,"name":"Infrastructure"}
            ]
            """);

        var result = await RunBashScriptAsync(
            Path.Combine(RepoRoot.Path, CommentScriptRelativePath),
            [analysisPath, trustedJobsPath, "https://github.com/microsoft/aspire/actions/runs/123"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Collection(
            result.Output.Split('\n').Where(line => line.StartsWith("- `", StringComparison.Ordinal)),
            line => Assert.Equal("- `Tests` — Known intermittent signature (flaky-test)", line),
            line => Assert.Equal("- `Infrastructure` — Runner disconnected (transient-infra)", line),
            line => Assert.Equal("- `Tests.Flaky` in job `Tests`", line));
    }

    [Fact]
    [RequiresTools(["bash", "jq"])]
    public async Task AnalysisValidatorRejectsFlakyVerdictWithoutInfraCause()
    {
        await WriteValidationFixtureAsync(
            """
            {"run_id":123,"run_scope":"pull-request","verdict":"flaky-test","pr":{"number":42},
             "failed_jobs":[{"id":1,"classification":"flaky-test"},{"id":2,"classification":"transient-infra"}],
             "failed_tests":[],
             "causes":["flaky-failure"]}
            """,
            """{"run_id":123,"run_scope":"pull-request","pr_numbers":"42"}""",
            """[{"id":1,"name":"Tests"},{"id":2,"name":"Build"}]""",
            new Dictionary<string, string>
            {
                ["flaky-failure.json"] = CreateCause("flaky-failure", "flaky-test"),
            });

        await AssertValidationRejectsMismatchedCausePresenceAsync();
    }

    [Fact]
    [RequiresTools(["bash", "jq"])]
    public async Task AnalysisValidatorRejectsCauseWithoutMatchingJob()
    {
        await WriteValidationFixtureAsync(
            """
            {"run_id":123,"run_scope":"pull-request","verdict":"flaky-test","pr":{"number":42},
             "failed_jobs":[{"id":1,"classification":"flaky-test"}],
             "failed_tests":[],
             "causes":["flaky-failure","infra-failure"]}
            """,
            """{"run_id":123,"run_scope":"pull-request","pr_numbers":"42"}""",
            """[{"id":1,"name":"Tests"}]""",
            new Dictionary<string, string>
            {
                ["flaky-failure.json"] = CreateCause("flaky-failure", "flaky-test"),
                ["infra-failure.json"] = CreateCause("infra-failure", "infra-failure"),
            });

        await AssertValidationRejectsMismatchedCausePresenceAsync();
    }

    [Fact]
    [RequiresTools(["bash", "jq"])]
    public async Task AnalysisValidatorRejectsMainMixedVerdictMissingTransientCauseType()
    {
        await WriteValidationFixtureAsync(
            """
            {"run_id":123,"run_scope":"main","verdict":"mixed","pr":null,
             "failed_jobs":[
               {"id":1,"classification":"main-repository-breakage"},
               {"id":2,"classification":"flaky-test"},
               {"id":3,"classification":"transient-infra"}],
             "failed_tests":[],
             "causes":["main-failure","flaky-failure"]}
            """,
            """{"run_id":123,"run_scope":"main","pr_numbers":""}""",
            """[{"id":1,"name":"Build"},{"id":2,"name":"Tests"},{"id":3,"name":"Setup"}]""",
            new Dictionary<string, string>
            {
                ["main-failure.json"] = CreateCause("main-failure", "main-repository-breakage"),
                ["flaky-failure.json"] = CreateCause("flaky-failure", "flaky-test"),
            });

        await AssertValidationRejectsMismatchedCausePresenceAsync();
    }

    [Fact]
    [RequiresTools(["bash", "jq"])]
    public async Task AnalysisValidatorRejectsPullRequestMixedVerdictWithWrongTransientCauseType()
    {
        await WriteValidationFixtureAsync(
            """
            {"run_id":123,"run_scope":"pull-request","verdict":"mixed","pr":{"number":42},
             "failed_jobs":[{"id":1,"classification":"code-issue"},{"id":2,"classification":"transient-infra"}],
             "failed_tests":[],
             "causes":["flaky-failure"]}
            """,
            """{"run_id":123,"run_scope":"pull-request","pr_numbers":"42"}""",
            """[{"id":1,"name":"Build"},{"id":2,"name":"Setup"}]""",
            new Dictionary<string, string>
            {
                ["flaky-failure.json"] = CreateCause("flaky-failure", "flaky-test"),
            });

        await AssertValidationRejectsMismatchedCausePresenceAsync();
    }

    [Theory]
    [InlineData("main")]
    [InlineData("pull-request")]
    [RequiresTools(["bash", "jq"])]
    public async Task AnalysisValidatorAcceptsMixedVerdictWithMatchingCauseTypes(string runScope)
    {
        var isMain = runScope == "main";
        var analysis = isMain
            ? """
              {"run_id":123,"run_scope":"main","verdict":"mixed","pr":null,
               "failed_jobs":[
                 {"id":1,"classification":"main-repository-breakage"},
                 {"id":2,"classification":"flaky-test"},
                 {"id":3,"classification":"transient-infra"}],
               "failed_tests":[],
               "causes":["main-failure","flaky-failure","infra-failure"]}
              """
            : """
              {"run_id":123,"run_scope":"pull-request","verdict":"mixed","pr":{"number":42},
               "failed_jobs":[
                 {"id":1,"classification":"code-issue"},
                 {"id":2,"classification":"flaky-test"},
                 {"id":3,"classification":"transient-infra"}],
               "failed_tests":[],
               "causes":["flaky-failure","infra-failure"]}
              """;
        var causes = new Dictionary<string, string>
        {
            ["flaky-failure.json"] = CreateCause("flaky-failure", "flaky-test"),
            ["infra-failure.json"] = CreateCause("infra-failure", "infra-failure"),
        };
        if (isMain)
        {
            causes["main-failure.json"] = CreateCause("main-failure", "main-repository-breakage");
        }

        await WriteValidationFixtureAsync(
            analysis,
            $$"""{"run_id":123,"run_scope":"{{runScope}}","pr_numbers":"{{(isMain ? "" : "42")}}"}""",
            """[{"id":1,"name":"Build"},{"id":2,"name":"Tests"},{"id":3,"name":"Setup"}]""",
            causes);

        var result = await RunValidationScriptAsync(Path.Combine(_workspace.Path, "output.json"));

        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    [RequiresTools(["bash", "jq"])]
    public async Task AnalysisValidatorAcceptsPullRequestMixedVerdictWithinOneJob()
    {
        await WriteValidationFixtureAsync(
            """
            {"run_id":123,"run_scope":"pull-request","verdict":"mixed","pr":{"number":42},
             "failed_jobs":[{"id":1,"classification":"code-issue"}],
             "failed_tests":[{"name":"Tests.Flaky","job":"Tests","error":"boom","stack_trace":"","classification":"flaky","reason":"Intermittent"}],
             "causes":["flaky-failure"]}
            """,
            """{"run_id":123,"run_scope":"pull-request","pr_numbers":"42"}""",
            """[{"id":1,"name":"Tests"}]""",
            new Dictionary<string, string>
            {
                ["flaky-failure.json"] = CreateCause("flaky-failure", "flaky-test"),
            });

        var result = await RunValidationScriptAsync(Path.Combine(_workspace.Path, "output.json"));

        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    [RequiresTools(["bash", "jq"])]
    public async Task AnalysisValidatorRejectsCodeIssueVerdictWithFlakyTest()
    {
        await WriteValidationFixtureAsync(
            """
            {"run_id":123,"run_scope":"pull-request","verdict":"code-issue","pr":{"number":42},
             "failed_jobs":[{"id":1,"classification":"code-issue"}],
             "failed_tests":[{"name":"Tests.Flaky","job":"Tests","error":"boom","stack_trace":"","classification":"flaky","reason":"Intermittent"}],
             "causes":[]}
            """,
            """{"run_id":123,"run_scope":"pull-request","pr_numbers":"42"}""",
            """[{"id":1,"name":"Tests"}]""");

        var result = await RunValidationScriptAsync(Path.Combine(_workspace.Path, "output.json"));

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(
            "::error::Analysis failed_tests are incompatible with verdict code-issue",
            result.Output,
            StringComparison.Ordinal);
    }

    [Fact]
    [RequiresTools(["bash", "jq"])]
    public async Task AnalysisValidatorAcceptsMainMixedVerdictWithinOneJob()
    {
        await WriteValidationFixtureAsync(
            """
            {"run_id":123,"run_scope":"main","verdict":"mixed","pr":null,
             "failed_jobs":[{"id":1,"classification":"main-repository-breakage"}],
             "failed_tests":[{"name":"Tests.Flaky","job":"Tests","error":"boom","stack_trace":"","classification":"flaky","reason":"Intermittent"}],
             "causes":["main-failure","flaky-failure"]}
            """,
            """{"run_id":123,"run_scope":"main","pr_numbers":""}""",
            """[{"id":1,"name":"Tests"}]""",
            new Dictionary<string, string>
            {
                ["main-failure.json"] = CreateCause("main-failure", "main-repository-breakage"),
                ["flaky-failure.json"] = CreateCause("flaky-failure", "flaky-test"),
            });

        var result = await RunValidationScriptAsync(Path.Combine(_workspace.Path, "output.json"));

        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    [RequiresTools(["bash", "jq"])]
    public async Task AnalysisValidatorRejectsMainBreakageVerdictWithFlakyTest()
    {
        await WriteValidationFixtureAsync(
            """
            {"run_id":123,"run_scope":"main","verdict":"main-repository-breakage","pr":null,
             "failed_jobs":[{"id":1,"classification":"main-repository-breakage"}],
             "failed_tests":[{"name":"Tests.Flaky","job":"Tests","error":"boom","stack_trace":"","classification":"flaky","reason":"Intermittent"}],
             "causes":["main-failure"]}
            """,
            """{"run_id":123,"run_scope":"main","pr_numbers":""}""",
            """[{"id":1,"name":"Tests"}]""",
            "main-failure.json",
            CreateCause("main-failure", "main-repository-breakage"));

        var result = await RunValidationScriptAsync(Path.Combine(_workspace.Path, "output.json"));

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(
            "::error::Analysis failed_tests are incompatible with verdict main-repository-breakage",
            result.Output,
            StringComparison.Ordinal);
    }

    [Fact]
    public void MainRepositoryBreakageUsesDedicatedIssueAndNeverPrComment()
    {
        Assert.Contains(
            "Deterministic compilation, test, API compatibility, lint, or formatting failures are `main-repository-breakage`",
            s_sourceWorkflow,
            StringComparison.Ordinal);

        ForEachExecutableWorkflow(workflow =>
        {
            Assert.Contains("CAUSE_TYPE\" = \"main-repository-breakage", workflow, StringComparison.Ordinal);
            Assert.Contains(IssueScriptRelativePath, workflow, StringComparison.Ordinal);
            Assert.Contains("gh label create \"main-ci-break\"", workflow, StringComparison.Ordinal);
            Assert.Contains(
                "if [ \"$RUN_SCOPE\" = \"main\" ]; then\necho \"Main run analysis is reported through cause issues, not PR comments.\"\nexit 0",
                workflow,
                StringComparison.Ordinal);
        });
    }

    [Fact]
    [RequiresTools(["bash", "jq"])]
    public async Task MainRepositoryBreakageIssueUsesTrustedMainContext()
    {
        var causePath = Path.Combine(_workspace.Path, "main-build-break.json");
        var runContextPath = Path.Combine(_workspace.Path, "run-context.json");
        var lastSuccessfulRunPath = Path.Combine(_workspace.Path, "last-successful-main-run.json");
        var triggeringMergePath = Path.Combine(_workspace.Path, "triggering-merge-pr.json");
        var bodyPath = Path.Combine(_workspace.Path, "issue-body.md");
        var metadataPath = Path.Combine(_workspace.Path, "issue-metadata.json");
        await File.WriteAllTextAsync(
            causePath,
            """{"id":"main-build-break","type":"main-repository-breakage","title":"Main build break","error_pattern":"Compilation failed"}""");
        await File.WriteAllTextAsync(runContextPath, """{"head_sha":"trusted-failure"}""");
        await File.WriteAllTextAsync(lastSuccessfulRunPath, """{"head_sha":"trusted-success"}""");
        await File.WriteAllTextAsync(
            triggeringMergePath,
            """{"number":41,"title":"Candidate merge","html_url":"https://github.com/microsoft/aspire/pull/41"}""");

        var result = await RunBashScriptAsync(
            Path.Combine(RepoRoot.Path, IssueScriptRelativePath),
            [
                causePath,
                runContextPath,
                lastSuccessfulRunPath,
                triggeringMergePath,
                "https://github.com/microsoft/aspire/actions/runs/123",
                "main",
                "0",
                "Build",
                "| 2026-08-31 | [123](https://github.com/microsoft/aspire/actions/runs/123) | Build | main |",
                bodyPath,
                metadataPath,
            ]);

        Assert.Equal(0, result.ExitCode);
        using var metadata = JsonDocument.Parse(await File.ReadAllTextAsync(metadataPath));
        Assert.Equal("[Main CI Failure] Main build break", metadata.RootElement.GetProperty("title").GetString());
        Assert.Equal("ci-failure-cause,main-ci-break", metadata.RootElement.GetProperty("labels").GetString());
        Assert.Equal(
            """
            <!-- ci-failure-cause:main-build-break -->
            <!-- ci-failure-cause-type:main-repository-breakage -->

            ## Build Information

            Build: https://github.com/microsoft/aspire/actions/runs/123
            Affected branch: `main`
            Last successful main SHA: `trusted-success`
            Failed main SHA: `trusted-failure`
            Triggering merge PR (context only, not necessarily causal): #41 Candidate merge

            ## Error Message

            ```
            Compilation failed
            ```

            ## Description

            Main build break

            **Type**: main-repository-breakage

            ## Occurrences

            | Date | Build | Job | Context |
            |------|-------|-----|----|
            | 2026-08-31 | [123](https://github.com/microsoft/aspire/actions/runs/123) | Build | main |
            """.ReplaceLineEndings("\n") + "\n",
            (await File.ReadAllTextAsync(bodyPath)).ReplaceLineEndings("\n"));
    }

    [Fact]
    public void PublisherValidatesAgentResultAgainstTrustedScope()
    {
        ForEachExecutableWorkflow(workflow =>
        {
            Assert.Contains(".github/workflows/analyze-ci-failure-validation.sh", workflow, StringComparison.Ordinal);
            Assert.Contains(".github/workflows/analyze-ci-failure-persistence.sh", workflow, StringComparison.Ordinal);
            Assert.Contains(".github/workflows/analyze-ci-failure-comment.sh", workflow, StringComparison.Ordinal);
            Assert.Contains("run: bash .github/workflows/analyze-ci-failure-validation.sh", workflow, StringComparison.Ordinal);
            var validationIndex = workflow.IndexOf(
                "run: bash .github/workflows/analyze-ci-failure-validation.sh",
                StringComparison.Ordinal);
            var publishStepIndex = workflow.IndexOf(
                "- name: Publish analysis data and comment on PR",
                validationIndex,
                StringComparison.Ordinal);
            Assert.True(validationIndex >= 0 && publishStepIndex > validationIndex);
        });

        var validationScript = NormalizeIndentation(s_validationScript);
        Assert.Contains("RUN_CONTEXT_FILE=\"ci-failure-data/run-context.json\"", validationScript, StringComparison.Ordinal);
        Assert.Contains("ANALYSIS_RUN_SCOPE=$(jq -r '.run_scope' \"$ANALYSIS_FILE\")", validationScript, StringComparison.Ordinal);
        Assert.Contains("Analysis result does not match trusted run context\"\nexit 1", validationScript, StringComparison.Ordinal);
        Assert.Contains("Main run analysis must not identify a subject PR\"\nexit 1", validationScript, StringComparison.Ordinal);
        Assert.Contains("Pull request analysis must identify a trusted subject PR\"\nexit 1", validationScript, StringComparison.Ordinal);
        Assert.Contains("TRUSTED_FAILED_JOBS_FILE=\"ci-failure-data/failed-jobs.json\"", validationScript, StringComparison.Ordinal);
        Assert.Contains("Analysis must contain numeric-ID failed_jobs and string-valued causes arrays\"\nexit 1", validationScript, StringComparison.Ordinal);
        Assert.Contains("Analysis failed_tests must match the safe field schema\"\nexit 1", validationScript, StringComparison.Ordinal);
        Assert.Contains("Cause ${CAUSE_BASENAME} contains unsupported or publisher-owned fields\"\nexit 1", validationScript, StringComparison.Ordinal);
        Assert.Contains("Analysis failed-job IDs do not match the trusted failed jobs\"\nexit 1", validationScript, StringComparison.Ordinal);
        Assert.Contains("Verdict '${VERDICT}' is not permitted for run scope ${TRUSTED_RUN_SCOPE}\"\nexit 1", validationScript, StringComparison.Ordinal);
        Assert.Contains("type '${CAUSE_TYPE}' is not permitted for run scope ${TRUSTED_RUN_SCOPE}\"\nexit 1", validationScript, StringComparison.Ordinal);
        Assert.Contains("Cause ${CAUSE_BASENAME} cannot change type from '${PRIOR_CAUSE_TYPE}' to '${CAUSE_TYPE}'\"\nexit 1", validationScript, StringComparison.Ordinal);
        Assert.Contains("Cause ${CAUSE_BASENAME} is not referenced by the analysis summary\"\nexit 1", validationScript, StringComparison.Ordinal);
        Assert.Contains("Analysis cause IDs must uniquely match the generated cause files\"\nexit 1", validationScript, StringComparison.Ordinal);
        Assert.Contains("Analysis must classify every failed job with a recognized classification\"\nexit 1", validationScript, StringComparison.Ordinal);
        Assert.Contains("Analysis contains a failed-job classification that is not permitted for run scope ${TRUSTED_RUN_SCOPE}\"\nexit 1", validationScript, StringComparison.Ordinal);
        Assert.Contains("if [ \"$INFRA_JOB_COUNT\" -ne \"$FAILED_JOB_COUNT\" ] ||", validationScript, StringComparison.Ordinal);
        Assert.Contains("A transient-infra verdict requires every failed job and cause to be an infrastructure failure\"\nexit 1", validationScript, StringComparison.Ordinal);
        Assert.Contains("if [ \"$FLAKY_JOB_COUNT\" -eq 0 ] || [ \"$TRANSIENT_JOB_COUNT\" -ne \"$FAILED_JOB_COUNT\" ] ||", validationScript, StringComparison.Ordinal);
        Assert.Contains("[ \"$FLAKY_CAUSE_COUNT\" -eq 0 ]", validationScript, StringComparison.Ordinal);
        Assert.Contains("A flaky-test verdict requires at least one flaky job, only transient failed jobs, and only transient causes\"\nexit 1", validationScript, StringComparison.Ordinal);
        Assert.Contains("if [ \"$CODE_ISSUE_JOB_COUNT\" -ne \"$FAILED_JOB_COUNT\" ] || [ \"$CAUSE_COUNT\" -ne 0 ]; then", validationScript, StringComparison.Ordinal);
        Assert.Contains("Analysis failed_tests are incompatible with verdict code-issue\"\nexit 1", validationScript, StringComparison.Ordinal);
        Assert.Contains("A code-issue verdict requires every failed job to be a code issue and must not include cause files\"\nexit 1", validationScript, StringComparison.Ordinal);
        Assert.Contains("if [ \"$MAIN_BREAK_JOB_COUNT\" -ne \"$FAILED_JOB_COUNT\" ] ||", validationScript, StringComparison.Ordinal);
        Assert.Contains("Analysis failed_tests are incompatible with verdict main-repository-breakage\"\nexit 1", validationScript, StringComparison.Ordinal);
        Assert.Contains("A main-repository-breakage verdict requires every failed job and cause to be a main repository breakage\"\nexit 1", validationScript, StringComparison.Ordinal);
        Assert.Contains("{ [ \"$TRANSIENT_JOB_COUNT\" -eq 0 ] && [ \"$FLAKY_TEST_COUNT\" -eq 0 ]; }", validationScript, StringComparison.Ordinal);
        Assert.Contains("A mixed verdict for main requires a main-breakage job and cause plus transient job or test evidence and cause\"\nexit 1", validationScript, StringComparison.Ordinal);
        Assert.Contains("A mixed verdict for a pull request requires a code-issue job plus transient job or test evidence and a transient cause\"\nexit 1", validationScript, StringComparison.Ordinal);

        Assert.Contains("### If failures include Transient Test Failures and no deterministic failures:", s_sourceWorkflow, StringComparison.Ordinal);
        Assert.Contains("### If ALL failures are Non-Transient PR Code Issues:", s_sourceWorkflow, StringComparison.Ordinal);
        Assert.Contains("### If ALL failures are Main Repository Breakages:", s_sourceWorkflow, StringComparison.Ordinal);
        Assert.Contains(
            "Use `\"transient-infra\"` when every failed job is an infrastructure issue, `\"flaky-test\"` when at least one failed job is a flaky test and every failed job is transient",
            s_sourceWorkflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "`failed_jobs` MUST contain exactly one object for every failed job in the summary, using its exact numeric ID, with no additions, omissions, or duplicates.",
            s_sourceWorkflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "If any of this run's tracked failures match an existing cause, you MUST reuse that cause's `id`",
            s_sourceWorkflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "PR-file relationships are indicators only for pull-request scope; main-scope `flaky-test` classification requires independent transient evidence.",
            s_sourceWorkflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "For pull-request scope, include the subject PR object when the summary provides one; otherwise use `null`.",
            s_sourceWorkflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "For every non-code failed-job classification present, write at least one cause file with the matching cause type.",
            s_sourceWorkflow,
            StringComparison.Ordinal);
    }

    [Fact]
    public void PublicationCheckoutIncludesRenderers()
    {
        ForEachExecutableWorkflow(workflow =>
        {
            var checkoutStep = GetSection(
                workflow,
                "- name: Checkout publication helpers",
                "- uses: actions/download-artifact");

            Assert.Contains(CommentScriptRelativePath, checkoutStep, StringComparison.Ordinal);
            Assert.Contains(IssueScriptRelativePath, checkoutStep, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void PublisherUsesTrustedMetadataAndVerifiesStoredIssueIdentity()
    {
        ForEachExecutableWorkflow(workflow =>
        {
            var publisher = GetSection(
                workflow,
                "RUN_CONTEXT_FILE=\"ci-failure-data/run-context.json\"",
                "# ── 4. Post PR comment using the analysis JSON ──");

            Assert.Contains("RUN_ID=\"$TRUSTED_RUN_ID\"", publisher, StringComparison.Ordinal);
            Assert.Contains("RUN_SCOPE=\"$TRUSTED_RUN_SCOPE\"", publisher, StringComparison.Ordinal);
            Assert.Contains("PR_NUMBERS=\"$TRUSTED_PR_NUMBERS\"", publisher, StringComparison.Ordinal);
            Assert.Contains("RUN_URL=$(jq -r '.html_url // \"\"' ci-failure-data/run.json)", publisher, StringComparison.Ordinal);
            Assert.Contains("ANALYZED_AT=$(date -u +\"%Y-%m-%dT%H:%M:%SZ\")", publisher, StringComparison.Ordinal);
            Assert.Contains("FIRST_JOB=$(jq -r '.[0].name // \"unknown\"' \"$TRUSTED_FAILED_JOBS_FILE\")", publisher, StringComparison.Ordinal);
            Assert.Contains("PR_NUMBER=$(bash .github/workflows/analyze-ci-failure-persistence.sh pr-number)", publisher, StringComparison.Ordinal);
            Assert.Contains("write-run-summary", publisher, StringComparison.Ordinal);
            Assert.Contains("add-occurrence", publisher, StringComparison.Ordinal);
            Assert.DoesNotContain("cp \"$ANALYSIS_FILE\"", publisher, StringComparison.Ordinal);
            Assert.Contains("($new | del(.occurrences, .issue_url))", publisher, StringComparison.Ordinal);
            Assert.Contains("if $ex.issue_url then {issue_url: $ex.issue_url} else {} end", publisher, StringComparison.Ordinal);
            Assert.Contains(
                "Stored cause ${CAUSE_BASENAME} cannot change type from '${CURRENT_CAUSE_TYPE}' to '${CAUSE_TYPE}'\"\nexit 1",
                publisher,
                StringComparison.Ordinal);
            var causeTypeIndex = publisher.IndexOf("CAUSE_TYPE=$(jq -r '.type' \"$CAUSE_FILE\")", StringComparison.Ordinal);
            var currentCauseTypeIndex = publisher.IndexOf("CURRENT_CAUSE_TYPE=$(jq -r '.type // \"\"' \"$EXISTING\")", StringComparison.Ordinal);
            Assert.True(causeTypeIndex >= 0 && causeTypeIndex < currentCauseTypeIndex);
            Assert.Contains("\"$STORED_ISSUE_URL\" =~ ^https://github\\.com/${REPO}/issues/([0-9]+)$", publisher, StringComparison.Ordinal);
            Assert.Contains(".pull_request == null", publisher, StringComparison.Ordinal);
            Assert.Contains("any(.labels[]?; .name == \"ci-failure-cause\")", publisher, StringComparison.Ordinal);
            Assert.Contains("TYPE_MARKER=\"<!-- ci-failure-cause-type:${CAUSE_TYPE} -->\"", publisher, StringComparison.Ordinal);
            Assert.Contains("map(rtrimstr(\"\\r\"))", publisher, StringComparison.Ordinal);
            Assert.Contains("$lines[0] == $marker", publisher, StringComparison.Ordinal);
            Assert.Contains("$lines[1] == $type_marker", publisher, StringComparison.Ordinal);
            Assert.Contains("[\"**Type**: \" + $cause_type]", publisher, StringComparison.Ordinal);
            Assert.True(
                publisher.IndexOf("git -C memory-repo push origin \"HEAD:$MEMORY_BRANCH\"", StringComparison.Ordinal) <
                publisher.IndexOf("# ── 2. Create or update issues for each cause ──", StringComparison.Ordinal));
            Assert.Contains(
                "\"$ANALYSIS_FILE\" \"$TRUSTED_FAILED_JOBS_FILE\" \"$RUN_URL\" > \"$COMMENT_FILE\"",
                workflow,
                StringComparison.Ordinal);
            Assert.Contains(".user.login == \\\"github-actions[bot]\\\"", workflow, StringComparison.Ordinal);
            Assert.Contains("startswith(\\\"${MARKER}\\\\n\\\")", workflow, StringComparison.Ordinal);
        });
        Assert.Contains("FAILED_SHA=$(jq -r '.head_sha // \"unknown\"' \"$RUN_CONTEXT_FILE\")", s_issueScript, StringComparison.Ordinal);
        Assert.Contains("LAST_SUCCESSFUL_SHA=$(jq -r '.head_sha // \"unknown\"' \"$LAST_SUCCESSFUL_RUN_FILE\")", s_issueScript, StringComparison.Ordinal);
        Assert.Contains("TRIGGERING_MERGE=$(jq -r 'if .number then \"#\\(.number) \\(.title)\" else \"Not found\" end' \"$TRIGGERING_MERGE_FILE\")", s_issueScript, StringComparison.Ordinal);
    }

    [Fact]
    public void CommentStepDefinesTrustedFailedJobsPath()
    {
        ForEachExecutableWorkflow(workflow =>
        {
            var commentStep = GetSection(
                workflow,
                "- name: Comment on PR",
                "# Update an existing analysis comment if one exists");
            var trustedJobsPathIndex = commentStep.IndexOf(
                "TRUSTED_FAILED_JOBS_FILE=\"ci-failure-data/failed-jobs.json\"",
                StringComparison.Ordinal);
            var rendererIndex = commentStep.IndexOf(
                "bash .github/workflows/analyze-ci-failure-comment.sh",
                StringComparison.Ordinal);

            Assert.True(trustedJobsPathIndex >= 0 && trustedJobsPathIndex < rendererIndex);
        });
    }

    [Fact]
    public void PublicationIsSerializedAcrossAnalyzedRuns()
    {
        foreach (var workflow in s_executableWorkflows)
        {
            Assert.Equal(
                "cancel-in-progress=false;group=analyze-ci-failure;queue=max",
                ExtractTopLevelMapping(workflow, "concurrency"));
        }
    }

    [Fact]
    public void WorkflowRunCollectionPinsTriggerAttemptAndTestArtifacts()
    {
        ForEachExecutableWorkflow(workflow =>
        {
            var collectionStep = GetSection(
                workflow,
                "- name: Collect CI failure data",
                "- name: Create analysis summary");

            Assert.Contains(
                "WORKFLOW_RUN_ATTEMPT: ${{ github.event.workflow_run.run_attempt }}",
                collectionStep,
                StringComparison.Ordinal);
            Assert.Contains(
                "repos/${REPO}/actions/runs/${RUN_ID}/attempts/${WORKFLOW_RUN_ATTEMPT}",
                collectionStep,
                StringComparison.Ordinal);
            Assert.Contains(
                ".created_at >= $started_at and .created_at <= $updated_at",
                collectionStep,
                StringComparison.Ordinal);
            Assert.Contains(
                "repos/${REPO}/actions/artifacts/${ARTIFACT_ID}/zip",
                collectionStep,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "gh run download \"${RUN_ID}\"",
                collectionStep,
                StringComparison.Ordinal);
        });
    }

    [Fact]
    public void RerunUsesTrustedRunContext()
    {
        ForEachExecutableWorkflow(workflow =>
        {
            Assert.Contains("const trustedRunId = Number(runContext.run_id);", workflow, StringComparison.Ordinal);
            Assert.Contains("const trustedRunAttempt = Number(runContext.run_attempt);", workflow, StringComparison.Ordinal);
            Assert.Contains("requestedRunId !== trustedRunId", workflow, StringComparison.Ordinal);
            Assert.Contains("analysis.verdict !== 'transient-infra'", workflow, StringComparison.Ordinal);
            Assert.Contains("if (trustedRunScope === 'pull-request')", workflow, StringComparison.Ordinal);
            Assert.Contains("run_id: trustedRunId", workflow, StringComparison.Ordinal);
            Assert.Contains("currentRun.run_attempt !== trustedRunAttempt", workflow, StringComparison.Ordinal);

            var rerunValidation = GetSection(
                workflow,
                "const analysisFile = path.join(path.dirname(outputFile), 'agent', 'analysis-result.json');",
                "if (!enableRerun)");
            Assert.Contains("const causesDir = path.join(path.dirname(outputFile), 'agent', 'causes');", rerunValidation, StringComparison.Ordinal);
            Assert.Contains("const trustedFailedJobsFile = path.join('ci-failure-data', 'failed-jobs.json');", rerunValidation, StringComparison.Ordinal);
            Assert.Contains("analysisJobIdSet.size !== trustedJobIdSet.size", rerunValidation, StringComparison.Ordinal);
            Assert.Contains("!analysisJobIds.every(jobId => trustedJobIdSet.has(jobId))", rerunValidation, StringComparison.Ordinal);
            Assert.Contains("core.setFailed('Rerun requires unique analysis cause IDs matching the generated cause files');\nreturn;", rerunValidation, StringComparison.Ordinal);
            Assert.Contains("cause.type !== 'infra-failure'", rerunValidation, StringComparison.Ordinal);
            Assert.Contains("!summaryCauseIds.includes(causeId)", rerunValidation, StringComparison.Ordinal);
            Assert.Contains("!analysis.failed_jobs.every(job => job && job.classification === 'transient-infra')", rerunValidation, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void AgentInstructionsRequireTransientInfraToOmitFailedTests()
    {
        Assert.Contains(
            "Set `failed_tests` to an empty array for `transient-infra`",
            s_sourceWorkflow,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AgentInstructionsUseMixedVerdictForMultipleFailureTypesWithinOneJob()
    {
        Assert.Contains(
            "A single failed job can contain both a deterministic failure and a flaky failed test.",
            s_sourceWorkflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "classify the job by the deterministic failure, include the flaky test and its cause, and use `mixed`",
            s_sourceWorkflow,
            StringComparison.Ordinal);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task RerunUsesTrustedRunIdForValidTransientAnalysis()
    {
        await WriteRerunFixtureAsync(
            """{"run_id":123,"run_scope":"pull-request","verdict":"transient-infra","failed_jobs":[{"id":456,"classification":"transient-infra"}],"failed_tests":[],"causes":["nuget-timeout"]}""",
            """{"id":"nuget-timeout","type":"infra-failure"}""");

        var result = await RunRerunScriptAsync();

        Assert.Empty(result.Failed);
        Assert.Equal([123], result.Reruns);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task RerunRejectsTransientAnalysisWithFailedTests()
    {
        await WriteRerunFixtureAsync(
            """{"run_id":123,"run_scope":"pull-request","verdict":"transient-infra","failed_jobs":[{"id":456,"classification":"transient-infra"}],"failed_tests":[{"name":"Tests.Deterministic","job":"Tests","error":"boom","classification":"code-issue","reason":"Deterministic"}],"causes":["nuget-timeout"]}""",
            """{"id":"nuget-timeout","type":"infra-failure"}""");

        var result = await RunRerunScriptAsync();

        Assert.Equal(["Rerun requires a transient-infra analysis without failed tests"], result.Failed);
        Assert.Empty(result.Reruns);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task RerunRejectsCauseWhoseStoredTypeChanged()
    {
        await WriteRerunFixtureAsync(
            """{"run_id":123,"run_scope":"pull-request","verdict":"transient-infra","failed_jobs":[{"id":456,"classification":"transient-infra"}],"failed_tests":[],"causes":["nuget-timeout"]}""",
            """{"id":"nuget-timeout","type":"infra-failure"}""",
            """{"id":"nuget-timeout","type":"flaky-test"}""");

        var result = await RunRerunScriptAsync();

        Assert.Equal(["Rerun cause nuget-timeout.json cannot change stored type from 'flaky-test' to 'infra-failure'"], result.Failed);
        Assert.Empty(result.Reruns);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task RerunRejectsMalformedStoredCause()
    {
        await WriteRerunFixtureAsync(
            """{"run_id":123,"run_scope":"pull-request","verdict":"transient-infra","failed_jobs":[{"id":456,"classification":"transient-infra"}],"failed_tests":[],"causes":["nuget-timeout"]}""",
            """{"id":"nuget-timeout","type":"infra-failure"}""",
            """{"id":"nuget-timeout","type":""");

        var result = await RunRerunScriptAsync();

        Assert.Equal(["Invalid JSON in prior rerun cause file nuget-timeout.json"], result.Failed);
        Assert.Empty(result.Reruns);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task RerunRejectsStoredCauseThatIsNotAnObject()
    {
        await WriteRerunFixtureAsync(
            """{"run_id":123,"run_scope":"pull-request","verdict":"transient-infra","failed_jobs":[{"id":456,"classification":"transient-infra"}],"failed_tests":[],"causes":["nuget-timeout"]}""",
            """{"id":"nuget-timeout","type":"infra-failure"}""",
            "null");

        var result = await RunRerunScriptAsync();

        Assert.Equal(["Prior rerun cause nuget-timeout.json must be an object with a string type"], result.Failed);
        Assert.Empty(result.Reruns);
    }

    [Fact]
    [RequiresTools(["bash", "jq"])]
    public async Task LastSuccessfulMainRunUsesExplicitOrderingForShuffledResults()
    {
        var fakeGh = """
            #!/usr/bin/env bash
            echo "$*" >> "${GH_CALL_LOG}"
            cat <<'JSON'
            {
              "total_count": 4,
              "workflow_runs": [
                {"id": 30, "created_at": "2026-08-30T11:00:00Z", "head_sha": "after"},
                {"id": 20, "created_at": "2026-08-30T09:00:00Z", "head_sha": "latest"},
                {"id": 20, "created_at": "2026-08-30T09:00:00Z", "head_sha": "latest"},
                {"id": 10, "created_at": "2026-08-30T08:00:00Z", "head_sha": "older"}
              ]
            }
            JSON
            """;

        var result = await RunHistoryScriptAsync(
            fakeGh,
            "2026-08-30T10:00:00Z",
            Path.Combine(_workspace.Path, "last-success.json"));

        Assert.Equal(0, result.ExitCode);
        using var output = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(_workspace.Path, "last-success.json")));
        Assert.Equal(20, output.RootElement.GetProperty("id").GetInt64());
        Assert.Equal("latest", output.RootElement.GetProperty("head_sha").GetString());
        Assert.Contains("per_page=100", await File.ReadAllTextAsync(Path.Combine(_workspace.Path, "gh-calls.log")), StringComparison.Ordinal);
    }

    [Fact]
    [RequiresTools(["bash", "jq"])]
    public async Task LastSuccessfulMainRunSubdividesCappedWindows()
    {
        var fakeGh = """
            #!/usr/bin/env bash
            echo "$*" >> "${GH_CALL_LOG}"
            call_count_file="${GH_CALL_COUNT_FILE}"
            call_count=$(cat "$call_count_file" 2>/dev/null || echo 0)
            call_count=$((call_count + 1))
            echo "$call_count" > "$call_count_file"
            if [ "$call_count" -eq 1 ]; then
              echo '{"total_count":1000,"workflow_runs":[]}'
            else
              echo '{"total_count":1,"workflow_runs":[{"id":77,"created_at":"2026-08-30T09:30:00Z","head_sha":"subdivided"}]}'
            fi
            """;

        var result = await RunHistoryScriptAsync(
            fakeGh,
            "2026-08-30T10:00:00Z",
            Path.Combine(_workspace.Path, "last-success.json"));

        Assert.Equal(0, result.ExitCode);
        using var output = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(_workspace.Path, "last-success.json")));
        Assert.Equal(77, output.RootElement.GetProperty("id").GetInt64());
        Assert.True(int.Parse(await File.ReadAllTextAsync(Path.Combine(_workspace.Path, "gh-call-count"))) >= 2);
        Assert.True(
            (await File.ReadAllLinesAsync(Path.Combine(_workspace.Path, "gh-calls.log")))
                .Select(line => line[(line.IndexOf("created=", StringComparison.Ordinal))..])
                .Distinct(StringComparer.Ordinal)
                .Count() >= 2);
    }

    [Fact]
    [RequiresTools(["bash", "jq"])]
    public async Task LastSuccessfulMainRunFallsBackToOlderWindow()
    {
        var fakeGh = """
            #!/usr/bin/env bash
            echo "$*" >> "${GH_CALL_LOG}"
            call_count_file="${GH_CALL_COUNT_FILE}"
            call_count=$(cat "$call_count_file" 2>/dev/null || echo 0)
            call_count=$((call_count + 1))
            echo "$call_count" > "$call_count_file"
            if [ "$call_count" -eq 1 ]; then
              echo '{"total_count":0,"workflow_runs":[]}'
            else
              echo '{"total_count":1,"workflow_runs":[{"id":55,"created_at":"2026-08-28T09:00:00Z","head_sha":"older-window"}]}'
            fi
            """;

        var result = await RunHistoryScriptAsync(
            fakeGh,
            "2026-08-30T10:00:00Z",
            Path.Combine(_workspace.Path, "last-success.json"));

        Assert.Equal(0, result.ExitCode);
        using var output = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(_workspace.Path, "last-success.json")));
        Assert.Equal(55, output.RootElement.GetProperty("id").GetInt64());
        Assert.Equal(2, int.Parse(await File.ReadAllTextAsync(Path.Combine(_workspace.Path, "gh-call-count"))));
        Assert.Equal(
            2,
            (await File.ReadAllLinesAsync(Path.Combine(_workspace.Path, "gh-calls.log")))
                .Select(line => line[(line.IndexOf("created=", StringComparison.Ordinal))..])
                .Distinct(StringComparer.Ordinal)
                .Count());
    }

    [Fact]
    [RequiresTools(["bash", "jq"])]
    public async Task LastSuccessfulMainRunSurfacesApiFailure()
    {
        var result = await RunHistoryScriptAsync(
            "#!/usr/bin/env bash\nexit 1",
            "2026-08-30T10:00:00Z",
            Path.Combine(_workspace.Path, "last-success.json"));

        Assert.NotEqual(0, result.ExitCode);
    }

    [Fact]
    [RequiresTools(["bash", "jq"])]
    public async Task CandidateMergeCollectionPreservesResultsWhenAssociationIsIncomplete()
    {
        var fakeGh = """
            #!/usr/bin/env bash
            echo "$*" >> "${GH_CALL_LOG}"
            case "$*" in
              *"compare/trusted-success...trusted-failure"*)
                cat <<'JSON'
            [
              {
                "total_commits": 2,
                "commits": [
                  {"sha":"unavailable","commit":{"message":"Unavailable commit"},"html_url":"https://github.com/microsoft/aspire/commit/unavailable"}
                ]
              },
              {
                "commits": [
                  {"sha":"associated","commit":{"message":"Associated commit"},"html_url":"https://github.com/microsoft/aspire/commit/associated"}
                ]
              }
            ]
            JSON
                ;;
              *"commits/associated/pulls"*)
                echo '{"number":41,"title":"Associated PR","html_url":"https://github.com/microsoft/aspire/pull/41","merged_at":"2026-08-30T00:00:00Z"}'
                ;;
              *"commits/unavailable/pulls"*)
                exit 1
                ;;
              *)
                exit 99
                ;;
            esac
            """;
        var candidatesPath = Path.Combine(_workspace.Path, "candidate-merges.json");
        var statusPath = Path.Combine(_workspace.Path, "candidate-merge-history-status.json");

        var result = await RunCandidateScriptAsync(fakeGh, candidatesPath, statusPath);

        Assert.Equal(0, result.ExitCode);
        using var candidates = JsonDocument.Parse(await File.ReadAllTextAsync(candidatesPath));
        var candidate = Assert.Single(candidates.RootElement.EnumerateArray());
        Assert.Equal("associated", candidate.GetProperty("sha").GetString());
        Assert.Equal(41, candidate.GetProperty("pull_request").GetProperty("number").GetInt32());
        using var status = JsonDocument.Parse(await File.ReadAllTextAsync(statusPath));
        Assert.Equal("incomplete", status.RootElement.GetProperty("state").GetString());
        var ghCalls = await File.ReadAllLinesAsync(Path.Combine(_workspace.Path, "gh-calls.log"));
        Assert.Contains(
            ghCalls,
            call => call.Contains("compare/trusted-success...trusted-failure", StringComparison.Ordinal)
                && call.Contains("--paginate", StringComparison.Ordinal)
                && call.Contains("--slurp", StringComparison.Ordinal));
        Assert.Contains(ghCalls, call => call.Contains("commits/unavailable/pulls", StringComparison.Ordinal));
        Assert.Contains(ghCalls, call => call.Contains("commits/associated/pulls", StringComparison.Ordinal));
    }

    [Fact]
    [RequiresTools(["bash", "jq"])]
    public async Task CandidateMergeCollectionReportsIncompleteWhenCompareRangeIsTruncated()
    {
        var fakeGh = """
            #!/usr/bin/env bash
            case "$*" in
              *"compare/trusted-success...trusted-failure"*)
                cat <<'JSON'
            [
              {
                "total_commits": 5,
                "commits": [
                  {"sha":"associated","commit":{"message":"Associated commit"},"html_url":"https://github.com/microsoft/aspire/commit/associated"}
                ]
              }
            ]
            JSON
                ;;
              *"commits/associated/pulls"*)
                echo '{"number":41,"title":"Associated PR","html_url":"https://github.com/microsoft/aspire/pull/41","merged_at":"2026-08-30T00:00:00Z"}'
                ;;
              *)
                exit 99
                ;;
            esac
            """;
        var candidatesPath = Path.Combine(_workspace.Path, "candidate-merges.json");
        var statusPath = Path.Combine(_workspace.Path, "candidate-merge-history-status.json");

        var result = await RunCandidateScriptAsync(fakeGh, candidatesPath, statusPath);

        Assert.Equal(0, result.ExitCode);
        using var candidates = JsonDocument.Parse(await File.ReadAllTextAsync(candidatesPath));
        var candidate = Assert.Single(candidates.RootElement.EnumerateArray());
        Assert.Equal("associated", candidate.GetProperty("sha").GetString());
        using var status = JsonDocument.Parse(await File.ReadAllTextAsync(statusPath));
        Assert.Equal("incomplete", status.RootElement.GetProperty("state").GetString());
    }

    [Fact]
    [RequiresTools(["bash", "jq"])]
    public async Task PersistedMainAnalysisRebuildsAllContextFromTrustedArtifacts()
    {
        await WritePersistenceFixtureAsync(
            """
            {
              "run_id": 999,
              "run_attempt": 99,
              "run_url": "https://evil.example/run",
              "run_scope": "main",
              "analyzed_at": "1999-01-01T00:00:00Z",
              "verdict": "main-repository-breakage",
              "pr": null,
              "triggering_merge_pr": {"number":999,"title":"forged triggering merge"},
              "main_context": {"last_successful_main_sha":"forged","failed_sha":"forged","candidate_merges":[{"sha":"forged"}]},
              "failed_jobs": [{"id":123,"classification":"main-repository-breakage","reason":"compiler failed"}],
              "failed_tests": [],
              "causes": ["main-build-break"]
            }
            """,
            """{"run_id":123,"run_attempt":2,"run_scope":"main","head_sha":"trusted-failed","pr_numbers":""}""",
            """{"html_url":"https://github.com/microsoft/aspire/actions/runs/123"}""",
            """[{"id":123,"name":"Build","conclusion":"failure","html_url":"https://github.com/job/123","steps":[{"name":"Compile","conclusion":"failure"}]}]""",
            """{"number":42,"title":"Trusted merge","html_url":"https://github.com/microsoft/aspire/pull/42"}""",
            """{"head_sha":"trusted-success"}""",
            """[{"sha":"trusted-candidate","message":"candidate","html_url":"https://github.com/commit","pull_request":{"number":41,"title":"Candidate","url":"https://github.com/microsoft/aspire/pull/41","merged_at":"2026-08-29T00:00:00Z"}}]""");

        var outputPath = Path.Combine(_workspace.Path, "persisted-main.json");
        var result = await RunPersistenceScriptAsync("write-run-summary", outputPath);

        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(outputPath));
        var root = document.RootElement;
        Assert.Equal(12, root.EnumerateObject().Count());
        Assert.Equal(123, root.GetProperty("run_id").GetInt64());
        Assert.Equal(2, root.GetProperty("run_attempt").GetInt32());
        Assert.Equal("https://github.com/microsoft/aspire/actions/runs/123", root.GetProperty("run_url").GetString());
        Assert.Equal("main", root.GetProperty("run_scope").GetString());
        Assert.Equal("2026-08-31T12:00:00Z", root.GetProperty("analyzed_at").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("pr").ValueKind);

        var triggeringMerge = root.GetProperty("triggering_merge_pr");
        Assert.Equal(8, triggeringMerge.EnumerateObject().Count());
        Assert.Equal(42, triggeringMerge.GetProperty("number").GetInt32());
        Assert.Equal("Trusted merge", triggeringMerge.GetProperty("title").GetString());
        Assert.Equal("https://github.com/microsoft/aspire/pull/42", triggeringMerge.GetProperty("url").GetString());

        var mainContext = root.GetProperty("main_context");
        Assert.Equal(3, mainContext.EnumerateObject().Count());
        Assert.Equal("trusted-failed", mainContext.GetProperty("failed_sha").GetString());
        Assert.Equal("trusted-success", mainContext.GetProperty("last_successful_main_sha").GetString());
        Assert.Equal("trusted-candidate", mainContext.GetProperty("candidate_merges")[0].GetProperty("sha").GetString());

        var failedJob = root.GetProperty("failed_jobs")[0];
        Assert.Equal(7, failedJob.EnumerateObject().Count());
        Assert.Equal("Build", failedJob.GetProperty("name").GetString());
        Assert.Equal("main-repository-breakage", failedJob.GetProperty("classification").GetString());
        Assert.Equal("compiler failed", failedJob.GetProperty("reason").GetString());
        Assert.Equal("Compile", failedJob.GetProperty("failed_steps")[0].GetString());
    }

    [Fact]
    [RequiresTools(["bash", "jq"])]
    public async Task PersistedPullRequestAnalysisKeepsSemanticsAndUsesTrustedPrContext()
    {
        await WritePersistenceFixtureAsync(
            """
            {
              "run_id": 999,
              "run_attempt": 99,
              "run_url": "https://evil.example/run",
              "run_scope": "pull-request",
              "analyzed_at": "1999-01-01T00:00:00Z",
              "verdict": "flaky-test",
              "pr": {"number":999,"title":"forged PR","url":"https://evil.example/pr"},
              "triggering_merge_pr": {"number":998},
              "main_context": {"failed_sha":"forged"},
              "failed_jobs": [{"id":123,"classification":"flaky-test","reason":"known flaky test"}],
              "failed_tests": [{"name":"Tests.Flaky","job":"Tests","error":"boom","stack_trace":"frame","classification":"flaky","reason":"known signature"}],
              "causes": ["flaky-test"]
            }
            """,
            """{"run_id":123,"run_attempt":2,"run_scope":"pull-request","head_sha":"trusted-pr-sha","pr_numbers":"42"}""",
            """{"html_url":"https://github.com/microsoft/aspire/actions/runs/123"}""",
            """[{"id":123,"name":"Tests","conclusion":"failure","html_url":"https://github.com/job/123","steps":[{"name":"Run tests","conclusion":"failure"}]}]""",
            "{}",
            "{}",
            "[]",
            """{"number":42,"title":"Trusted PR","state":"open","user":"octocat","head_branch":"feature","base_branch":"main","html_url":"https://github.com/microsoft/aspire/pull/42"}""");

        var outputPath = Path.Combine(_workspace.Path, "persisted-pr.json");
        var result = await RunPersistenceScriptAsync("write-run-summary", outputPath);

        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(outputPath));
        var root = document.RootElement;
        Assert.Equal(12, root.EnumerateObject().Count());
        Assert.Equal(123, root.GetProperty("run_id").GetInt64());
        Assert.Equal(2, root.GetProperty("run_attempt").GetInt32());
        Assert.Equal("https://github.com/microsoft/aspire/actions/runs/123", root.GetProperty("run_url").GetString());
        Assert.Equal("2026-08-31T12:00:00Z", root.GetProperty("analyzed_at").GetString());

        var pr = root.GetProperty("pr");
        Assert.Equal(7, pr.EnumerateObject().Count());
        Assert.Equal(42, pr.GetProperty("number").GetInt32());
        Assert.Equal("Trusted PR", pr.GetProperty("title").GetString());
        Assert.Equal("https://github.com/microsoft/aspire/pull/42", pr.GetProperty("url").GetString());
        Assert.Equal("known flaky test", root.GetProperty("failed_jobs")[0].GetProperty("reason").GetString());
        Assert.Equal("Tests.Flaky", root.GetProperty("failed_tests")[0].GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("triggering_merge_pr").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("main_context").ValueKind);
    }

    [Theory]
    [InlineData("main", "", "0")]
    [InlineData("pull-request", "42,43", "42")]
    [RequiresTools(["bash", "jq"])]
    public async Task PersistedOccurrenceUsesOnlyTrustedSubjectPr(
        string runScope,
        string trustedPrNumbers,
        string expectedPrNumber)
    {
        var failureDataDirectory = Directory.CreateDirectory(Path.Combine(_workspace.Path, "ci-failure-data")).FullName;
        await File.WriteAllTextAsync(
            Path.Combine(failureDataDirectory, "run-context.json"),
            $$"""{"run_id":123,"run_scope":"{{runScope}}","pr_numbers":"{{trustedPrNumbers}}"}""");
        var causeFile = Path.Combine(_workspace.Path, "cause.json");
        await File.WriteAllTextAsync(
            causeFile,
            """{"id":"test-failure","type":"flaky-test","title":"Test failure","error_pattern":"boom"}""");

        var result = await RunBashScriptAsync(
            Path.Combine(RepoRoot.Path, PersistenceScriptRelativePath),
            ["add-occurrence", causeFile, "123", "https://github.com/run/123", "Tests", "2026-08-31T12:00:00Z"],
            new Dictionary<string, string>
            {
                ["CI_FAILURE_DATA_DIR"] = failureDataDirectory,
            });

        Assert.Equal(0, result.ExitCode);
        using var output = JsonDocument.Parse(result.Output);
        Assert.Equal(expectedPrNumber, output.RootElement.GetProperty("occurrences")[0].GetProperty("pr_number").GetRawText());
    }

    [Fact]
    public void PublicationDoesNotRenderUnavailablePrAsNumber()
    {
        ForEachExecutableWorkflow(workflow =>
        {
            Assert.Contains(
                "elif [ \"$PR_NUMBER\" = \"0\" ]; then\nOCCURRENCE_CONTEXT=\"unavailable\"",
                workflow,
                StringComparison.Ordinal);
        });
        Assert.Contains(
            "  if [ \"$RUN_SCOPE\" = \"pull-request\" ] && [ \"$PR_NUMBER\" != \"0\" ]; then\n    echo \"Pull request: #${PR_NUMBER}\"",
            s_issueScript,
            StringComparison.Ordinal);
    }

    private static void ForEachExecutableWorkflow(Action<string> assertion)
    {
        foreach (var workflow in s_executableWorkflows)
        {
            assertion(NormalizeIndentation(workflow));
        }
    }

    private static string NormalizeIndentation(string value)
        => string.Join('\n', value.ReplaceLineEndings("\n").Split('\n').Select(line => line.TrimStart()));

    private static string GetSection(string value, string start, string end)
    {
        var startIndex = value.IndexOf(start, StringComparison.Ordinal);
        Assert.True(startIndex >= 0, $"Could not find section start: {start}");
        var endIndex = value.IndexOf(end, startIndex, StringComparison.Ordinal);
        Assert.True(endIndex >= 0, $"Could not find section end: {end}");
        return value[startIndex..(endIndex + end.Length)];
    }

    private static string ExtractTriggeringMergeSelector(string workflow)
    {
        const string ContextMarker = "# The PR associated with the failed head commit identifies the merge";
        const string SelectorMarker = "--jq \"";
        const string SelectorEnd = "\" \\";

        var contextIndex = workflow.IndexOf(ContextMarker, StringComparison.Ordinal);
        Assert.True(contextIndex >= 0);
        var selectorStart = workflow.IndexOf(SelectorMarker, contextIndex, StringComparison.Ordinal);
        Assert.True(selectorStart >= 0);
        selectorStart += SelectorMarker.Length;
        var selectorEnd = workflow.IndexOf(SelectorEnd, selectorStart, StringComparison.Ordinal);
        Assert.True(selectorEnd >= 0);

        return workflow[selectorStart..selectorEnd]
            .Replace("\\\"", "\"", StringComparison.Ordinal)
            .Replace("${REPO}", "microsoft/aspire", StringComparison.Ordinal);
    }

    private static string ExtractTopLevelMapping(string workflow, string key)
    {
        var lines = workflow.ReplaceLineEndings("\n").Split('\n');
        var mappingStart = Array.IndexOf(lines, $"{key}:");
        Assert.True(mappingStart >= 0, $"Could not find top-level mapping: {key}");

        return string.Join(
            ';',
            lines
                .Skip(mappingStart + 1)
                .TakeWhile(line => line.Length == 0 || char.IsWhiteSpace(line[0]))
                .Select(line => line.Trim())
                .Where(line => line.Length > 0 && !line.StartsWith('#'))
                .Select(line => line.Split(':', 2))
                .Select(parts => $"{parts[0]}={parts[1].Trim()}")
                .Order());
    }

    private static string CreateCause(string id, string type)
        => $$"""{"id":"{{id}}","type":"{{type}}","title":"Failure","error_pattern":"boom"}""";

    private static string ReadWorkflow(string fileName)
        => File.ReadAllText(Path.Combine(RepoRoot.Path, ".github", "workflows", fileName));

    private async Task<CommandResult> RunValidationScriptAsync(string agentOutputPath)
    {
        var scriptPath = Path.Combine(RepoRoot.Path, ValidationScriptRelativePath);
        Assert.True(File.Exists(scriptPath), $"Expected validation helper at '{ValidationScriptRelativePath}'.");

        using var process = new Process();
        process.StartInfo.FileName = "bash";
        process.StartInfo.ArgumentList.Add(scriptPath);
        process.StartInfo.WorkingDirectory = _workspace.Path;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.Environment["GH_AW_AGENT_OUTPUT"] = agentOutputPath;

        process.Start();

        // Read both streams concurrently to avoid deadlock when the validator emits diagnostics.
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await process.WaitForExitAsync(timeout.Token);

        return new CommandResult(process.ExitCode, await stdoutTask + await stderrTask);
    }

    private async Task<CommandResult> RunHistoryScriptAsync(string fakeGh, string failedRunCreatedAt, string outputPath)
    {
        var fakeBinDirectory = Directory.CreateDirectory(Path.Combine(_workspace.Path, "fake-bin")).FullName;
        var fakeGhPath = Path.Combine(fakeBinDirectory, "gh");
        await File.WriteAllTextAsync(fakeGhPath, fakeGh);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                fakeGhPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return await RunBashScriptAsync(
            Path.Combine(RepoRoot.Path, HistoryScriptRelativePath),
            ["microsoft/aspire", "137649006", failedRunCreatedAt, outputPath],
            new Dictionary<string, string>
            {
                ["PATH"] = $"{fakeBinDirectory}{Path.PathSeparator}{Environment.GetEnvironmentVariable("PATH")}",
                ["GH_CALL_LOG"] = Path.Combine(_workspace.Path, "gh-calls.log"),
                ["GH_CALL_COUNT_FILE"] = Path.Combine(_workspace.Path, "gh-call-count"),
            });
    }

    private async Task<CommandResult> RunCandidateScriptAsync(string fakeGh, string candidatesPath, string statusPath)
    {
        var fakeBinDirectory = Directory.CreateDirectory(Path.Combine(_workspace.Path, "fake-bin")).FullName;
        var fakeGhPath = Path.Combine(fakeBinDirectory, "gh");
        await File.WriteAllTextAsync(fakeGhPath, fakeGh);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                fakeGhPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return await RunBashScriptAsync(
            Path.Combine(RepoRoot.Path, CandidatesScriptRelativePath),
            ["microsoft/aspire", "trusted-success", "trusted-failure", candidatesPath, statusPath],
            new Dictionary<string, string>
            {
                ["PATH"] = $"{fakeBinDirectory}{Path.PathSeparator}{Environment.GetEnvironmentVariable("PATH")}",
                ["GH_CALL_LOG"] = Path.Combine(_workspace.Path, "gh-calls.log"),
            });
    }

    private Task<CommandResult> RunJqAsync(string selector, string input)
        => RunProcessAsync("jq", ["-c", selector], standardInput: input);

    private async Task WriteRerunFixtureAsync(string analysis, string cause, string? priorCause = null)
    {
        var agentDirectory = Directory.CreateDirectory(Path.Combine(_workspace.Path, "agent")).FullName;
        var causesDirectory = Directory.CreateDirectory(Path.Combine(agentDirectory, "causes")).FullName;
        var failureDataDirectory = Directory.CreateDirectory(Path.Combine(_workspace.Path, "ci-failure-data")).FullName;
        await File.WriteAllTextAsync(
            Path.Combine(_workspace.Path, "output.json"),
            """{"items":[{"type":"rerun_failed_jobs","run_id":123,"reason":"Transient infrastructure failure"}]}""");
        await File.WriteAllTextAsync(Path.Combine(agentDirectory, "analysis-result.json"), analysis);
        await File.WriteAllTextAsync(Path.Combine(causesDirectory, "nuget-timeout.json"), cause);
        await File.WriteAllTextAsync(
            Path.Combine(failureDataDirectory, "run-context.json"),
            """{"run_id":123,"run_attempt":1,"run_scope":"pull-request","pr_numbers":"42"}""");
        await File.WriteAllTextAsync(
            Path.Combine(failureDataDirectory, "failed-jobs.json"),
            """[{"id":456,"name":"Tests"}]""");
        if (priorCause is not null)
        {
            var priorCausesDirectory = Directory.CreateDirectory(Path.Combine(failureDataDirectory, "prior-causes")).FullName;
            await File.WriteAllTextAsync(Path.Combine(priorCausesDirectory, "nuget-timeout.json"), priorCause);
        }
    }

    private async Task<RerunHarnessResult> RunRerunScriptAsync()
    {
        var requestPath = Path.Combine(_workspace.Path, "rerun-request.json");
        var outputPath = Path.Combine(_workspace.Path, "rerun-result.json");
        var script = ExtractWorkflowScript("analyze-ci-failure.lock.yml", "- name: Rerun failed jobs");
        await File.WriteAllTextAsync(
            requestPath,
            JsonSerializer.Serialize(new
            {
                script,
                agentOutputPath = Path.Combine(_workspace.Path, "output.json"),
            }));

        var result = await RunProcessAsync(
            "node",
            [
                Path.Combine(RepoRoot.Path, "tests", "Infrastructure.Tests", "WorkflowScripts", "analyze-ci-failure-rerun.harness.js"),
                requestPath,
                outputPath,
            ]);

        Assert.Equal(0, result.ExitCode);
        var response = JsonSerializer.Deserialize<RerunHarnessResult>(await File.ReadAllTextAsync(outputPath));
        return Assert.IsType<RerunHarnessResult>(response);
    }

    private static string ExtractWorkflowScript(string workflowFileName, string stepName)
    {
        var lines = ReadWorkflow(workflowFileName).ReplaceLineEndings("\n").Split('\n');
        var stepIndex = Array.FindIndex(lines, line => line.Trim() == stepName);
        Assert.True(stepIndex >= 0, $"Could not find workflow step: {stepName}");
        var scriptIndex = Array.FindIndex(lines, stepIndex, line => line.TrimEnd().EndsWith("script: |", StringComparison.Ordinal));
        Assert.True(scriptIndex >= 0, $"Could not find script block for workflow step: {stepName}");

        var keyIndent = IndentOf(lines[scriptIndex]);
        var body = new List<string>();
        for (var i = scriptIndex + 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Trim().Length == 0)
            {
                body.Add(string.Empty);
                continue;
            }

            if (IndentOf(line) <= keyIndent)
            {
                break;
            }

            body.Add(line);
        }

        while (body.Count > 0 && body[^1].Length == 0)
        {
            body.RemoveAt(body.Count - 1);
        }

        Assert.NotEmpty(body);
        var minIndent = body.Where(line => line.Length > 0).Min(IndentOf);
        return string.Join('\n', body.Select(line => line.Length >= minIndent ? line[minIndent..] : line));
    }

    private static int IndentOf(string line) => line.Length - line.TrimStart().Length;

    private async Task WritePersistenceFixtureAsync(
        string analysis,
        string runContext,
        string run,
        string trustedFailedJobs,
        string triggeringMerge,
        string lastSuccessfulRun,
        string candidateMerges,
        string prMetadata = "{}")
    {
        var agentDirectory = Directory.CreateDirectory(Path.Combine(_workspace.Path, "agent")).FullName;
        var failureDataDirectory = Directory.CreateDirectory(Path.Combine(_workspace.Path, "ci-failure-data")).FullName;
        await File.WriteAllTextAsync(Path.Combine(agentDirectory, "analysis-result.json"), analysis);
        await File.WriteAllTextAsync(Path.Combine(failureDataDirectory, "run-context.json"), runContext);
        await File.WriteAllTextAsync(Path.Combine(failureDataDirectory, "run.json"), run);
        await File.WriteAllTextAsync(Path.Combine(failureDataDirectory, "failed-jobs.json"), trustedFailedJobs);
        await File.WriteAllTextAsync(Path.Combine(failureDataDirectory, "triggering-merge-pr.json"), triggeringMerge);
        await File.WriteAllTextAsync(Path.Combine(failureDataDirectory, "last-successful-main-run.json"), lastSuccessfulRun);
        await File.WriteAllTextAsync(Path.Combine(failureDataDirectory, "candidate-merges.json"), candidateMerges);
        await File.WriteAllTextAsync(Path.Combine(failureDataDirectory, "pr-metadata.json"), prMetadata);
    }

    private Task<CommandResult> RunPersistenceScriptAsync(string command, string? outputPath = null)
    {
        var arguments = new List<string>
        {
            command,
            Path.Combine(_workspace.Path, "agent", "analysis-result.json"),
        };
        if (outputPath is not null)
        {
            arguments.Add(outputPath);
            arguments.Add("2026-08-31T12:00:00Z");
        }

        return RunBashScriptAsync(
            Path.Combine(RepoRoot.Path, PersistenceScriptRelativePath),
            arguments,
            new Dictionary<string, string>
            {
                ["CI_FAILURE_DATA_DIR"] = Path.Combine(_workspace.Path, "ci-failure-data"),
            });
    }

    private async Task<CommandResult> RunBashScriptAsync(
        string scriptPath,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string>? environment = null)
        => await RunProcessAsync("bash", [scriptPath, .. arguments], environment);

    private async Task<CommandResult> RunProcessAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string>? environment = null,
        string? standardInput = null)
    {
        using var process = new Process();
        process.StartInfo.FileName = fileName;
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }
        process.StartInfo.WorkingDirectory = _workspace.Path;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardInput = standardInput is not null;
        process.StartInfo.UseShellExecute = false;
        if (environment is not null)
        {
            foreach (var (name, value) in environment)
            {
                process.StartInfo.Environment[name] = value;
            }
        }

        process.Start();
        if (standardInput is not null)
        {
            await process.StandardInput.WriteAsync(standardInput);
            process.StandardInput.Close();
        }
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await process.WaitForExitAsync(timeout.Token);

        return new CommandResult(process.ExitCode, await stdoutTask + await stderrTask);
    }

    private async Task WriteValidationFixtureAsync(
        string analysis,
        string runContext,
        string trustedFailedJobs,
        string? causeFileName = null,
        string? cause = null)
    {
        var agentDirectory = Path.Combine(_workspace.Path, "agent");
        var failureDataDirectory = Path.Combine(_workspace.Path, "ci-failure-data");
        Directory.CreateDirectory(agentDirectory);
        Directory.CreateDirectory(failureDataDirectory);

        await File.WriteAllTextAsync(Path.Combine(agentDirectory, "analysis-result.json"), analysis);
        await File.WriteAllTextAsync(Path.Combine(failureDataDirectory, "run-context.json"), runContext);
        await File.WriteAllTextAsync(Path.Combine(failureDataDirectory, "failed-jobs.json"), trustedFailedJobs);
        if (causeFileName is not null && cause is not null)
        {
            await WriteCauseFilesAsync(new Dictionary<string, string> { [causeFileName] = cause });
        }
    }

    private async Task WriteValidationFixtureAsync(
        string analysis,
        string runContext,
        string trustedFailedJobs,
        IReadOnlyDictionary<string, string> causes)
    {
        await WriteValidationFixtureAsync(analysis, runContext, trustedFailedJobs);
        await WriteCauseFilesAsync(causes);
    }

    private async Task WriteCauseFilesAsync(IReadOnlyDictionary<string, string> causes)
    {
        var causesDirectory = Directory.CreateDirectory(Path.Combine(_workspace.Path, "agent", "causes")).FullName;
        foreach (var (fileName, cause) in causes)
        {
            await File.WriteAllTextAsync(Path.Combine(causesDirectory, fileName), cause);
        }
    }

    private async Task AssertValidationRejectsMismatchedCausePresenceAsync()
    {
        var result = await RunValidationScriptAsync(Path.Combine(_workspace.Path, "output.json"));

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(
            "::error::Failed-job classifications and persisted cause types do not match",
            result.Output,
            StringComparison.Ordinal);
    }

    private sealed record CommandResult(int ExitCode, string Output);

    private sealed record RerunHarnessResult(string[] Failed, int[] Reruns, string[] Infos, string[] Warnings);
}
