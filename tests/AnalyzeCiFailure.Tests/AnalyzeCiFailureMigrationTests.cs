// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using System.Text.Json;
using System.Text;
using System.Collections.Immutable;
using System.Text.RegularExpressions;
using AnalyzeCiFailure;
using Xunit;

namespace Infrastructure.Tests;

/// <summary>Checks legacy history compatibility, replay recovery and preservation of established failure facts.</summary>
public sealed partial class AnalyzeCiFailureWorkflowTests
{
    [Fact]
    public void ExecutionIdProjectionBoundsDurableMetadataWithoutChangingRawIdentity()
    {
        var execution = new string('x', count: 800);
        var failure = RawFailure("result.trx", execution) with { JobId = 123 };
        var evidence = AnalyzeCiFailureFixtures.CreateEvidence(RunScope.Main);
        evidence = evidence with
        {
            TestEvidence = evidence.TestEvidence with { Failures = TestFailureReader.ConsolidateFailures([failure]) },
        };

        var projected = Assert.Single(PublicationEvidenceProjection.ProjectTestFailures(evidence));

        Assert.Equal(new string('x', count: 500), projected.ExecutionId);
        Assert.Equal(execution, evidence.TestEvidence.Failures[0].Sources[0].ExecutionId);
    }

    [Theory]
    [InlineData("token=fake-execution-secret", "token=[REDACTED]")]
    [InlineData("raw\u001b[31mexecution\r\nid", "rawexecution id")]
    public void ExecutionIdsAreSanitizedOnlyAtThePublicationBoundary(string execution, string expected)
    {
        var first = RawFailure("first.trx", execution) with { JobId = 123 };
        var copied = first with { Source = first.Source with { ArtifactId = 9, ResultFile = "copied.trx" } };
        var failures = TestFailureReader.ConsolidateFailures([first, copied]);
        var evidence = AnalyzeCiFailureFixtures.CreateEvidence(RunScope.Main);
        evidence = evidence with { TestEvidence = evidence.TestEvidence with { Failures = failures } };
        var proposal = AnalyzeCiFailureFixtures.CreateInfrastructureProposal(evidence) with
        {
            Jobs = [new ProposedJobDecision { JobId = 123, Classification = JobClassification.FlakyTest, Reason = "Timeout" }],
            Tests = [new ProposedTestDecision { Ref = "test-1", Classification = TestClassification.Flaky, Reason = "Timeout" }],
            Causes =
            [
                new NewCauseProposal
                {
                    Definition = new ProposedCauseDefinition { Id = "timeout", Type = CauseType.FlakyTest, Title = "Timeout", ErrorPattern = "timeout" },
                    JobIds = [123], TestRefs = ["test-1"], Reason = "Timeout",
                },
            ],
        };

        var accepted = AnalysisValidator.Validate(evidence, proposal, "microsoft/aspire");
        var occurrence = AnalysisPublisher.CreateOccurrence(accepted, Assert.Single(accepted.Causes), JsonValue.Create(value: 123)!, "2026-10-30T16:00:00Z");
        var summary = RunSummaryBuilder.Create(accepted.Report, accepted.Evidence, accepted.CapturedPublication, "2026-10-30T16:00:00Z");

        Assert.Equal(expected, occurrence["tests"]![0]!["execution_id"]!.GetValue<string>());
        Assert.Equal(expected, Assert.Single(summary.Tests).ExecutionId);
        Assert.Equal(execution, Assert.Single(evidence.TestEvidence.Failures).Sources[0].ExecutionId);
        Assert.Equal(2, evidence.TestEvidence.Failures[0].Sources.Length);
    }

    [Fact]
    public async Task MissingSourceStepNumbersFailAfterWorkDiscoveryInsteadOfInventingEvidence()
    {
        var result = await RunCollectionAsync("missing-step-number");
        Assert.Equal(1, result.ExitCode);
        var root = EvidenceDirectory;
        var outcome = TransientJson.Read<AnalysisOutcome>(await File.ReadAllTextAsync(Path.Combine(root, "collection-outcome.json")));
        Assert.Equal("failed", outcome.Status);
        Assert.True(outcome.HasRelevantFailures);
        Assert.Contains(outcome.Problems, problem => problem.Stage == "jobs" && problem.Message.Contains("source number", StringComparison.Ordinal));
        Assert.False(File.Exists(Path.Combine(root, "evidence.json")));
    }

    [Fact]
    public async Task VersionedFilesRejectInvalidUtf8InsteadOfReplacingItWithDisplayText()
    {
        await WriteGroupedFixtureAsync();
        var (evidence, proposal) = AnalyzeCiFailureFixtures.Read(_workspace.Path);
        proposal = proposal with { Jobs = proposal.Jobs.SetItem(index: 0, proposal.Jobs[0] with { Reason = "WIRE-UTF8-MARKER" }) };
        Assert.Equal(0, (await RunStrictValidationAsync(TransientJson.Write(evidence), TransientJson.Write(proposal))).ExitCode);
        var path = Path.Combine(_workspace.Path, "ci-analysis-output", "proposal.json");
        var bytes = Encoding.UTF8.GetBytes(TransientJson.Write(proposal));
        var index = bytes.AsSpan().IndexOf("WIRE-UTF8-MARKER"u8);
        Assert.True(index >= 0);
        bytes[index] = 0xff;
        await File.WriteAllBytesAsync(path, bytes);
        var result = await RunValidatorCommandAsync(["validate", "--repo", "microsoft/aspire", "--working-dir", _workspace.Path,
            "--analysis-dir", Path.Combine(_workspace.Path, "ci-analysis-output")]);
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("UTF-8", result.Output, StringComparison.Ordinal);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HistoricalAttachmentsRemainByteIdenticalAcrossRepreparation(bool readableUtf16)
    {
        var result = await RunCollectionAsync(readableUtf16 ? "stored-history-utf16" : "stored-history-invalid-utf8");
        Assert.True(result.ExitCode == 0, result.Output);
        var root = EvidenceDirectory;
        var name = readableUtf16 ? "evidence.json" : "unreadable.json";
        var path = Path.Combine(root, "history/causes", name);
        Assert.Equal(HistoricalAttachmentBytes(readableUtf16), await File.ReadAllBytesAsync(path));
        var index = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(root, "history/cause-index.json")))!.AsArray();
        var entry = Assert.Single(index, entry => entry!["file"]!.GetValue<string>() == "history/causes/" + name)!;
        if (readableUtf16)
        {
            Assert.Equal("evidence", entry["id"]!.GetValue<string>());
        }
        else
        {
            Assert.StartsWith("Cannot summarize prior cause record: ", entry["read_error"]!.GetValue<string>());
        }
        var bundle = TransientJson.Read<EvidenceBundle>(await File.ReadAllTextAsync(Path.Combine(root, "evidence.json")));
        Assert.Equal(
            readableUtf16 ? new[] { "evidence", "keyless-infra", "opaque-test" } : ["keyless-infra", "opaque-test"],
            bundle.PriorCauseIdentities.Select(cause => cause.Id));
        var regenerated = await RunValidatorCommandAsync(["replay", "--working-dir", _workspace.Path]);
        Assert.True(regenerated.ExitCode == 0, regenerated.Output);
        Assert.Equal(HistoricalAttachmentBytes(readableUtf16), await File.ReadAllBytesAsync(path));
        var regeneratedIndex = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(root, "history/cause-index.json")))!.AsArray();
        Assert.Single(regeneratedIndex, item => item!["file"]!.GetValue<string>() == "history/causes/" + name);
    }

    private static byte[] HistoricalAttachmentBytes(bool utf16) => !utf16 ? [0xff, 0x7b, 0x7d] :
        Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("""{"id":"evidence","type":"infra-failure","title":"Legacy UTF-16","error_pattern":"HTTP 504","custom":{"keep":true}}""")).ToArray();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void EmptyExecutionIdsDoNotEstablishCopiedExecutionIdentity(string? execution)
    {
        var failure = RawFailure("results.trx", execution);
        var failures = TestFailureReader.ConsolidateFailures([failure, failure]);
        Assert.Equal(2, failures.Length);
        Assert.All(failures, test => Assert.Null(test.Sources[0].ExecutionId));
    }

    [Fact]
    public void DurableAmbiguityNumberingUsesTheWholeOrderedListAndOnlyTheFirstSource()
    {
        var unique = RawFailure("unique.trx", execution: null) with { JobId = 123, Name = "A.Unique" };
        var first = RawFailure("first.trx", "first-execution") with { JobId = 123, Name = "Z.Same" };
        var copied = first with { Source = first.Source with { ArtifactId = 9, ResultFile = "copied.trx" } };
        var second = first with { Source = first.Source with { ExecutionId = "second-execution" } };
        var failures = TestFailureReader.ConsolidateFailures([unique, first, copied, second]);
        var evidence = AnalyzeCiFailureFixtures.CreateEvidence(RunScope.PullRequest);
        evidence = evidence with { TestEvidence = evidence.TestEvidence with { Failures = failures } };
        var proposal = AnalyzeCiFailureFixtures.CreateInfrastructureProposal(evidence) with
        {
            Jobs = [new ProposedJobDecision { JobId = 123, Classification = JobClassification.FlakyTest, Reason = "Timeout" }],
            Tests = failures.Select(test => new ProposedTestDecision { Ref = test.Ref, Classification = TestClassification.Flaky, Reason = "Timeout" }).ToImmutableArray(),
            Causes =
            [
                new NewCauseProposal
                {
                    Definition = new ProposedCauseDefinition { Id = "timeouts", Type = CauseType.FlakyTest, Title = "Timeouts", ErrorPattern = "timeout" },
                    JobIds = [123], TestRefs = failures.Select(test => test.Ref).ToImmutableArray(), Reason = "Timeouts",
                },
            ],
        };
        var accepted = AnalysisValidator.Validate(evidence, proposal, "microsoft/aspire");
        var occurrence = AnalysisPublisher.CreateOccurrence(accepted, Assert.Single(accepted.Causes), JsonValue.Create(value: 123)!, "2026-10-21T01:00:00Z");
        Assert.Collection(
            occurrence["tests"]!.AsArray(),
            test => Assert.Null(test!["observation_id"]),
            test =>
            {
                Assert.Equal("observation-2", test!["observation_id"]!.GetValue<string>());
                Assert.Equal("first.trx", test["result_file"]!.GetValue<string>());
                Assert.Equal("first-execution", test["execution_id"]!.GetValue<string>());
                Assert.Equal(
                    new[] { "error", "execution_id", "job", "job_id", "name", "observation_id", "result_file", "result_format", "stack_trace", "standard_error", "standard_output" },
                    test.AsObject().Select(property => property.Key).Order(StringComparer.Ordinal));
            },
            test => Assert.Equal("observation-3", test!["observation_id"]!.GetValue<string>()));
        Assert.Equal(2, failures[1].Sources.Length);
    }

    [Fact]
    public async Task FreshCollectionLeavesPreviousSuccessAndDiagnosticInputsUnchangedBeforeNoWork()
    {
        var root = Directory.CreateDirectory(EvidenceDirectory).FullName;
        var stale = new[] { "evidence.json", "analysis-summary.md", "history/cause-index.json", "job-999.log", "annotations-999.json", "run.json",
            "run-context.json", "collection-summary.json", "test-evidence.json", "test-failures.json" };
        foreach (var name in stale)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(root, name))!);
            await File.WriteAllTextAsync(Path.Combine(root, name), "previous invocation");
        }
        var result = await RunCollectionAsync("healthy");
        Assert.True(result.ExitCode == 0, result.Output);
        Assert.All(stale, name => Assert.Equal("previous invocation", File.ReadAllText(Path.Combine(root, name))));
        Assert.NotEqual(root, EvidenceDirectory);
        var outcome = TransientJson.Read<AnalysisOutcome>(await File.ReadAllTextAsync(Path.Combine(EvidenceDirectory, "collection-outcome.json")));
        Assert.Equal("no-work", outcome.Status);
        Assert.False(outcome.HasRelevantFailures);
    }

    [Theory]
    [InlineData("missing-pr-metadata")]
    [InlineData("wrong-pr-metadata")]
    public async Task OptionalPrMetadataFailureNeverErasesTheKnownSubject(string scenario)
    {
        var result = await RunCollectionAsync(scenario);
        Assert.True(result.ExitCode == 0, result.Output);
        var root = EvidenceDirectory;
        var bundle = TransientJson.Read<EvidenceBundle>(await File.ReadAllTextAsync(Path.Combine(root, "evidence.json")));
        Assert.Equal(42, bundle.SubjectPr!.Number);
        Assert.Null(bundle.SubjectPr.Metadata);
        var outcome = TransientJson.Read<AnalysisOutcome>(await File.ReadAllTextAsync(Path.Combine(root, "collection-outcome.json")));
        Assert.Equal("ready", outcome.Status);
        Assert.Equal(42, outcome.SubjectPrNumber);
        Assert.Contains(outcome.Problems, problem => problem.Stage == "pr-metadata");
    }

    [Fact]
    public async Task DiagnosisProjectionUsesOnlyCapturedValuesAndKeepsPerFieldDiagnosticBounds()
    {
        var evidence = AnalyzeCiFailureFixtures.CreateEvidence(RunScope.PullRequest);
        var attachments = new Dictionary<string, CollectedAttachment>
        {
            ["diagnostics/job-123.log"] = CollectedAttachment.FromText("Captured runner failure"),
        }.ToImmutableDictionary();
        var collected = new CollectedRun(evidence, attachments, [])
        {
            JsonContext = new Dictionary<string, JsonElement>
            {
                ["diagnostics/annotations-123.json"] = JsonSerializer.SerializeToElement(new JsonArray(new JsonObject { ["message"] = new string('a', count: 1005) })),
            }.ToImmutableDictionary(),
        };
        var before = TransientJson.Write(evidence);
        var first = WorkflowFailureDiagnosisContextBuilder.Build(collected);
        var file = Path.Combine(_workspace.Path, "job-123.log");
        await File.WriteAllTextAsync(file, "A later file must not replace captured logs");
        var second = WorkflowFailureDiagnosisContextBuilder.Build(collected);
        Assert.Equal(first.Summary, second.Summary);
        Assert.Contains("Captured runner failure", first.Summary, StringComparison.Ordinal);
        Assert.Equal(before, TransientJson.Write(evidence));
        var annotation = Assert.Single(first.Summary.Split('\n'), line => line.TrimStart().StartsWith("[{\"message\":", StringComparison.Ordinal));
        Assert.Equal(1000, JsonNode.Parse(annotation)![0]!["message"]!.GetValue<string>().Length);
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 2)]
    public void SharedFixtureContextGroupsDiagnosticsWithoutDroppingObservationReferences(bool differentStack, int groups)
    {
        var evidence = AnalyzeCiFailureFixtures.CreateEvidence(RunScope.Main);
        var failures = Enumerable.Range(1, count: 78).Select(index => new CollectedTestFailure
        {
            Ref = $"test-{index}",
            JobId = 123,
            Name = $"Tests.Case{index}",
            DisplayName = $"Tests.Case{index}",
            Diagnostics = new CollectedDiagnostics
            {
                Error = "Shared fixture cannot start on kernel 6.19",
                StackTrace = "",
                StandardOutput = "",
                StandardError = "",
            },
            Sources = [new TestResultSource { ArtifactId = 10, ResultFile = "results.trx", Format = TestArtifactFormat.Trx, ExecutionId = null }],
        }).ToImmutableArray();
        if (differentStack)
        {
            failures = failures.SetItem(0, failures[0] with
            {
                Diagnostics = failures[0].Diagnostics with { StackTrace = "Different fixture initialization stack" },
            });
        }
        evidence = evidence with { TestEvidence = evidence.TestEvidence with { Failures = failures } };
        var before = TransientJson.Write(evidence);

        var summary = WorkflowFailureDiagnosisContextBuilder.Build(new CollectedRun(evidence, ImmutableDictionary<string, CollectedAttachment>.Empty, [])).Summary;

        Assert.Equal(groups, summary.Split("Shared fixture cannot start on kernel 6.19", StringSplitOptions.None).Length - 1);
        foreach (var failure in failures)
        {
            Assert.Contains($"\"{failure.Ref}\"", summary, StringComparison.Ordinal);
        }
        Assert.Equal(before, TransientJson.Write(evidence));
    }

    [Fact]
    public async Task OrderlyCancellationFinalizesFailureWithoutInventingEstablishedFacts()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var invocation = new AnalysisInvocation();
        _invocationDirectories.Add(invocation.RootDirectory);
        var paths = invocation.Evidence;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AnalyzeCiFailure.EvidencePreparation.CollectEvidenceAndPrepareAnalysisAsync(
            _workspace.Path,
            "microsoft/aspire",
            invocation,
            "123",
            "1",
            CreateGitHubClient(),
            cancellation.Token));
        var outcome = TransientJson.Read<AnalysisOutcome>(await File.ReadAllTextAsync(paths.Outcome));
        Assert.Equal("failed", outcome.Status);
        Assert.Null(outcome.HasRelevantFailures);
        Assert.Null(outcome.RunId);
        Assert.Contains(outcome.Problems, problem => problem.Stage == "identity");
        Assert.False(File.Exists(paths.Bundle));
        Assert.False(File.Exists(paths.Summary));
    }

    [Theory]
    [InlineData("9007199254740992")]
    [InlineData("9007199254740993")]
    [InlineData("9223372036854775807")]
    public async Task PublicationPreservesExactLargeIssueIdsThroughLookupEditAndHistory(string id)
    {
        await WriteGroupedFixtureAsync();
        await File.WriteAllTextAsync(
            Path.Combine(_workspace.Path, "issue.json"),
            new JsonArray(new JsonObject
            {
                ["number"] = JsonNode.Parse(id),
                ["state"] = "open",
                ["body"] = ExistingGroupedIssue,
                ["labels"] = new JsonArray(new JsonObject { ["name"] = "ci-failure-cause" }),
            }).ToJsonString());
        var result = await RunSuccessfulGroupedPublicationAsync();
        Assert.True(result.ExitCode == 0, result.Output);
        var calls = await File.ReadAllLinesAsync(Path.Combine(_workspace.Path, "gh-calls.log"));
        Assert.Contains($"api repos/microsoft/aspire/issues/{id}", calls);
        Assert.Contains(calls, call => call.StartsWith($"issue edit {id} --repo microsoft/aspire ", StringComparison.Ordinal));
        var history = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(_workspace.Path, "memory-repo", "causes", "debugger-timeout.json")))!;
        Assert.Equal($"https://github.com/microsoft/aspire/issues/{id}", history["issue_url"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("77.0000000000000001")]
    [InlineData("1e400")]
    [InlineData("9223372036854775808")]
    public async Task InvalidUpstreamIssueIdsNeverBecomeApiEndpointSegments(string id)
    {
        await WriteGroupedFixtureAsync();
        await File.WriteAllTextAsync(
            Path.Combine(_workspace.Path, "issue.json"),
            new JsonArray(new JsonObject
            {
                ["number"] = JsonNode.Parse(id),
                ["state"] = "open",
                ["body"] = ExistingGroupedIssue,
                ["labels"] = new JsonArray(new JsonObject { ["name"] = "ci-failure-cause" }),
            }).ToJsonString());
        var result = await RunSuccessfulGroupedPublicationAsync();
        Assert.Equal(1, result.ExitCode);
        Assert.All(
            await File.ReadAllLinesAsync(Path.Combine(_workspace.Path, "gh-calls.log")),
            call => Assert.Contains("--paginate --slurp", call, StringComparison.Ordinal));
        Assert.False(Directory.Exists(Path.Combine(_workspace.Path, "memory-repo", "runs")));
    }

    [Theory]
    [InlineData("new-collision")]
    [InlineData("existing-missing")]
    [InlineData("existing-type-change")]
    [InlineData("existing-unreadable")]
    [InlineData("existing-bad-history")]
    [InlineData("existing-id-invalid")]
    [InlineData("new-replay-type-change")]
    [InlineData("new-replay-id-change")]
    public async Task FreshCauseIdentityFailuresPrecedeEveryPublicationEffect(string scenario)
    {
        await WriteGroupedFixtureAsync();
        var (evidence, proposal) = AnalyzeCiFailureFixtures.Read(_workspace.Path);
        var created = Assert.IsType<NewCauseProposal>(Assert.Single(proposal.Causes));
        if (!scenario.StartsWith("new-", StringComparison.Ordinal))
        {
            evidence = evidence with { PriorCauseIdentities = [new PriorCauseIdentity { Id = created.Definition.Id, Type = created.Definition.Type }] };
            proposal = proposal with
            {
                Causes = [new ExistingCauseProposal { Id = created.Definition.Id, JobIds = created.JobIds, TestRefs = created.TestRefs, Reason = created.Reason }],
            };
        }
        Assert.Equal(0, (await RunStrictValidationAsync(TransientJson.Write(evidence), TransientJson.Write(proposal))).ExitCode);
        JsonObject? history = scenario == "existing-missing" ? null : new JsonObject
        {
            ["id"] = scenario == "existing-id-invalid" ? JsonValue.Create(value: false) :
                JsonValue.Create(scenario == "new-replay-id-change" ? "different-cause" : created.Definition.Id),
            ["type"] = scenario is "existing-type-change" or "new-replay-type-change" ? "infra-failure" : scenario == "existing-unreadable" ? "invalid" : "flaky-test",
            ["title"] = "Stored definition", ["error_pattern"] = "Stored pattern",
            ["occurrences"] = new JsonArray(new JsonObject { ["run_id"] = scenario.StartsWith("new-replay", StringComparison.Ordinal) ? 123 : 999, ["observed_at"] = "2026-10-20T01:00:00Z" }),
        };
        if (scenario == "existing-bad-history")
        {
            history!["occurrences"] = "not an array";
        }
        var result = await RunSuccessfulGroupedPublicationAsync(history, useTypedProposal: true);
        Assert.Equal(1, result.ExitCode);
        Assert.False(File.Exists(Path.Combine(_workspace.Path, "gh-calls.log")));
        Assert.False(Directory.Exists(Path.Combine(_workspace.Path, "memory-repo", "runs")));
        var pushed = await RunProcessAsync("git", ["--no-pager", "--git-dir", "remote.git", "show-ref", "--verify", "refs/heads/memory/ci-failure-analysis"]);
        Assert.Equal(_publicationHistoryBefore, pushed);
    }

    [Fact]
    public async Task NewCauseProposalReplaysAfterDurableHistoryPushAndFailedIssueCreation()
    {
        await WriteGroupedFixtureAsync();
        var (evidence, proposal) = AnalyzeCiFailureFixtures.Read(_workspace.Path);
        Assert.IsType<NewCauseProposal>(Assert.Single(proposal.Causes));
        Assert.Equal(0, (await RunStrictValidationAsync(TransientJson.Write(evidence), TransientJson.Write(proposal))).ExitCode);
        await File.WriteAllTextAsync(Path.Combine(_workspace.Path, "fail-issue-create-once"), "");
        await File.WriteAllTextAsync(Path.Combine(_workspace.Path, "seed-unselected-malformed"), "");
        var first = await RunSuccessfulGroupedPublicationAsync(useTypedProposal: true);
        Assert.Equal(1, first.ExitCode);
        Assert.Contains("POST /repos/microsoft/aspire/issues: HTTP 500.", first.Output, StringComparison.Ordinal);
        var durable = await RunProcessAsync(
            "git",
            ["--no-pager", "--git-dir", "remote.git", "show",
            "refs/heads/memory/ci-failure-analysis:causes/debugger-timeout.json"]);
        Assert.Equal(0, durable.ExitCode);
        var before = JsonNode.Parse(durable.Output)!;
        var created = (NewCauseProposal)proposal.Causes[0];
        evidence = evidence with { Run = evidence.Run with { Attempt = 2 } };
        proposal = proposal with
        {
            Run = proposal.Run with { Attempt = 2 },
            Causes = [created with { Definition = created.Definition with { Title = "Retry definition must not replace the first writer" } }],
        };
        await File.WriteAllTextAsync(Path.Combine(EvidenceDirectory, "evidence.json"), TransientJson.Write(evidence));
        await File.WriteAllTextAsync(Path.Combine(_workspace.Path, "ci-analysis-output", "proposal.json"), TransientJson.Write(proposal));
        var fakeGh = Assert.Single(Directory.GetFiles(_workspace.Path, "gh", SearchOption.AllDirectories));
        var retry = await RunTypedPublicationCommandAsync(fakeGh, "2026-10-23T12:00:00Z");
        Assert.True(retry.ExitCode == 0, retry.Output);
        var stored = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(_workspace.Path, "memory-repo", "causes", "debugger-timeout.json")))!;
        Assert.Equal("Debugger timeout", stored["title"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(before["occurrences"], stored["occurrences"]));
        var occurrence = Assert.Single(stored["occurrences"]!.AsArray())!;
        Assert.Equal(
            new[] { "job", "observed_at", "pr_number", "run_id", "run_url", "tests" },
            occurrence.AsObject().Select(property => property.Key).Order(StringComparer.Ordinal));
        Assert.Equal(42, occurrence["pr_number"]!.GetValue<int>());
        Assert.Equal(
            new[] { "error", "job", "job_id", "name", "result_file", "result_format", "stack_trace", "standard_error", "standard_output" },
            occurrence["tests"]![0]!.AsObject().Select(property => property.Key).Order(StringComparer.Ordinal));
        var summary = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(_workspace.Path, "memory-repo", "runs", "123.json")))!;
        Assert.Equal("2026-10-23T12:00:00Z", summary["analyzed_at"]!.GetValue<string>());
        Assert.Equal(2, summary["run_attempt"]!.GetValue<int>());
        Assert.Null(summary["pr"]);
        Assert.True(File.Exists(Path.Combine(_workspace.Path, "pr-comment.md")));
    }

    [Theory]
    [InlineData("identity")]
    [InlineData("unavailable")]
    [InlineData("malformed")]
    public async Task ValidationIgnoresPlantedAgentEvidenceOnRejection(string rejection)
    {
        await WriteGroupedFixtureAsync();
        var (evidence, proposal) = AnalyzeCiFailureFixtures.Read(_workspace.Path);
        Assert.Equal(0, (await RunStrictValidationAsync(TransientJson.Write(evidence), TransientJson.Write(proposal))).ExitCode);
        var agent = Path.Combine(_workspace.Path, "ci-analysis-output");
        await File.WriteAllTextAsync(Path.Combine(agent, "evidence.json"), "not trusted evidence");
        await File.WriteAllTextAsync(Path.Combine(agent, "ci-validation-result.json"), """{"verdict":"transient-infra"}""");
        var arguments = new[] { "validate", "--repo", "microsoft/aspire", "--working-dir", _workspace.Path, "--analysis-dir", agent };
        var accepted = await RunValidatorCommandAsync(arguments);
        Assert.Equal(0, accepted.ExitCode);
        if (rejection == "unavailable")
        {
            await File.WriteAllTextAsync(
                Path.Combine(EvidenceDirectory, "evidence.json"),
                TransientJson.Write(evidence with { TestEvidence = evidence.TestEvidence with { State = EvidenceState.Unavailable, Failures = [] } }));
        }
        var rejectedText = rejection == "malformed" ? "not JSON" : TransientJson.Write(rejection == "identity"
            ? proposal with { Run = proposal.Run with { Attempt = proposal.Run.Attempt + 1 } } : proposal);
        await File.WriteAllTextAsync(Path.Combine(agent, "proposal.json"), rejectedText);
        var rejected = await RunValidatorCommandAsync(arguments);
        Assert.Equal(1, rejected.ExitCode);
        Assert.Equal(rejectedText, await File.ReadAllTextAsync(Path.Combine(agent, "proposal.json")));
    }

    [Fact]
    public async Task RemovedAcceptanceFileOptionCannotOverwriteAgentFiles()
    {
        await WriteGroupedFixtureAsync();
        AnalyzeCiFailureFixtures.Write(_workspace.Path);
        var agent = Path.Combine(_workspace.Path, "ci-analysis-output");
        var inside = Path.Combine(agent, "planted-result.json");
        await File.WriteAllTextAsync(inside, "agent-owned bytes");
        var result = await RunValidatorCommandAsync(["validate", "--repo", "microsoft/aspire", "--working-dir", _workspace.Path,
            "--analysis-dir", agent, "--result-file", inside]);
        Assert.Equal(2, result.ExitCode);
        Assert.Contains("--result-file", result.Output, StringComparison.Ordinal);
        Assert.Equal("agent-owned bytes", await File.ReadAllTextAsync(inside));
    }

    [Fact]
    public async Task CollectedArtifactAloneSupportsValidationAndPublicationInAnotherWorkspace()
    {
        var collection = await RunCollectionAsync(useWorkflowAdapter: true);
        Assert.True(collection.ExitCode == 0, collection.Output);
        var transferred = Directory.CreateDirectory(Path.Combine(_workspace.Path, "transferred")).FullName;
        var data = Directory.CreateDirectory(Path.Combine(transferred, "ci-failure-analysis/evidence")).FullName;
        var source = EvidenceDirectory;
        foreach (var name in new[] { "evidence.json", "analysis-summary.md", "history/cause-index.json" })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(data, name))!);
            File.Copy(Path.Combine(source, name), Path.Combine(data, name));
        }
        var evidence = TransientJson.Read<EvidenceBundle>(await File.ReadAllTextAsync(Path.Combine(data, "evidence.json")));
        var proposal = new AnalysisProposal
        {
            SchemaVersion = 1,
            Run = new ProposedWorkflowRun { Repository = evidence.Run.Repository, Id = evidence.Run.Id, Attempt = evidence.Run.Attempt },
            Jobs = evidence.Jobs.Select(job => new ProposedJobDecision { JobId = job.Id, Classification = JobClassification.FlakyTest, Reason = "Current timeout" }).ToImmutableArray(),
            Tests = evidence.TestEvidence.Failures.Select(test => new ProposedTestDecision { Ref = test.Ref, Classification = TestClassification.Flaky, Reason = "Current timeout" }).ToImmutableArray(),
            Causes =
            [
                new NewCauseProposal
                {
                    Definition = new ProposedCauseDefinition { Id = "transferred-timeout", Type = CauseType.FlakyTest, Title = "Transferred timeout", ErrorPattern = "timeout" },
                    JobIds = evidence.Jobs.Select(job => job.Id).ToImmutableArray(),
                    TestRefs = evidence.TestEvidence.Failures.Select(test => test.Ref).ToImmutableArray(),
                    Reason = "Current diagnostics",
                },
            ],
        };
        var agent = Directory.CreateDirectory(Path.Combine(_workspace.Path, "agent-artifact")).FullName;
        await File.WriteAllTextAsync(Path.Combine(agent, "proposal.json"), TransientJson.Write(proposal));
        var validation = await RunValidatorCommandAsync(["validate", "--repo", "microsoft/aspire", "--working-dir", transferred, "--analysis-dir", agent]);
        Assert.True(validation.ExitCode == 0, validation.Output);
        var remote = Path.Combine(transferred, "remote.git");
        var memory = Path.Combine(transferred, "history-checkout");
        foreach (var arguments in new string[][]
        {
            ["--no-pager", "init", "--bare", remote],
            ["--no-pager", "init", transferred],
            ["--no-pager", "-C", transferred, "remote", "add", "origin", remote],
        })
        {
            var setup = await RunProcessAsync("git", arguments);
            Assert.True(setup.ExitCode == 0, setup.Output);
        }
        var fakeGh = await CreateFakeGhAsync("""
            #!/usr/bin/env bash
            set -eu
            if [ "$1" = api ]; then
              case "$*" in
                *pulls/42*) printf '{"state":"open","locked":false}' ;;
                *) printf '[]' ;;
              esac
            elif [ "$1 $2" = "issue create" ]; then
              printf 'https://github.com/microsoft/aspire/issues/77\n'
            elif [ "$1 $2" = "pr comment" ]; then
              printf 'https://github.com/microsoft/aspire/pull/42#issuecomment-123\n'
            else
              exit 99
            fi
            """);
        var publish = await RunProcessAsync(
            Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
            ["exec", "--runtimeconfig", Path.ChangeExtension(typeof(AnalyzeCiFailureWorkflowTests).Assembly.Location, ".runtimeconfig.json"),
                typeof(AnalyzeCiFailure.Program).Assembly.Location, "publish", "--repo", "microsoft/aspire", "--working-dir", transferred,
                "--evidence-dir", data, "--analysis-dir", agent, "--history-dir", memory, "--observed-at", "2026-10-21T01:00:00Z"],
            new Dictionary<string, string> { ["PATH"] = $"{Path.GetDirectoryName(fakeGh)}{Path.PathSeparator}{Environment.GetEnvironmentVariable("PATH")}" });
        Assert.True(publish.ExitCode == 0, publish.Output);
        var stored = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(memory, "causes", "transferred-timeout.json")))!;
        var test = Assert.Single(stored["occurrences"]![0]!["tests"]!.AsArray())!;
        Assert.Equal("stderr evidence", test["standard_error"]!.GetValue<string>());
        Assert.Equal("00001.trx", test["result_file"]!.GetValue<string>());
        Assert.False(Directory.Exists(Path.Combine(data, "test-results")));
        Assert.Equal(
            new[] { "error", "job", "job_id", "name", "result_file", "result_format", "stack_trace", "standard_error", "standard_output" },
            test.AsObject().Select(property => property.Key).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task WorkflowExamplesDecodeAndValidateAgainstMatchingCollectedFacts()
    {
        await WriteGroupedFixtureAsync();
        var (captured, _) = AnalyzeCiFailureFixtures.Read(_workspace.Path);
        var evidence = captured with
        {
            Run = captured.Run with { Id = 12345, Attempt = 1, Url = "https://github.com/microsoft/aspire/actions/runs/12345" },
            Jobs = [captured.Jobs[0] with { Id = 67890 }],
            TestEvidence = captured.TestEvidence with { Failures = [captured.TestEvidence.Failures[0] with { Ref = "test-1", JobId = 67890 }] },
            PriorCauseIdentities = [new PriorCauseIdentity { Id = "test-timeout", Type = CauseType.FlakyTest }],
        };
        var markdown = await File.ReadAllTextAsync(Path.Combine(RepoRoot.Path, ".github", "workflows", "analyze-ci-failure.md"));
        var examples = JsonExampleRegex().Matches(markdown).Select(match => match.Groups[1].Value).ToArray();
        Assert.Equal(2, examples.Length);
        var proposal = TransientJson.Read<AnalysisProposal>(examples[0]);
        Assert.Equal(AnalysisVerdict.FlakyTest, AnalysisValidator.Validate(evidence, proposal, "microsoft/aspire").Report.Verdict);
        var newCause = TransientJson.Read<ProposedCause>(examples[1]);
        Assert.Equal(
            AnalysisVerdict.FlakyTest,
            AnalysisValidator.Validate(
                evidence with { PriorCauseIdentities = [] },
                proposal with { Causes = [newCause] },
                "microsoft/aspire").Report.Verdict);
    }

    [GeneratedRegex("```json\\r?\\n(.*?)\\r?\\n```", RegexOptions.Singleline)]
    private static partial Regex JsonExampleRegex();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FatalEvidenceWriteFailureRetainsKnownWorkAndDoesNotBecomeDegraded(bool unavailableTests)
    {
        var result = await RunCollectionAsync(unavailableTests ? "write-evidence-failure-missing-artifact" : "write-evidence-failure", useWorkflowAdapter: true);
        Assert.Equal(1, result.ExitCode);
        var root = EvidenceDirectory;
        var outcome = TransientJson.Read<AnalysisOutcome>(await File.ReadAllTextAsync(Path.Combine(root, "collection-outcome.json")));
        Assert.Equal("failed", outcome.Status);
        Assert.True(outcome.HasRelevantFailures);
        Assert.Equal(123, outcome.RunId);
        Assert.Contains(outcome.Problems, problem => problem.Stage == "views");
        Assert.Equal(unavailableTests, outcome.Problems.Any(problem => problem.Stage == "artifacts"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CopiedExecutionsRetainAllSourcesButIdentitylessFailuresRemainDistinct(bool executionId)
    {
        var first = RawFailure("source-a.trx", executionId ? "execution" : null);
        var second = first with { Source = first.Source with { ArtifactId = 9, ResultFile = "source-b.trx" } };
        var failures = TestFailureReader.ConsolidateFailures([first, second]);
        Assert.Equal(executionId ? 1 : 2, failures.Length);
        Assert.Equal(executionId ? 2 : 1, failures[0].Sources.Length);
        Assert.Equal("source-a.trx", failures[0].Sources[0].ResultFile);
    }

    [Fact]
    public void CopyConflictsAreDetectedBeforeDiagnosticTruncation()
    {
        var first = RawFailure("source-a.trx", "execution");
        first = first with { Diagnostics = first.Diagnostics with { Error = new string('a', count: 1001) + "first" } };
        var second = first with
        {
            Diagnostics = first.Diagnostics with { Error = new string('a', count: 1001) + "different" },
            Source = first.Source with { ArtifactId = 9 },
        };
        Assert.Equal(
            "Conflicting diagnostics for the same test execution: job=77 execution='execution' artifacts=8,9 test='Tests.Method'",
            Assert.Throws<ValidationException>(() => TestFailureReader.ConsolidateFailures([first, second])).Message);
    }

    private static RawTestFailure RawFailure(string file, string? execution)
        => new(
            JobId: 77,
            "Tests",
            "Tests.Method",
            new CollectedDiagnostics
            {
                Error = "timeout",
                StackTrace = "",
                StandardOutput = "",
                StandardError = "",
            },
            new TestResultSource
            {
                ArtifactId = 8,
                ResultFile = file,
                Format = TestArtifactFormat.Trx,
                ExecutionId = execution,
            });

    [Theory]
    [InlineData("valid", "ready", 0)]
    [InlineData("healthy", "no-work", 0)]
    [InlineData("missing-artifact", "degraded", 0)]
    [InlineData("wrong-run", "failed", 1)]
    public async Task CollectionFinalizesVersionedOutcomeAndSingleAuthoritativeBundle(string scenario, string status, int exitCode)
    {
        var result = await RunCollectionAsync(scenario);
        Assert.Equal(exitCode, result.ExitCode);
        var root = EvidenceDirectory;
        var outcome = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(root, "collection-outcome.json")))!;
        Assert.Equal(status, outcome["status"]!.GetValue<string>());
        Assert.Equal(1, outcome["schema_version"]!.GetValue<int>());
        if (status is "ready" or "degraded")
        {
            var bundle = TransientJson.Read<EvidenceBundle>(await File.ReadAllTextAsync(Path.Combine(root, "evidence.json")));
            Assert.Equal(123, bundle.Run.Id);
            Assert.Equal(2, bundle.Run.Attempt);
            Assert.Equal(status == "ready" ? EvidenceState.Complete : EvidenceState.Unavailable, bundle.TestEvidence.State);
            Assert.True(File.Exists(Path.Combine(root, "analysis-summary.md")));
            Assert.False(File.Exists(Path.Combine(root, "run-context.json")));
            Assert.False(File.Exists(Path.Combine(root, "test-failures.json")));
        }
    }
}
