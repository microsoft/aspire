// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json.Nodes;
using AnalyzeCiFailure;
using Aspire.TestUtilities;
using VerifyXunit;
using Xunit;

namespace Infrastructure.Tests;

/// <summary>Checks collector policy against independently defined workflow and artifact conventions.</summary>
public sealed partial class AnalyzeCiFailureWorkflowTests
{
    [Fact]
    [RequiresTools(["bash", "git"])]
    public async Task MochaDiagnosticOverflowRemainsBoundedWithoutDroppingLaterCompletedFailures()
    {
        var started = Stopwatch.GetTimestamp();
        var (result, progress) = await CollectAlternateWorkflowAsync(
            new AlternateWorkflowConventions(TestArtifactFormat.Mocha, TestEvidencePolicy.Required),
            "mocha-many-hooks");
        var elapsed = Stopwatch.GetElapsedTime(started);
        var collected = result.Run!;
        var failure = Assert.Single(collected.Evidence.TestEvidence.Failures);

        Assert.Equal(EvidenceState.Complete, collected.Evidence.TestEvidence.State);
        Assert.Equal("Alternate test", failure.Name);
        Assert.Equal("assertion failed", failure.Diagnostics.Error);
        Assert.Empty(progress.Problems);
        Assert.True(elapsed < TimeSpan.FromSeconds(10), $"Collecting 2,000 hook diagnostics took {elapsed}.");

        var diagnostic = collected.Attachments["diagnostics/job-77-mocha.log"].Text;
        var entries = diagnostic.Split("Mocha hook/harness diagnostic: not a completed test failure.\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.True(diagnostic.Length <= 65536 + 150);
        await Verifier.Verify(new
        {
            RetainedEntries = entries.Length,
            FirstEntry = entries[0],
            LastEntry = entries[^1],
        }).UseDirectory("Snapshots");
    }

    [Theory]
    [InlineData("mocha-mixed-hook", 1)]
    [InlineData("mocha-hook-only", 0)]
    [RequiresTools(["bash", "git"])]
    public async Task MochaHookDiagnosticsRemainVisibleWithoutInventingTestFailures(string scenario, int failures)
    {
        var (result, progress) = await CollectAlternateWorkflowAsync(
            new AlternateWorkflowConventions(TestArtifactFormat.Mocha, TestEvidencePolicy.Required),
            scenario);
        var collected = result.Run!;

        Assert.Equal(EvidenceState.Complete, collected.Evidence.TestEvidence.State);
        Assert.Equal(failures, collected.Evidence.TestEvidence.Failures.Length);
        Assert.Empty(progress.Problems);
        if (failures != 0)
        {
            var failure = Assert.Single(collected.Evidence.TestEvidence.Failures);
            Assert.Equal("Alternate test", failure.Name);
            Assert.Equal("assertion failed", failure.Diagnostics.Error);
            var source = Assert.Single(failure.Sources);
            Assert.Equal(10, source.ArtifactId);
            Assert.Equal(TestArtifactFormat.Mocha, source.Format);
        }
        var diagnostic = collected.Attachments["diagnostics/job-77-mocha.log"].Text;
        Assert.Contains("after each hook", diagnostic, StringComparison.Ordinal);
        Assert.Contains("job=77 artifact=10", diagnostic, StringComparison.Ordinal);
        Assert.Contains("token=[REDACTED]", diagnostic, StringComparison.Ordinal);
        Assert.False(diagnostic.Contains("hook-secret", StringComparison.Ordinal));
        var context = WorkflowFailureDiagnosisContextBuilder.Build(collected);
        Assert.Contains("not a completed test failure", context.Summary, StringComparison.Ordinal);
        Assert.Contains("after each hook", context.Summary, StringComparison.Ordinal);
    }

    private readonly Dictionary<string, string> _alternateEnvironment = [];

    [Theory]
    [InlineData("multi-trx-valid", "complete", 3)]
    [InlineData("multi-trx-missing", "unavailable", 2)]
    [InlineData("multi-trx-malformed", "unavailable", 2)]
    [InlineData("multi-trx-interrupted-missing", "unavailable", 2)]
    [InlineData("multi-trx-conflicting", "unavailable", 2)]
    [InlineData("multi-trx-middle-download-failure", "unavailable", 3)]
    [RequiresTools(["bash", "git"])]
    public async Task OneJobCollectsEveryRequiredArtifactAndEveryFrameworkReport(
        string scenario,
        string state,
        int expectedFailures)
    {
        var conventions = new AlternateWorkflowConventions(TestArtifactFormat.Trx, TestEvidencePolicy.Required);
        var (result, progress) = await CollectAlternateWorkflowAsync(conventions, scenario);
        var evidence = result.Run!.Evidence;

        Assert.Equal(state, CiFailureJson.EnumText(evidence.TestEvidence.State));
        Assert.Equal(expectedFailures, evidence.TestEvidence.Failures.Length);
        Assert.Equal(["Alternate tests"], conventions.DescribedJobs);
        if (state == "complete")
        {
            Assert.All(evidence.TestEvidence.Failures, failure => Assert.Equal(77, failure.JobId));
            Assert.Equal(
                ["net10", "net9", "other"],
                evidence.TestEvidence.Failures.Select(failure => failure.Diagnostics.Error).Order(StringComparer.Ordinal));
            var copied = Assert.Single(evidence.TestEvidence.Failures, failure => failure.Diagnostics.Error == "net9");
            Assert.Equal([10L, 11L], copied.Sources.Select(source => source.ArtifactId));
            Assert.Equal(2, evidence.TestEvidence.Failures.Count(failure => failure.Name == "Framework.Case"));
        }
        else
        {
            var problem = Assert.Single(progress.Problems, problem => problem.Category == AnalysisProblemCategory.TestEvidence);
            Assert.Contains("alternate-results-2-net10", problem.Problem.Message, StringComparison.Ordinal);
            Assert.Contains("Alternate tests", problem.Problem.Message, StringComparison.Ordinal);
            Assert.Contains("https://github.com/microsoft/aspire/actions/runs/123", problem.Problem.Message, StringComparison.Ordinal);
            Assert.Contains("attempt=2", problem.Problem.Message, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("missing", "artifacts")]
    [InlineData("download-failure", "artifacts")]
    [InlineData("malformed", "parsing")]
    [RequiresTools(["bash", "git"])]
    public async Task ArtifactFailuresIdentifyRunJobArtifactAndProcessingStage(string scenario, string stage)
    {
        var conventions = new AlternateWorkflowConventions(TestArtifactFormat.Trx, TestEvidencePolicy.Required);
        var (_, progress) = await CollectAlternateWorkflowAsync(conventions, scenario);
        var problem = Assert.Single(progress.Problems, problem => problem.Category == AnalysisProblemCategory.TestEvidence);

        Assert.Equal(stage, problem.Problem.Stage);
        Assert.Contains("Alternate tests", problem.Problem.Message, StringComparison.Ordinal);
        Assert.Contains("alternate-results-2", problem.Problem.Message, StringComparison.Ordinal);
        Assert.Contains("https://github.com/microsoft/aspire/actions/runs/123", problem.Problem.Message, StringComparison.Ordinal);
        Assert.Contains("attempt=2", problem.Problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    [RequiresTools(["bash", "git"])]
    public async Task ArchiveSetupFailureCannotProduceCompleteEmptyEvidence()
    {
        var conventions = new AlternateWorkflowConventions(TestArtifactFormat.Trx, TestEvidencePolicy.Required);
        var (result, progress) = await CollectAlternateWorkflowAsync(conventions, "archive-directory-file");

        Assert.Equal(EvidenceState.Unavailable, result.Run!.Evidence.TestEvidence.State);
        Assert.Empty(result.Run.Evidence.TestEvidence.Failures);
        Assert.Contains(
            progress.Problems,
            problem => problem.Category == AnalysisProblemCategory.TestEvidence &&
            problem.Problem.Stage == "artifacts" &&
            problem.Problem.Message.Contains("https://github.com/microsoft/aspire/actions/runs/123", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Mocha", "Required", "valid", "complete", 1)]
    [InlineData("Mocha", "Required", "missing", "unavailable", 0)]
    [InlineData("Mocha", "Required", "oversized", "unavailable", 0)]
    [InlineData("Mocha", "Required", "download-failure", "unavailable", 0)]
    [InlineData("Mocha", "Required", "empty", "unavailable", 0)]
    [InlineData("Mocha", "Required", "wrong-artifact-run", "unavailable", 0)]
    [InlineData("Trx", "OptionalDiagnostics", "missing", "not-applicable", 0)]
    [InlineData("Trx", "OptionalDiagnostics", "download-failure", "complete", 0)]
    [InlineData("Trx", "OptionalDiagnostics", "empty", "complete", 0)]
    [InlineData("Trx", "OptionalDiagnostics", "malformed", "unavailable", 0)]
    [InlineData("Trx", "OptionalDiagnostics", "wrong-artifact-run", "complete", 0)]
    [InlineData("Mocha", "RequiredForInterruptedExecution", "missing", "unavailable", 0)]
    [InlineData("Mocha", "RequiredForInterruptedExecution", "download-failure", "unavailable", 0)]
    [InlineData("Mocha", "RequiredForInterruptedExecution", "valid", "complete", 1)]
    [InlineData("Mocha", "RequiredForInterruptedExecution", "oversized", "unavailable", 0)]
    [InlineData("Mocha", "RequiredForInterruptedExecution", "empty", "unavailable", 0)]
    [RequiresTools(["bash", "git"])]
    public async Task AlternateWorkflowUsesItsOwnNamesAndIndependentEvidencePolicy(
        string format,
        string policy,
        string scenario,
        string state,
        int failures)
    {
        var adapter = new AlternateWorkflowConventions(Enum.Parse<TestArtifactFormat>(format), Enum.Parse<TestEvidencePolicy>(policy));
        var (result, progress) = await CollectAlternateWorkflowAsync(adapter, scenario);

        Assert.True(result.HasRelevantFailures);
        Assert.Equal(77, Assert.Single(result.Run!.Evidence.Jobs).Id);
        Assert.Equal(state, CiFailureJson.EnumText(result.Run.Evidence.TestEvidence.State));
        Assert.Equal(failures, result.Run.Evidence.TestEvidence.Failures.Length);
        Assert.Equal(["Alternate tests"], adapter.DescribedJobs);
        if (failures != 0)
        {
            var failure = Assert.Single(result.Run.Evidence.TestEvidence.Failures);
            Assert.Equal("Alternate test", failure.Name);
            Assert.Equal(77, failure.JobId);
            Assert.Equal("assertion failed", failure.Diagnostics.Error);
        }
        if (state == "unavailable")
        {
            Assert.Contains(
                progress.Problems,
                problem => problem.Category == AnalysisProblemCategory.TestEvidence &&
                problem.Problem.Stage is "artifacts" or "parsing" && problem.Problem.Message.Length > 0);
        }
    }

    [Theory]
    [InlineData("optional-download-failure", "complete", 1, "OptionalContext", "artifacts", "Unable to download")]
    [InlineData("optional-malformed", "unavailable", 1, "TestEvidence", "parsing", "Cannot parse test result")]
    [InlineData("interrupted-missing", "unavailable", 1, "TestEvidence", "artifacts", "unavailable")]
    [RequiresTools(["bash", "git"])]
    public async Task AlternateMixedJobsPreserveRequiredEvidenceUnlessAnotherOutcomeIsUnknown(
        string scenario,
        string state,
        int failures,
        string category,
        string stage,
        string message)
    {
        var adapter = new AlternateWorkflowConventions(TestArtifactFormat.Mocha, TestEvidencePolicy.Required);
        var (result, progress) = await CollectAlternateWorkflowAsync(adapter, scenario);

        Assert.Equal(state, CiFailureJson.EnumText(result.Run!.Evidence.TestEvidence.State));
        Assert.Equal(failures, result.Run.Evidence.TestEvidence.Failures.Length);
        Assert.Equal(["Alternate tests", "Secondary tests"], adapter.DescribedJobs);
        Assert.Contains(
            progress.Problems,
            problem => problem.Category.ToString() == category &&
            problem.Problem.Stage == stage && problem.Problem.Message.Contains(message, StringComparison.Ordinal));
    }

    [Fact]
    [RequiresTools(["bash", "git"])]
    public async Task AlternateConventionsFailureDegradesWithItsOriginalDiagnostic()
    {
        var adapter = new AlternateWorkflowConventions(TestArtifactFormat.Mocha, TestEvidencePolicy.Required)
        {
            RejectDescriptions = true,
        };
        var (result, progress) = await CollectAlternateWorkflowAsync(adapter, "valid");

        Assert.Equal(EvidenceState.Unavailable, result.Run!.Evidence.TestEvidence.State);
        Assert.Contains(
            "Alternate tests have an invalid artifact convention",
            Assert.Single(progress.Problems, problem => problem.Problem.Stage == "artifacts").Problem.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    [RequiresTools(["bash", "git"])]
    public async Task AlternateConventionsRejectAnotherWorkflowPath()
    {
        var adapter = new AlternateWorkflowConventions(TestArtifactFormat.Mocha, TestEvidencePolicy.Required);
        var error = await Assert.ThrowsAsync<ValidationException>(() => CollectAlternateWorkflowAsync(adapter, "wrong-path"));

        Assert.Contains("'.github/workflows/ci.yml', not '.github/workflows/alternate.yml'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    [RequiresTools(["bash", "git"])]
    public async Task AlternateConventionsCannotRebindCollectedJobIdentity()
    {
        var adapter = new AlternateWorkflowConventions(TestArtifactFormat.Mocha, TestEvidencePolicy.Required);
        adapter.AdditionalJobs["Alternate tests"] = new TestArtifactExpectation(
            "Unrelated job",
            "alternate-results-2",
            TestArtifactFormat.Mocha,
            TestEvidencePolicy.Required);

        var (result, progress) = await CollectAlternateWorkflowAsync(adapter, "valid");

        Assert.Equal(EvidenceState.Unavailable, result.Run!.Evidence.TestEvidence.State);
        Assert.Contains(
            "Workflow conventions cannot change a collected test job's identity",
            Assert.Single(progress.Problems, problem => problem.Problem.Stage == "artifacts").Problem.Message,
            StringComparison.Ordinal);
        Assert.Contains(
            "job='Alternate tests'",
            Assert.Single(progress.Problems, problem => problem.Problem.Stage == "artifacts").Problem.Message,
            StringComparison.Ordinal);
        Assert.Contains(
            "returned job='Unrelated job'",
            Assert.Single(progress.Problems, problem => problem.Problem.Stage == "artifacts").Problem.Message,
            StringComparison.Ordinal);
        Assert.Equal("Alternate tests", Assert.Single(result.Run.Evidence.Jobs).Name);
    }

    [Theory]
    [InlineData("schedule", "main")]
    [InlineData("push", "release/13.0")]
    [InlineData("workflow_dispatch", "main")]
    [RequiresTools(["bash", "git"])]
    public async Task AlternateWorkflowCannotBroadenSupportedReportingScopes(string eventName, string branch)
    {
        var adapter = new AlternateWorkflowConventions(TestArtifactFormat.Mocha, TestEvidencePolicy.Required);
        var (result, _) = await CollectAlternateWorkflowAsync(adapter, "valid", eventName, branch);

        Assert.False(result.HasRelevantFailures);
        Assert.Null(result.Run);
        Assert.Empty(adapter.DescribedJobs);
        Assert.Single(await File.ReadAllLinesAsync(Path.Combine(_workspace.Path, "gh-calls.log")));
    }

    [Fact]
    [RequiresTools(["bash", "git"])]
    public async Task AlternateWorkflowMainBaselineUsesTheSelectedWorkflow()
    {
        var adapter = new AlternateWorkflowConventions(TestArtifactFormat.Mocha, TestEvidencePolicy.Required);
        var (result, _) = await CollectAlternateWorkflowAsync(adapter, "valid", "push", "main");

        Assert.Equal(RunScope.Main, result.Run!.Evidence.Run.Scope);
        Assert.Equal(100, result.Run.Evidence.MainContext!.LastSuccessfulRun!.Id);
        Assert.Contains(
            await File.ReadAllLinesAsync(Path.Combine(_workspace.Path, "gh-calls.log")),
            call => call.Contains("actions/workflows/93/runs", StringComparison.Ordinal));
    }

    [Fact]
    [RequiresTools(["bash", "git"])]
    public async Task DefaultCompositionDoesNotAdmitAlternateWorkflow()
    {
        await CollectAlternateWorkflowAsync(
            new AlternateWorkflowConventions(TestArtifactFormat.Mocha, TestEvidencePolicy.Required),
            "valid");
        var result = await RunValidatorCommandAsync(
            ["collect", "--repo", "microsoft/aspire", "--working-dir", _workspace.Path, "--run-id", "123", "--run-attempt", "2"],
            _alternateEnvironment);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("not '.github/workflows/ci.yml'", result.Output, StringComparison.Ordinal);
    }

    private async Task<(EvidenceCollectionResult Result, AnalysisProgress Progress)> CollectAlternateWorkflowAsync(
        AlternateWorkflowConventions adapter,
        string scenario,
        string eventName = "pull_request",
        string branch = "feature")
    {
        var responses = Directory.CreateDirectory(Path.Combine(_workspace.Path, "responses")).FullName;
        var zip = Path.Combine(responses, "alternate.zip");
        var rejectsDescriptions = adapter.RejectDescriptions;
        var plan = rejectsDescriptions
            ? new TestArtifactExpectation("Alternate tests", "alternate-results-2", TestArtifactFormat.Mocha, TestEvidencePolicy.Required)
            : Assert.Single(adapter.DescribeTestArtifacts(new JsonObject { ["name"] = "Alternate tests" }, runAttempt: 2));
        adapter.DescribedJobs.Clear();
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            if (scenario != "empty")
            {
                var name = plan.Format == TestArtifactFormat.Mocha ? "mocha.json" : "results.trx";
                await WriteZipEntryAsync(
                    archive,
                    name,
                    scenario == "malformed" ? "malformed" :
                    scenario == "mocha-many-hooks" ? CreateMochaReportWithManyHooks() :
                    scenario is "mocha-mixed-hook" or "mocha-hook-only"
                    ? $$$"""
                        {"tests":[{"fullTitle":"Alternate test"}],"failures":[
                        {{{(scenario == "mocha-mixed-hook" ? """{"fullTitle":"Alternate test","err":{"name":"AssertionError","message":"assertion failed"}}, """ : "")}}}
                        {"fullTitle":"after each hook","err":{"name":"Error","message":"teardown failed; token=hook-secret"}}]}
                        """ :
                    plan.Format == TestArtifactFormat.Mocha
                    ? """{"tests":[{"fullTitle":"Alternate test"}],"failures":[{"fullTitle":"Alternate test","err":{"message":"assertion failed"}}]}"""
                    : """<TestRun><Results><UnitTestResult testName="Alternate test" outcome="Failed"><Output><ErrorInfo><Message>assertion failed</Message></ErrorInfo></Output></UnitTestResult></Results></TestRun>""");
            }
        }
        await File.WriteAllTextAsync(
            Path.Combine(responses, "run.json"),
            $$"""
            {"id":123,"run_attempt":2,"path":"{{(scenario == "wrong-path" ? ".github/workflows/ci.yml" : ".github/workflows/alternate.yml")}}",
             "event":"{{eventName}}","head_branch":"{{branch}}","head_sha":"failed-sha",
             "workflow_id":93,"created_at":"2026-09-04T12:00:00Z",
             "html_url":"https://github.com/microsoft/aspire/actions/runs/123",
             "conclusion":"failure","pull_requests":[],"run_started_at":"2026-09-04T12:00:00Z",
             "updated_at":"2026-09-04T12:02:00Z"}
            """);
        await File.WriteAllTextAsync(
            Path.Combine(responses, "jobs.json"),
            """
            [{"jobs":[
              {"id":77,"name":"Alternate tests","conclusion":"failure","steps":[
                {"number":1,"name":"Check dependent jobs failed","conclusion":"failure"}]},
              {"id":78,"name":"Alternate gate","conclusion":"failure","steps":[]}]}]
            """);
        var size = scenario == "oversized" ? 104857601 : new FileInfo(zip).Length;
        await File.WriteAllTextAsync(
            Path.Combine(responses, "artifacts.json"),
            scenario == "missing" ? """[{"artifacts":[]}]""" : $$$"""
            [{"artifacts":[{"id":10,"name":"alternate-results-2","expired":false,
            "created_at":"2026-09-04T12:01:00Z","size_in_bytes":{{{size}}},
            "workflow_run":{"id":123,"head_sha":"failed-sha"}}]}]
            """);
        if (scenario.StartsWith("multi-trx-", StringComparison.Ordinal))
        {
            adapter.AdditionalArtifacts.Add(new TestArtifactExpectation(
                "Alternate tests",
                "alternate-results-2-net10",
                TestArtifactFormat.Trx,
                scenario == "multi-trx-interrupted-missing"
                    ? TestEvidencePolicy.RequiredForInterruptedExecution : TestEvidencePolicy.Required));
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Update))
            {
                foreach (var entry in archive.Entries.ToArray())
                {
                    entry.Delete();
                }
                await WriteZipEntryAsync(archive, "net9/results.trx", FrameworkResult("Framework.Case", "net9", "net9"));
                await WriteZipEntryAsync(archive, "net10/results.trx", FrameworkResult("Framework.Case", "net10", "net10"));
            }
            var pages = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(responses, "artifacts.json")))!;
            pages[0]!["artifacts"]![0]!["size_in_bytes"] = new FileInfo(zip).Length;
            if (scenario is not ("multi-trx-missing" or "multi-trx-interrupted-missing"))
            {
                var secondZip = Path.Combine(responses, "secondary.zip");
                using (var archive = ZipFile.Open(secondZip, ZipArchiveMode.Create))
                {
                    await WriteZipEntryAsync(archive, "copy/results.trx", FrameworkResult(
                        "Framework.Case",
                        "net9",
                        scenario == "multi-trx-conflicting" ? "conflicting diagnostic" : "net9"));
                    await WriteZipEntryAsync(
                        archive,
                        "other/results.trx",
                        scenario == "multi-trx-malformed"
                        ? "malformed" : FrameworkResult("Framework.Other", "other", "other"));
                }
                pages[0]!["artifacts"]!.AsArray().Add(new JsonObject
                {
                    ["id"] = 11,
                    ["name"] = "alternate-results-2-net10",
                    ["expired"] = false,
                    ["created_at"] = "2026-09-04T12:01:00Z",
                    ["size_in_bytes"] = new FileInfo(secondZip).Length,
                    ["workflow_run"] = new JsonObject { ["id"] = 123, ["head_sha"] = "failed-sha" },
                });
            }
            if (scenario == "multi-trx-middle-download-failure")
            {
                adapter.AdditionalArtifacts.Add(new TestArtifactExpectation(
                    "Alternate tests", "alternate-results-2-last", TestArtifactFormat.Trx, TestEvidencePolicy.Required));
                var finalZip = Path.Combine(responses, "final.zip");
                using (var archive = ZipFile.Open(finalZip, ZipArchiveMode.Create))
                {
                    await WriteZipEntryAsync(archive, "last.trx", FrameworkResult("Framework.Last", "last", "last"));
                }
                pages[0]!["artifacts"]!.AsArray().Add(new JsonObject
                {
                    ["id"] = 12, ["name"] = "alternate-results-2-last", ["expired"] = false,
                    ["created_at"] = "2026-09-04T12:01:00Z", ["size_in_bytes"] = new FileInfo(finalZip).Length,
                    ["workflow_run"] = new JsonObject { ["id"] = 123, ["head_sha"] = "failed-sha" },
                });
            }
            await File.WriteAllTextAsync(Path.Combine(responses, "artifacts.json"), pages.ToJsonString());
        }
        if (scenario == "wrong-artifact-run")
        {
            var pages = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(responses, "artifacts.json")))!;
            pages[0]!["artifacts"]![0]!["workflow_run"]!["id"] = 999;
            await File.WriteAllTextAsync(Path.Combine(responses, "artifacts.json"), pages.ToJsonString());
        }
        if (scenario is "optional-download-failure" or "optional-malformed" or "interrupted-missing")
        {
            adapter.AdditionalJobs["Secondary tests"] = new TestArtifactExpectation(
                "Secondary tests",
                "secondary-2",
                TestArtifactFormat.Trx,
                scenario == "interrupted-missing" ? TestEvidencePolicy.RequiredForInterruptedExecution : TestEvidencePolicy.OptionalDiagnostics);
            var jobs = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(responses, "jobs.json")))!;
            jobs[0]!["jobs"]!.AsArray().Add(new JsonObject
            {
                ["id"] = 79,
                ["name"] = "Secondary tests",
                ["conclusion"] = "failure",
                ["steps"] = new JsonArray(),
            });
            await File.WriteAllTextAsync(Path.Combine(responses, "jobs.json"), jobs.ToJsonString());
            if (scenario != "interrupted-missing")
            {
                var secondaryZip = Path.Combine(responses, "secondary.zip");
                using (var archive = ZipFile.Open(secondaryZip, ZipArchiveMode.Create))
                {
                    await WriteZipEntryAsync(archive, "results.trx", "malformed");
                }
                var pages = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(responses, "artifacts.json")))!;
                pages[0]!["artifacts"]!.AsArray().Add(new JsonObject
                {
                    ["id"] = 11,
                    ["name"] = "secondary-2",
                    ["expired"] = false,
                    ["created_at"] = "2026-09-04T12:01:00Z",
                    ["size_in_bytes"] = new FileInfo(secondaryZip).Length,
                });
                await File.WriteAllTextAsync(Path.Combine(responses, "artifacts.json"), pages.ToJsonString());
            }
        }
        var fakeGh = await CreateFakeGhAsync($$"""
            #!/usr/bin/env bash
            set -eu
            printf '%s\n' "$*" >> gh-calls.log
            case "$*" in
              *"attempts/2/jobs"*) cat responses/jobs.json ;;
              *"actions/runs/123/attempts/2") cat responses/run.json ;;
              "api --help") printf '%s' '--allow-escape-sequences' ;;
              *"actions/jobs/"*"/logs") printf '%s' 'Error: alternate failure' ;;
              *"commits/failed-sha/pulls"*) printf '%s' '[[]]' ;;
              *"actions/workflows/93/runs"*) printf '%s' '{"total_count":1,"workflow_runs":[{"id":100,"created_at":"2026-09-04T11:00:00Z","head_sha":"previous"}]}' ;;
              *"compare/previous...failed-sha"*) printf '%s' '[{"status":"ahead","total_commits":0,"commits":[]}]' ;;
              *"actions/runs/123/artifacts"*) cat responses/artifacts.json ;;
              *"actions/artifacts/10/zip")
                if [ '{{scenario}}' = download-failure ]; then printf '%s' 'download failed' >&2; exit 23; fi
                cat responses/alternate.zip ;;
              *"actions/artifacts/11/zip")
                if [ '{{scenario}}' = optional-download-failure ] || [ '{{scenario}}' = multi-trx-middle-download-failure ]; then printf '%s' 'download failed' >&2; exit 23; fi
                cat responses/secondary.zip ;;
              *"actions/artifacts/12/zip") cat responses/final.zip ;;
              *) printf 'Unexpected call: %s\n' "$*" >&2; exit 99 ;;
            esac
            """);
        Assert.True(File.Exists(fakeGh));
        _alternateEnvironment["PATH"] = $"{Path.GetDirectoryName(fakeGh)}{Path.PathSeparator}{Environment.GetEnvironmentVariable("PATH")}";
        Assert.Equal(0, (await RunProcessAsync("git", ["--no-pager", "init"])).ExitCode);
        Assert.Equal(0, (await RunProcessAsync("git", ["--no-pager", "init", "--bare", "remote.git"])).ExitCode);
        Assert.Equal(0, (await RunProcessAsync("git", ["--no-pager", "remote", "add", "origin", Path.Combine(_workspace.Path, "remote.git")])).ExitCode);
        var progress = new AnalysisProgress();
        using var scratch = new AnalysisScratchWorkspace();
        if (scenario == "archive-directory-file")
        {
            await File.WriteAllTextAsync(scratch.Archives, "not a directory");
        }
        var collector = new WorkflowRunEvidenceCollector(
            _workspace.Path,
            "microsoft/aspire",
            scratch,
            new InvocationCapture(),
            adapter,
            _alternateEnvironment,
            CreateGitHubClient(_alternateEnvironment));

        return (await collector.CollectAsync(new EvidenceCollectionRequest("microsoft/aspire", RunId: 123, Attempt: 2), progress, CancellationToken.None), progress);
    }

    private static string CreateMochaReportWithManyHooks()
        => new JsonObject
        {
            ["tests"] = new JsonArray(new JsonObject { ["fullTitle"] = "Alternate test" }),
            ["failures"] = new JsonArray(Enumerable.Range(0, 2000).Select(index => new JsonObject
            {
                ["fullTitle"] = $"after each hook {index:D4}",
                ["err"] = new JsonObject
                {
                    ["name"] = "Error",
                    ["message"] = new string('x', 1000) + "; token=hook-secret",
                },
            }).Append(new JsonObject
            {
                ["fullTitle"] = "Alternate test",
                ["err"] = new JsonObject { ["name"] = "AssertionError", ["message"] = "assertion failed" },
            }).ToArray()),
        }.ToJsonString();

    private static string FrameworkResult(string name, string execution, string error)
        => $$"""
            <TestRun><Results><UnitTestResult testName="{{name}}" executionId="{{execution}}" outcome="Failed">
            <Output><ErrorInfo><Message>{{error}}</Message></ErrorInfo></Output></UnitTestResult></Results></TestRun>
            """;
}
