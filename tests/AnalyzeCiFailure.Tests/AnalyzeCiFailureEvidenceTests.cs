// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using AnalyzeCiFailure;
using Aspire.TestUtilities;
using VerifyXunit;
using Xunit;

namespace Infrastructure.Tests;

/// <summary>Verifies trusted evidence construction, immutable facts and rejection of unsupported authority.</summary>
public sealed partial class AnalyzeCiFailureWorkflowTests
{
    [Theory]
    [InlineData("""{"test":"Test","display_test":"Test","job":"Tests","error":false,"stack_trace":"","standard_output":"","standard_error":""}""")]
    [InlineData("""{"test":"Test","display_test":"Test","job":"Tests","error":"","stack_trace":[],"standard_output":"","standard_error":""}""")]
    public void OwnedEvidenceContractsRejectNonTextDiagnostics(string json)
    {
        Assert.ThrowsAny<JsonException>(() =>
            CiFailureJson.ToContract<TestFailureEvidence>(CiFailureJson.Parse(json)));
    }

    [Fact]
    public void OptionalUpstreamMetadataKeepsLocalTextTolerance()
    {
        var metadata = CiFailureJson.ToContract<MergedPullRequest>(
            CiFailureJson.Parse("""{"number":42,"title":false,"user":{"login":[]},"head":{"ref":false}}"""))!;

        Assert.Null(metadata.Title);
        Assert.Null(metadata.User!.Login);
        Assert.Null(metadata.Head!.Ref);
    }

    [Theory]
    [InlineData("""{"id":"debugger-timeout","type":"flaky-test","test_name":false}""")]
    [InlineData("""{"id":false,"type":"flaky-test","test_name":null}""")]
    public void StoredCauseIdentityRetainsLocalOptionalTextTolerance(string prior)
    {
        var identity = CiFailureJson.ToContract<StoredCauseIdentity>(CiFailureJson.Parse(prior))!;
        Assert.Equal(CauseType.FlakyTest, identity.Type);
        Assert.Null(identity.TestName);
        Assert.Equal(prior.Contains("\"id\":false", StringComparison.Ordinal) ? null : "debugger-timeout", identity.Id);
    }

    [Fact]
    public async Task BoundReportUsesCollectedDiagnosticsAndDiscardsProducerProvenanceShapes()
    {
        await WriteGroupedFixtureAsync();
        var directory = Path.Combine(_workspace.Path, "ci-analysis-output");
        var file = Path.Combine(directory, "analysis-result.json");
        var proposal = JsonNode.Parse(await File.ReadAllTextAsync(file))!;
        var test = proposal["failed_tests"]![0]!;
        test["error"] = "Producer diagnostic";
        test["result_file"] = new JsonObject { ["forged"] = true };
        test["execution_id"] = new JsonArray("invented");
        test["result_format"] = "unsupported";
        await File.WriteAllTextAsync(file, proposal.ToJsonString());

        var accepted = AnalyzeCiFailureFixtures.Validate(_workspace.Path);

        Assert.Collection(
            accepted.Report.Tests,
            bound =>
            {
                Assert.Equal("Tests.Method(value: 1/2)", bound.Name);
                Assert.Equal("Trusted debugger timeout", bound.Error);
                Assert.Equal("results.trx", bound.ResultFile);
                Assert.Equal(TestArtifactFormat.Trx, bound.ResultFormat);
                Assert.Null(bound.ExecutionId);
            },
            bound =>
            {
                Assert.Equal("Tests.Other", bound.Name);
                Assert.Equal("Trusted debugger timeout", bound.Error);
            });
        Assert.True(JsonNode.DeepEquals(proposal, JsonNode.Parse(await File.ReadAllTextAsync(file))));
    }

    [Theory]
    [InlineData("flaky-test\n")]
    [InlineData("flaky\u0000-test")]
    public async Task DecisionProposalRejectsProducerVerdictEvenWhenTextResemblesSupportedVerdict(string verdict)
    {
        await WriteGroupedFixtureAsync();
        var (evidence, proposal) = AnalyzeCiFailureFixtures.Read(_workspace.Path);
        var node = JsonNode.Parse(TransientJson.Write(proposal))!;
        node["verdict"] = verdict;
        var result = await RunStrictValidationAsync(TransientJson.Write(evidence), node.ToJsonString());
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Cannot decode", result.Output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("absent")]
    [InlineData("null")]
    [InlineData("populated")]
    public async Task TestEvidenceContractRoundTripsExactIdentityAndOptionalProvenance(string provenance)
    {
        const string exactName = "Tests.Method(value: \"one/two\", framework: \"net11.0\")";
        var source = new JsonObject
        {
            ["test"] = exactName,
            ["job"] = "Tests",
            ["job_id"] = JsonNode.Parse("9007199254740993.0"),
            ["error"] = "Connection timed out",
            ["stack_trace"] = "frame\r\nnext",
            ["standard_output"] = "stdout",
            ["standard_error"] = "stderr",
        };
        if (provenance != "absent")
        {
            source["result_file"] = provenance == "populated" ? "00001.trx" : null;
            source["result_format"] = provenance == "populated" ? "trx" : null;
            source["execution_id"] = provenance == "populated" ? "execution-one" : null;
        }
        source["display_test"] = exactName;
        var evidence = new[] { CiFailureJson.ToContract<TestFailureEvidence>(DiagnosticTextSanitizer.SanitizeJson(source, int.MaxValue, multiline: true))! };
        var file = Path.Combine(_workspace.Path, "typed-test-failures.json");

        CiFailureJson.WriteContract(file, evidence);
        var restored = CiFailureJson.ReadContract<ImmutableArray<TestFailureEvidence>>(file);

        var test = Assert.Single(restored);
        Assert.Equal(exactName, test.Name);
        Assert.Equal("9007199254740993.0", test.JobId!.Value.GetRawText());
        Assert.Equal("Connection timed out", test.Error);
        Assert.Equal("frame\nnext", test.StackTrace);
        Assert.Equal("stdout", test.StandardOutput);
        Assert.Equal("stderr", test.StandardError);
        Assert.Equal(provenance == "populated" ? "00001.trx" : null, test.ResultFile);
        Assert.Equal(provenance == "populated" ? TestArtifactFormat.Trx : (TestArtifactFormat?)null, test.ResultFormat);
        Assert.Equal(provenance == "populated" ? "execution-one" : null, test.ExecutionId);
        await Verifier.Verify(await File.ReadAllTextAsync(file), "json").UseDirectory("Snapshots");
    }

    [Fact]
    public async Task AcceptedAnalysisProjectsOnlyDiagnosisFieldsFromProposal()
    {
        await WriteGroupedFixtureAsync();
        var directory = Path.Combine(_workspace.Path, "ci-analysis-output");
        var proposalPath = Path.Combine(directory, "proposal.json");
        var (evidence, proposal) = AnalyzeCiFailureFixtures.Read(_workspace.Path);
        var text = TransientJson.Write(proposal);
        await File.WriteAllTextAsync(proposalPath, text);
        var accepted = AnalysisValidator.Validate(evidence, proposal, "microsoft/aspire");
        var output = Path.Combine(_workspace.Path, "accepted.json");
        CiFailureJson.WriteContract(output, accepted.Report);
        var projection = JsonNode.Parse(await File.ReadAllTextAsync(output))!.AsObject();

        Assert.Equal(["verdict", "failed_jobs", "failed_tests", "causes"], projection.Select(field => field.Key));
        Assert.Equal("Trusted debugger timeout", projection["failed_tests"]![0]!["error"]!.GetValue<string>());
        Assert.Equal(text, await File.ReadAllTextAsync(proposalPath));
    }

    [Theory]
    [InlineData("result_file", "123")]
    [InlineData("result_format", "[]")]
    [InlineData("execution_id", "false")]
    [InlineData("observation_id", "{}")]
    public async Task ValidationRejectsNonTextTestProvenance(string field, string value)
    {
        await WriteGroupedFixtureAsync();
        var (evidence, proposal) = AnalyzeCiFailureFixtures.Read(_workspace.Path);
        var document = JsonNode.Parse(TransientJson.Write(evidence))!;
        var test = document["test_evidence"]!["failures"]![0]!;
        if (field == "observation_id")
        {
            test["ref"] = JsonNode.Parse(value);
        }
        else
        {
            test["sources"]![0]![field == "result_format" ? "format" : field] = JsonNode.Parse(value);
        }
        Assert.Throws<ValidationException>(() => AnalysisValidator.Validate(
            TransientJson.Read<EvidenceBundle>(document.ToJsonString()),
            proposal,
            "microsoft/aspire"));
    }

    [Fact]
    public async Task AcceptedAnalysisKeepsDiagnosticsAndDefinitionsWhenWorkingValuesAreReplaced()
    {
        await WriteGroupedFixtureAsync();
        var accepted = AnalyzeCiFailureFixtures.Validate(_workspace.Path);
        var modified = accepted with
        {
            Report = accepted.Report with { Verdict = AnalysisVerdict.CodeIssue },
            Tests = accepted.Tests.SetItem(index: 0, accepted.Tests[0] with { Error = "Replaced captured diagnostic" }),
            Causes = accepted.Causes.SetItem(
                index: 0,
                accepted.Causes[0] with
                {
                    Definition = accepted.Causes[0].Definition! with
                    {
                        Title = "Replaced cause title",
                        ErrorPattern = "Replaced cause pattern",
                    },
                }),
        };

        Assert.Equal(AnalysisVerdict.CodeIssue, modified.Report.Verdict);
        var cause = Assert.Single(accepted.Causes);
        var occurrence = AnalysisPublisher.CreateOccurrence(
            accepted,
            cause,
            JsonValue.Create(value: 123)!,
            "2026-10-21T01:00:00Z");
        Assert.Equal(AnalysisVerdict.FlakyTest, accepted.Report.Verdict);
        Assert.Equal("Trusted debugger timeout", accepted.Report.Tests[0].Error);
        Assert.Equal("Debugger timeout", cause.Definition!.Title);
        Assert.Equal("Connection timed out", cause.Definition!.ErrorPattern);
        Assert.Collection(
            occurrence["tests"]!.AsArray(),
            test => Assert.Equal("Trusted debugger timeout", test!["error"]!.GetValue<string>()),
            test => Assert.Equal("Trusted debugger timeout", test!["error"]!.GetValue<string>()));
    }

    [Fact]
    public async Task ValidationDoesNotReadOptionalPublicationMetadata()
    {
        await WriteGroupedFixtureAsync();
        await File.WriteAllTextAsync(Path.Combine(EvidenceDirectory, "diagnostics/pr-metadata.json"), "not JSON");

        var accepted = AnalyzeCiFailureFixtures.Validate(_workspace.Path);

        Assert.Equal(AnalysisVerdict.FlakyTest, accepted.Report.Verdict);
        Assert.Single(accepted.Causes);
    }

    [Fact]
    [RequiresTools(["bash", "jq", "git"])]
    public async Task DestinationRejectionPrecedesOptionalMetadataParsing()
    {
        await WriteGroupedFixtureAsync();
        await File.WriteAllTextAsync(Path.Combine(EvidenceDirectory, "diagnostics/pr-metadata.json"), "not JSON");
        await File.WriteAllTextAsync(
            Path.Combine(_workspace.Path, "issue-details.json"),
            new JsonObject
            {
                ["number"] = 77,
                ["state"] = "open",
                ["locked"] = true,
                ["body"] = ExistingGroupedIssue,
                ["labels"] = new JsonArray(new JsonObject { ["name"] = "ci-failure-cause" }),
            }.ToJsonString());

        var result = await RunSuccessfulGroupedPublicationAsync(body: ExistingGroupedIssue);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("destination is locked", result.Output, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_workspace.Path, "memory-repo", "runs", "123.json")));
        var branch = await RunProcessAsync(
            "git",
            ["--no-pager", "--git-dir", "remote.git",
            "show-ref", "--verify", "--quiet", "refs/heads/memory/ci-failure-analysis"]);
        Assert.Equal(1, branch.ExitCode);
    }

    [Fact]
    [RequiresTools(["bash", "jq", "git"])]
    public async Task PureCodeIssuePublicationDoesNotReadOptionalMetadata()
    {
        await WriteGroupedFixtureAsync();
        var analysisDirectory = Path.Combine(_workspace.Path, "ci-analysis-output");
        var summaryPath = Path.Combine(analysisDirectory, "analysis-result.json");
        var summary = JsonNode.Parse(await File.ReadAllTextAsync(summaryPath))!;
        summary["verdict"] = "code-issue";
        summary["causes"] = new JsonArray();
        summary["failed_jobs"]![0]!["classification"] = "code-issue";
        foreach (var test in summary["failed_tests"]!.AsArray())
        {
            test!["classification"] = "code-issue";
        }
        await File.WriteAllTextAsync(summaryPath, summary.ToJsonString());
        File.Delete(Path.Combine(analysisDirectory, "causes", "debugger-timeout.json"));
        foreach (var name in new[] { "pr-metadata.json", "triggering-merge-pr.json", "last-successful-main-run.json",
            "candidate-merge-history-status.json", "candidate-merges.json" })
        {
            await File.WriteAllTextAsync(Path.Combine(EvidenceDirectory, name), "not JSON");
        }

        var result = await RunSuccessfulGroupedPublicationAsync();

        Assert.True(result.ExitCode == 0, result.Output);
        Assert.StartsWith(
            "<!-- analyze-ci-failure -->\n",
            await File.ReadAllTextAsync(Path.Combine(_workspace.Path, "pr-comment.md")),
            StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(_workspace.Path, "memory-repo", "runs")));
        var branch = await RunProcessAsync(
            "git",
            ["--no-pager", "--git-dir", "remote.git",
            "show-ref", "--verify", "--quiet", "refs/heads/memory/ci-failure-analysis"]);
        Assert.Equal(1, branch.ExitCode);
    }

    [Fact]
    [RequiresTools(["bash", "jq", "git"])]
    public async Task PublicationUsesValidatedEvidenceAfterSourceFilesChange()
    {
        await WriteGroupedFixtureAsync();
        await File.WriteAllTextAsync(Path.Combine(_workspace.Path, "invalidate-evidence-on-lookup"), "");

        var result = await RunSuccessfulGroupedPublicationAsync();

        Assert.True(result.ExitCode == 0, result.Output);
        var history = await RunProcessAsync(
            "git",
            ["--no-pager", "--git-dir", "remote.git",
            "show", "refs/heads/memory/ci-failure-analysis:runs/123.json"]);
        Assert.True(history.ExitCode == 0, history.Output);
        var projection = new JsonObject
        {
            ["summary"] = JsonNode.Parse(history.Output),
            ["issue"] = await File.ReadAllTextAsync(Path.Combine(_workspace.Path, "issue-body.md")),
            ["comment"] = await File.ReadAllTextAsync(Path.Combine(_workspace.Path, "pr-comment.md")),
        };
        await Verifier.Verify(
            projection.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }),
            "json").UseDirectory("Snapshots");
    }

    [Theory]
    [InlineData("incomplete")]
    [InlineData("unavailable")]
    public async Task SummaryDoesNotReadCandidatesWhenHistoryIsNotAvailable(string state)
    {
        await WritePersistenceFixtureAsync(
            """{"verdict":"main-repository-breakage","failed_jobs":[],"failed_tests":[],"causes":[]}""",
            """{"run_id":123,"run_scope":"main","head_sha":"failed"}""",
            """{"html_url":"https://github.com/microsoft/aspire/actions/runs/123"}""",
            """[{"id":123,"name":"Build"}]""",
            "{}",
            "{}",
            "not JSON",
            candidateHistoryStatus: $$"""{"state":"{{state}}"}""");

        var output = Path.Combine(_workspace.Path, "summary.json");
        var result = await RunSummaryAsync(output);

        Assert.True(result.ExitCode == 0, result.Output);
        var summary = JsonNode.Parse(await File.ReadAllTextAsync(output))!;
        Assert.Null(summary["main_context"]!["candidate_merges"]);
        Assert.Equal(state, summary["main_context"]!["candidate_merge_history_state"]!.GetValue<string>());
    }

    [Fact]
    public async Task MainSummaryContractPreservesExactNumbersAndCompleteContext()
    {
        await WritePersistenceFixtureAsync(
            """
            {
              "verdict":"main-repository-breakage",
              "failed_jobs":[{"id":1.23e2,"classification":"main-repository-breakage","reason":"Build failed"}],
              "failed_tests":[{"name":"Test","job":"Build","job_id":123.0,"error":"Failure","result_file":null}],
              "causes":["build-break"]
            }
            """,
            """{"run_id":9007199254740993,"run_attempt":1e1,"run_scope":"main","head_sha":"failed","unknown":false}""",
            """{"html_url":"https://github.com/microsoft/aspire/actions/runs/9007199254740993","unknown":{"preserve":"captured payload"}}""",
            """[{"id":123.0,"name":"Build","conclusion":"failure","unknown":true,"steps":[{"name":"Build","conclusion":"failure"}]}]""",
            """{"number":4.2e1,"title":"Merge title","user":{"login":"author","unknown":true},"head":{"ref":"feature"},"base":{"ref":"main"},"merged_at":"2026-10-21T01:00:00Z"}""",
            """{"head_sha":"success","unknown":false}""",
            """[{"sha":"commit","message":"Candidate","html_url":"https://github.com/microsoft/aspire/commit/commit","pull_request":{"number":42,"title":"Candidate PR","url":"https://github.com/microsoft/aspire/pull/42"}}]""");
        var capture = Path.Combine(EvidenceDirectory, "diagnostics/run.json");
        var original = await File.ReadAllTextAsync(capture);
        var output = Path.Combine(_workspace.Path, "summary.json");

        var result = await RunSummaryAsync(output);

        Assert.True(result.ExitCode == 0, result.Output);
        Assert.Equal(original, await File.ReadAllTextAsync(capture));
        await Verifier.Verify(await File.ReadAllTextAsync(output), "json").UseDirectory("Snapshots");
    }

    [Fact]
    public async Task EvidenceContractsNormalizeDuplicatePropertiesAndIgnoreUnknownFields()
    {
        var file = Path.Combine(_workspace.Path, "jobs.json");
        const string json = """[{"id":123.0,"name":"Old","na\u006de":"Tests","steps":false,"unknown":{"nested":[1,2]}}]""";
        await File.WriteAllTextAsync(file, json);

        var jobs = CiFailureJson.ReadContract<WorkflowJob[]>(file)!;

        var job = Assert.Single(jobs);
        Assert.Equal("Tests", job.Name);
        Assert.Equal("123.0", job.Id!.Value.GetRawText());
        Assert.Equal(json, await File.ReadAllTextAsync(file));
    }

    [Fact]
    public async Task SummaryTreatsNonObjectOptionalMetadataAsUnavailable()
    {
        await WritePersistenceFixtureAsync(
            """{"verdict":"main-repository-breakage","failed_jobs":[],"failed_tests":[],"causes":[]}""",
            """{"run_id":123,"run_scope":"main","head_sha":"failed"}""",
            """{"html_url":"https://github.com/microsoft/aspire/actions/runs/123"}""",
            """[{"id":123,"name":"Build"}]""",
            """{"number":42,"user":false,"head":[],"base":"not an object"}""",
            "{}",
            """[{"sha":"candidate","pull_request":false}]""");
        var output = Path.Combine(_workspace.Path, "summary.json");

        var result = await RunSummaryAsync(output);

        Assert.True(result.ExitCode == 0, result.Output);
        await Verifier.Verify(await File.ReadAllTextAsync(output), "json").UseDirectory("Snapshots");
    }

    [Theory]
    [InlineData("pull-request", "available")]
    [InlineData("main", "unavailable")]
    public async Task SummaryIgnoresNonObjectNestedMetadataOutsideTriggeringMergeGate(string scope, string state)
    {
        await WritePersistenceFixtureAsync(
            """{"verdict":"transient-infra","failed_jobs":[],"failed_tests":[],"causes":[]}""",
            $$"""{"run_id":123,"run_scope":"{{scope}}"}""",
            """{"html_url":"https://github.com/microsoft/aspire/actions/runs/123"}""",
            """[{"id":123,"name":"Build"}]""",
            """{"number":42,"user":false,"head":[],"base":"not an object"}""",
            "{}",
            "not JSON",
            candidateHistoryStatus: $$"""{"state":"{{state}}"}""");
        var output = Path.Combine(_workspace.Path, "summary.json");

        var result = await RunSummaryAsync(output);

        Assert.True(result.ExitCode == 0, result.Output);
        var summary = JsonNode.Parse(await File.ReadAllTextAsync(output))!;
        Assert.Null(summary["triggering_merge_pr"]);
    }
}
