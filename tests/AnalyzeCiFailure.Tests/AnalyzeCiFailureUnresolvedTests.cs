// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using System.Text.Json.Nodes;
using AnalyzeCiFailure;
using Aspire.TestUtilities;
using VerifyXunit;
using Xunit;

namespace Infrastructure.Tests;

/// <summary>Preserves unknown mechanisms without inventing infrastructure, test causality or rerun eligibility.</summary>
public sealed partial class AnalyzeCiFailureWorkflowTests
{
    [Theory]
    [InlineData("main", false)]
    [InlineData("main", true)]
    [InlineData("pull-request", false)]
    [InlineData("pull-request", true)]
    public async Task UnresolvedFailuresRetainObservedDiagnosticsAndTrustedContext(string scopeName, bool observedTests)
    {
        var scope = CiFailureJson.ReadEnum<RunScope>(scopeName, "Invalid scope");
        var evidence = AnalyzeCiFailureFixtures.CreateEvidence(scope);
        var proposal = UnresolvedProposal(evidence);
        if (observedTests)
        {
            evidence = evidence with
            {
                TestEvidence = evidence.TestEvidence with
                {
                    Failures =
                    [
                        new CollectedTestFailure
                        {
                            Ref = "test-1", JobId = 123, Name = "Tests.ActuallyFailed", DisplayName = "Tests.ActuallyFailed",
                            Diagnostics = new CollectedDiagnostics
                            {
                                Error = "Unexpected host disconnect; token=private-value", StackTrace = "TestFrame",
                                StandardOutput = "Captured output", StandardError = "Captured error",
                            },
                            Sources = [new TestResultSource { ArtifactId = 10, ResultFile = "results.trx", Format = TestArtifactFormat.Trx, ExecutionId = "execution" }],
                        },
                    ],
                },
            };
            proposal = proposal with
            {
                Tests = [new ProposedTestDecision { Ref = "test-1", Classification = TestClassification.Unresolved, Reason = "Failure observed; mechanism unknown" }],
            };
        }

        var accepted = AnalysisValidator.Validate(
            TransientJson.Read<EvidenceBundle>(TransientJson.Write(evidence)),
            TransientJson.Read<AnalysisProposal>(TransientJson.Write(proposal)), "microsoft/aspire");
        Assert.Equal(AnalysisVerdict.Unresolved, accepted.Report.Verdict);
        Assert.Equal(observedTests ? 1 : 0, accepted.Report.Tests.Length);
        var cause = Assert.Single(accepted.Causes);
        var occurrence = AnalysisPublisher.CreateOccurrence(accepted, cause, JsonValue.Create(123)!, "2026-11-04T00:00:00Z");
        var history = AnalysisPublisher.MergeCause((JsonObject)CiFailureJson.ToNode(cause.Definition)!, null, occurrence);
        var draft = CauseIssueRenderer.RenderCanonicalIssue(
            history, accepted.Evidence.Context, accepted.CapturedPublication, accepted.Run.Url,
            CiFailureJson.EnumText(scope), scope == RunScope.Main ? "0" : "42", "` Tests `",
            $"| 2026-11-04 | [123](https://github.com/microsoft/aspire/actions/runs/123) | ` Tests ` | {(scope == RunScope.Main ? "main" : "#42")} |");
        var body = CauseIssueRenderer.FitIssue(draft.Body!, CauseIssueRenderer.Observations(history));
        var run = RunSummaryBuilder.Create(accepted.Report, accepted.Evidence, accepted.CapturedPublication, "2026-11-04T00:00:00Z");
        var restoredHistory = CiFailureJson.ToContract<PriorCauseRecord>(history)!;
        Assert.Equal(CauseType.Unresolved, restoredHistory.Type);

        await Verifier.Verify(CiFailureJson.Serialize(new
        {
            draft.Title, draft.Labels, Body = body, History = history, Run = run,
            Comment = scope == RunScope.PullRequest ? PrCommentRenderer.Render(accepted.Report, accepted.Evidence.Jobs, accepted.Run.Url) : null,
        }), "json").UseDirectory("Snapshots").UseParameters(scope, observedTests);
    }

    [Theory]
    [InlineData("transient-infra", "infra-failure")]
    [InlineData("flaky-test", "flaky-test")]
    [InlineData("main-repository-breakage", "main-repository-breakage")]
    public void UnresolvedHistoryCannotAuthorizeAnEstablishedMechanism(string classificationName, string typeName)
    {
        var classification = CiFailureJson.ReadEnum<JobClassification>(classificationName, "Invalid classification");
        var type = CiFailureJson.ReadEnum<CauseType>(typeName, "Invalid cause type");
        var evidence = AnalyzeCiFailureFixtures.CreateEvidence(RunScope.Main) with
        {
            PriorCauseIdentities = [new PriorCauseIdentity { Id = "host-exit-unresolved", Type = CauseType.Unresolved }],
        };
        var proposal = UnresolvedProposal(evidence);
        proposal = proposal with
        {
            Jobs = [proposal.Jobs[0] with { Classification = classification }],
            Causes = [new ExistingCauseProposal { Id = "host-exit-unresolved", JobIds = [123], TestRefs = [], Reason = "Same exit code" }],
        };

        Assert.Contains("incompatible failed job", Assert.Throws<ValidationException>(() =>
            AnalysisValidator.Validate(evidence, proposal, "microsoft/aspire")).Message, StringComparison.Ordinal);
        Assert.Throws<ValidationException>(() => ValidationRules.RequireUnchangedCauseType(
            new FailureCause("host-exit-unresolved", "host-exit-unresolved.json", type, [NormalizedJsonNumber.Parse("123")], default),
            new StoredCauseIdentity(CauseType.Unresolved, null) { Id = "host-exit-unresolved" }));
    }

    [Theory]
    [InlineData("main", "transient-infra", "infra-failure")]
    [InlineData("main", "main-repository-breakage", "main-repository-breakage")]
    [InlineData("pull-request", "transient-infra", "infra-failure")]
    [InlineData("pull-request", "code-issue", null)]
    public void UncertaintyRemainsVisibleAlongsidePositivelyDiagnosedJobs(string scopeName, string classificationName, string? typeName)
    {
        var scope = CiFailureJson.ReadEnum<RunScope>(scopeName, "Invalid scope");
        var evidence = AnalyzeCiFailureFixtures.CreateEvidence(scope);
        evidence = evidence with { Jobs = evidence.Jobs.Add(evidence.Jobs[0] with { Id = 456, Name = "Diagnosed job" }) };
        var proposal = UnresolvedProposal(evidence);
        var unknown = (NewCauseProposal)proposal.Causes[0];
        proposal = proposal with
        {
            Jobs = [proposal.Jobs[0], proposal.Jobs[1] with
            {
                Classification = CiFailureJson.ReadEnum<JobClassification>(classificationName, "Invalid classification"),
                Reason = typeName == "infra-failure" ? "Package feed returned HTTP 429" : "Compiler diagnostic: error CS1002",
            }],
            Causes = [unknown with { JobIds = [123] }],
        };
        if (typeName is not null)
        {
            proposal = proposal with
            {
                Causes = proposal.Causes.Add(new NewCauseProposal
                {
                    Definition = new ProposedCauseDefinition
                    {
                        Id = "diagnosed-failure", Type = CiFailureJson.ReadEnum<CauseType>(typeName, "Invalid cause type"),
                        Title = "Diagnosed failure", ErrorPattern = proposal.Jobs[1].Reason,
                    },
                    JobIds = [456], TestRefs = [], Reason = "Positive diagnostic",
                }),
            };
        }

        var accepted = AnalysisValidator.Validate(evidence, proposal, "microsoft/aspire");

        Assert.Equal(AnalysisVerdict.Unresolved, accepted.Report.Verdict);
        Assert.Equal(classificationName, CiFailureJson.EnumText(accepted.Report.Jobs[1].Classification!.Value));
        Assert.Equal(proposal.Causes.Length, accepted.Causes.Length);
    }

    [Theory]
    [InlineData("uncovered-job")]
    [InlineData("uncovered-test")]
    [InlineData("test-reference")]
    [InlineData("runner-loss")]
    public void UnresolvedDecisionsStillRequireCompleteCompatibleEvidence(string scenario)
    {
        var evidence = AnalyzeCiFailureFixtures.CreateEvidence(RunScope.Main);
        var proposal = UnresolvedProposal(evidence);
        if (scenario == "uncovered-job")
        {
            proposal = proposal with { Causes = [] };
        }
        else if (scenario == "test-reference")
        {
            proposal = proposal with { Causes = [proposal.Causes[0] with { TestRefs = ["test-1"] }] };
        }
        else if (scenario == "runner-loss")
        {
            evidence = evidence with { Jobs = [evidence.Jobs[0] with { RunnerFailure = WorkflowRunEvidenceCollector.RunnerLostAnnotation }] };
        }
        else
        {
            var infra = AnalyzeCiFailureFixtures.CreateInfrastructureProposal(evidence);
            proposal = infra with
            {
                Tests = [new ProposedTestDecision { Ref = "test-1", Classification = TestClassification.Unresolved, Reason = "Unknown mechanism" }],
            };
            evidence = evidence with
            {
                TestEvidence = evidence.TestEvidence with
                {
                    Failures =
                    [
                        new CollectedTestFailure
                        {
                            Ref = "test-1", JobId = 123, Name = "Tests.Failure", DisplayName = "Tests.Failure",
                            Diagnostics = new CollectedDiagnostics { Error = "Failure", StackTrace = "", StandardOutput = "", StandardError = "" },
                            Sources = [new TestResultSource { ArtifactId = 10, ResultFile = "results.trx", Format = TestArtifactFormat.Trx, ExecutionId = null }],
                        },
                    ],
                },
            };
        }

        Assert.Throws<ValidationException>(() => AnalysisValidator.Validate(evidence, proposal, "microsoft/aspire"));
    }

    [Theory]
    [InlineData("main", false)]
    [InlineData("pull-request", false)]
    [InlineData("main", true)]
    [InlineData("pull-request", true)]
    [RequiresTools(["bash", "jq", "git"])]
    public async Task UnresolvedPublicationPersistsAndDisplaysEveryCapturedFailure(string scopeName, bool aggregate)
    {
        var scope = CiFailureJson.ReadEnum<RunScope>(scopeName, "Invalid scope");
        await WriteGroupedFixtureAsync();
        var (evidence, original) = AnalyzeCiFailureFixtures.Read(_workspace.Path);
        evidence = evidence with
        {
            Run = evidence.Run with { Scope = scope, Event = scope == RunScope.Main ? "push" : "pull_request", HeadBranch = scope == RunScope.Main ? "main" : "feature" },
            SubjectPr = scope == RunScope.Main ? null : evidence.SubjectPr,
            MainContext = scope == RunScope.Main ? AnalyzeCiFailureFixtures.CreateEvidence(scope).MainContext! with { FailedSha = evidence.Run.HeadSha } : null,
        };
        var proposal = UnresolvedProposal(evidence) with
        {
            Tests = original.Tests.Select(test => test with { Classification = TestClassification.Unresolved, Reason = "Captured failure; mechanism unknown" }).ToImmutableArray(),
        };
        if (aggregate)
        {
            var cause = (NewCauseProposal)proposal.Causes[0];
            proposal = proposal with
            {
                Causes = Enumerable.Range(1, 11).Select(index => (ProposedCause)(cause with
                {
                    Definition = cause.Definition with { Id = $"unresolved-symptom-{index}", Title = $"Unresolved symptom {index}" },
                })).ToImmutableArray(),
            };
        }
        await File.WriteAllTextAsync(Path.Combine(EvidenceDirectory, "evidence.json"), TransientJson.Write(evidence));
        await File.WriteAllTextAsync(Path.Combine(_workspace.Path, "ci-analysis-output", "proposal.json"), TransientJson.Write(proposal));

        var published = await RunSuccessfulGroupedPublicationAsync(useTypedProposal: true);

        Assert.True(published.ExitCode == 0, published.Output);
        var id = aggregate ? "unresolved-symptom-1" : "host-exit-unresolved";
        var history = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(_workspace.Path, "memory-repo", "causes", id + ".json")))!;
        var tests = history["occurrences"]![0]!["tests"]!.AsArray();
        Assert.Equal(evidence.TestEvidence.Failures.Length, tests.Count);
        Assert.Equal("unresolved", JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(_workspace.Path, "memory-repo", "runs", "123.json")))!["verdict"]!.GetValue<string>());
        var issue = await File.ReadAllTextAsync(Path.Combine(_workspace.Path, "issue-body.md"));
        Assert.All(tests, test => Assert.Contains(test!["name"]!.GetValue<string>(), issue, StringComparison.Ordinal));
        Assert.Contains("not an established mechanism or causal test", issue, StringComparison.Ordinal);
        if (scope == RunScope.PullRequest)
        {
            var comment = await File.ReadAllTextAsync(Path.Combine(_workspace.Path, "pr-comment.md"));
            if (!aggregate)
            {
                Assert.All(tests, test => Assert.Contains(test!["name"]!.GetValue<string>(), comment, StringComparison.Ordinal));
            }
            Assert.Contains("will not be automatically rerun", comment, StringComparison.Ordinal);
        }
    }

    [Fact]
    [RequiresTools(["bash", "jq", "git"])]
    public async Task UnresolvedPublicationReplayAndLaterOccurrencePreserveFirstHistoryAndOperatorNotes()
    {
        await WriteGroupedFixtureAsync();
        var (evidence, original) = AnalyzeCiFailureFixtures.Read(_workspace.Path);
        var proposal = UnresolvedProposal(evidence) with
        {
            Tests = original.Tests.Select(test => test with { Classification = TestClassification.Unresolved }).ToImmutableArray(),
        };
        var proposalPath = Path.Combine(_workspace.Path, "ci-analysis-output", "proposal.json");
        var evidencePath = Path.Combine(EvidenceDirectory, "evidence.json");
        await File.WriteAllTextAsync(evidencePath, TransientJson.Write(evidence));
        await File.WriteAllTextAsync(proposalPath, TransientJson.Write(proposal));
        var first = await RunSuccessfulGroupedPublicationAsync(useTypedProposal: true);
        Assert.True(first.ExitCode == 0, first.Output);
        var historyPath = Path.Combine(_workspace.Path, "memory-repo", "causes", "host-exit-unresolved.json");
        var initialHistory = JsonNode.Parse(await File.ReadAllTextAsync(historyPath))!;
        var initialRunPath = Path.Combine(_workspace.Path, "memory-repo", "runs", "123.json");
        var initialRun = await File.ReadAllTextAsync(initialRunPath);
        var body = await File.ReadAllTextAsync(Path.Combine(_workspace.Path, "issue-body.md"));
        await File.WriteAllTextAsync(Path.Combine(_workspace.Path, "issue.json"), new JsonArray(new JsonObject
        {
            ["number"] = 77, ["state"] = "open", ["html_url"] = "https://github.com/microsoft/aspire/issues/77",
            ["body"] = body + "\nOperator notes: preserve this.\n",
            ["labels"] = new JsonArray(new JsonObject { ["name"] = "ci-failure-cause" }),
        }).ToJsonString());
        var fakeGh = Path.Combine(Assert.Single(Directory.GetDirectories(_workspace.Path, "fake-bin-*")), "gh");

        var replay = await RunTypedPublicationCommandAsync(fakeGh, "2026-10-21T01:00:00Z");

        Assert.True(replay.ExitCode == 0, replay.Output);
        Assert.True(JsonNode.DeepEquals(initialHistory, JsonNode.Parse(await File.ReadAllTextAsync(historyPath))));
        evidence = evidence with
        {
            Run = evidence.Run with { Id = 124, HeadSha = new string('b', 40), Url = "https://github.com/microsoft/aspire/actions/runs/124" },
            Jobs = [evidence.Jobs[0] with { Id = 456 }],
            TestEvidence = evidence.TestEvidence with
            {
                Failures = evidence.TestEvidence.Failures.Select(test => test with { JobId = 456 }).ToImmutableArray(),
            },
            PriorCauseIdentities = [new PriorCauseIdentity { Id = "host-exit-unresolved", Type = CauseType.Unresolved }],
        };
        proposal = proposal with
        {
            Run = proposal.Run with { Id = 124 },
            Jobs = [proposal.Jobs[0] with { JobId = 456 }],
            Causes = [new ExistingCauseProposal { Id = "host-exit-unresolved", JobIds = [456], TestRefs = [], Reason = "Matching symptoms, mechanism still unknown" }],
        };
        await File.WriteAllTextAsync(evidencePath, TransientJson.Write(evidence));
        await File.WriteAllTextAsync(proposalPath, TransientJson.Write(proposal));

        var later = await RunTypedPublicationCommandAsync(fakeGh, "2026-10-22T01:00:00Z");

        Assert.True(later.ExitCode == 0, later.Output);
        var history = JsonNode.Parse(await File.ReadAllTextAsync(historyPath))!;
        Assert.Equal("unresolved", history["type"]!.GetValue<string>());
        Assert.Equal(2, history["occurrences"]!.AsArray().Count);
        Assert.True(JsonNode.DeepEquals(initialHistory["occurrences"]![0], history["occurrences"]![0]));
        Assert.Equal(initialRun, await File.ReadAllTextAsync(initialRunPath));
        var updated = await File.ReadAllTextAsync(Path.Combine(_workspace.Path, "issue-body.md"));
        Assert.Contains("Operator notes: preserve this.", updated, StringComparison.Ordinal);
        Assert.Contains("actions/runs/124", updated, StringComparison.Ordinal);
        Assert.Contains("job 456", updated, StringComparison.Ordinal);
    }

    [Fact]
    public void OversizedUnresolvedReportFailsExplicitlyBeforePublication()
    {
        var evidence = AnalyzeCiFailureFixtures.CreateEvidence(RunScope.PullRequest);
        evidence = evidence with
        {
            TestEvidence = evidence.TestEvidence with
            {
                Failures = Enumerable.Range(1, 25).Select(index => new CollectedTestFailure
                {
                    Ref = $"test-{index}", JobId = 123, Name = $"Tests.Failure{index}", DisplayName = $"Tests.Failure{index}",
                    Diagnostics = new CollectedDiagnostics
                    {
                        Error = "Failure observed; mechanism unknown", StackTrace = "",
                        StandardOutput = new string('x', 4000), StandardError = "",
                    },
                    Sources = [new TestResultSource { ArtifactId = 10, ResultFile = "results.trx", Format = TestArtifactFormat.Trx, ExecutionId = null }],
                }).ToImmutableArray(),
            },
        };
        var proposal = UnresolvedProposal(evidence) with
        {
            Tests = evidence.TestEvidence.Failures.Select(test => new ProposedTestDecision
            {
                Ref = test.Ref, Classification = TestClassification.Unresolved, Reason = "Unknown mechanism",
            }).ToImmutableArray(),
        };

        var error = Assert.Throws<ValidationException>(() => AnalysisValidator.Validate(evidence, proposal, "microsoft/aspire"));

        Assert.Equal("Rendered PR comment exceeds the 65000-byte publication budget", error.Message);
    }

    private static AnalysisProposal UnresolvedProposal(EvidenceBundle evidence)
        => AnalyzeCiFailureFixtures.CreateInfrastructureProposal(evidence) with
        {
            Jobs = evidence.Jobs.Select(job => new ProposedJobDecision
            {
                JobId = job.Id, Classification = JobClassification.Unresolved, Reason = "Test host exit 3; no infrastructure mechanism established",
            }).ToImmutableArray(),
            Causes =
            [
                new NewCauseProposal
                {
                    Definition = new ProposedCauseDefinition
                    {
                        Id = "host-exit-unresolved", Type = CauseType.Unresolved,
                        Title = "Test host aborted; mechanism unresolved", ErrorPattern = "Test host exited with exit code 3",
                    },
                    JobIds = evidence.Jobs.Select(job => job.Id).ToImmutableArray(), TestRefs = [], Reason = "Observed symptoms only",
                },
            ],
        };
}
