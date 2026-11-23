// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using System.Text.Json.Nodes;
using AnalyzeCiFailure;
using Xunit;

namespace Infrastructure.Tests;

/// <summary>Verifies strict versioned-contract decoding, required fields and canonical wire values.</summary>
public sealed class AnalyzeCiFailureTransientContractTests
{
    [Theory]
    [InlineData("transient-infra", true)]
    [InlineData("code-issue", false)]
    public void InfrastructureFixtureFailuresRetainTestInventoryWithoutInventingFlakiness(string classification, bool accepted)
    {
        var evidence = CreateEvidence();
        evidence = evidence with
        {
            TestEvidence = evidence.TestEvidence with
            {
                Failures =
                [
                    new CollectedTestFailure
                    {
                        Ref = "test-1", JobId = evidence.Jobs[0].Id, Name = "Tests.FixtureFailure", DisplayName = "Tests.FixtureFailure",
                        Diagnostics = new CollectedDiagnostics
                        {
                            Error = "Fixture container cannot start on the runner kernel.", StackTrace = "", StandardOutput = "", StandardError = "",
                        },
                        Sources = [new TestResultSource { ArtifactId = 10, ResultFile = "results.trx", Format = TestArtifactFormat.Trx, ExecutionId = "execution" }],
                    },
                ],
            },
        };
        var json = JsonNode.Parse(TransientJson.Write(AnalyzeCiFailureFixtures.CreateInfrastructureProposal(evidence)))!;
        json["jobs"]![0]!["classification"] = classification;
        json["tests"] = JsonNode.Parse("""[{"ref":"test-1","classification":"transient-infra","reason":"Runner kernel incompatibility"}]""");
        var proposal = TransientJson.Read<AnalysisProposal>(json.ToJsonString());

        if (!accepted)
        {
            Assert.Throws<ValidationException>(() => AnalysisValidator.Validate(evidence, proposal, "microsoft/aspire"));
            return;
        }

        var analysis = AnalysisValidator.Validate(evidence, proposal, "microsoft/aspire");

        Assert.Equal(AnalysisVerdict.TransientInfrastructure, analysis.Report.Verdict);
        Assert.Equal("transient-infra", CiFailureJson.EnumText(Assert.Single(analysis.Report.Tests).Classification!.Value));
        Assert.Equal("Fixture container cannot start on the runner kernel.", analysis.Report.Tests[0].Error);
        Assert.Equal(CauseType.InfrastructureFailure, Assert.Single(analysis.Causes).Type);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PureValidationDoesNotMutateAcceptedOrRejectedInputs(bool reject)
    {
        var evidence = CreateEvidence();
        var proposal = TransientJson.Read<AnalysisProposal>(Proposal);
        if (reject)
        {
            proposal = proposal with { Run = proposal.Run with { Attempt = 2 } };
        }
        var originalEvidence = TransientJson.Write(evidence);
        var originalProposal = TransientJson.Write(proposal);
        for (var i = 0; i < 2; i++)
        {
            if (reject)
            {
                Assert.Throws<ValidationException>(() => AnalysisValidator.Validate(evidence, proposal, "microsoft/aspire"));
            }
            else
            {
                var accepted = AnalysisValidator.Validate(evidence, proposal, "microsoft/aspire");
                Assert.Equal(AnalysisVerdict.CodeIssue, accepted.Report.Verdict);
            }
        }
        Assert.Equal(originalEvidence, TransientJson.Write(evidence));
        Assert.Equal(originalProposal, TransientJson.Write(proposal));
    }

    [Fact]
    public void DecisionOnlyProposalBindsCollectedDiagnosticsWithoutCopyingThem()
    {
        var evidence = CreateEvidence() with
        {
            TestEvidence = new CollectedTestEvidence
            {
                State = EvidenceState.Complete, Problems = [],
                Failures =
                [
                    new CollectedTestFailure
                    {
                        Ref = "test-1", JobId = 9007199254740992, Name = "Tests.Example", DisplayName = "Tests.Example",
                        Diagnostics = new CollectedDiagnostics
                        {
                            Error = "Collected error", StackTrace = "Collected frame", StandardOutput = "stdout", StandardError = "stderr",
                        },
                        Sources = [new TestResultSource { ArtifactId = 10, ResultFile = "00001.trx", Format = TestArtifactFormat.Trx, ExecutionId = "execution" }],
                    },
                ],
            },
        };
        var proposal = TransientJson.Read<AnalysisProposal>(Proposal) with
        {
            Tests = [new ProposedTestDecision { Ref = "test-1", Classification = TestClassification.CodeIssue, Reason = "Deterministic" }],
        };
        var accepted = AnalysisValidator.Validate(evidence, proposal, "microsoft/aspire");
        var test = Assert.Single(accepted.Report.Tests);
        Assert.Equal("Collected error", test.Error);
        Assert.Equal("Collected frame", test.StackTrace);
        Assert.Equal("00001.trx", test.ResultFile);
        Assert.Null(test.ObservationId);
        Assert.Equal("stdout", test.StandardOutput);
        Assert.Equal("stderr", test.StandardError);
    }

    [Theory]
    [InlineData("repository")]
    [InlineData("run")]
    [InlineData("attempt")]
    [InlineData("version")]
    [InlineData("duplicate-job")]
    [InlineData("omitted-job")]
    [InlineData("unknown-test")]
    public void PureValidationRejectsReboundIdentityAndIncompleteCoverage(string scenario)
    {
        var evidence = CreateEvidence();
        var proposal = TransientJson.Read<AnalysisProposal>(Proposal);
        proposal = scenario switch
        {
            "repository" => proposal with { Run = proposal.Run with { Repository = "other/repository" } },
            "run" => proposal with { Run = proposal.Run with { Id = 9007199254740992 } },
            "attempt" => proposal with { Run = proposal.Run with { Attempt = 2 } },
            "version" => proposal with { SchemaVersion = 2 },
            "duplicate-job" => proposal with { Jobs = [proposal.Jobs[0], proposal.Jobs[0]] },
            "omitted-job" => proposal with { Jobs = [] },
            "unknown-test" => proposal with { Tests = [new ProposedTestDecision { Ref = "unknown", Classification = TestClassification.Flaky, Reason = "Decision" }] },
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };

        Assert.Throws<ValidationException>(() => AnalysisValidator.Validate(evidence, proposal, "microsoft/aspire"));
    }

    [Fact]
    public void PureValidationExplainsIncompleteTestInventoryWithoutCopyingUntrustedNames()
    {
        var evidence = CreateEvidence();
        var failures = Enumerable.Range(start: 1, count: 81).Select(index => new CollectedTestFailure
        {
            Ref = $"test-{index}",
            JobId = evidence.Jobs[0].Id,
            Name = $"Tests.Example{index}(token=untrusted-value)",
            DisplayName = $"Tests.Example{index}",
            Diagnostics = new CollectedDiagnostics { Error = "Failure", StackTrace = "", StandardOutput = "", StandardError = "" },
            Sources = [new TestResultSource { ArtifactId = 10, ResultFile = "results.trx", Format = TestArtifactFormat.Trx, ExecutionId = $"execution-{index}" }],
        }).ToImmutableArray();
        evidence = evidence with { TestEvidence = evidence.TestEvidence with { Failures = failures } };
        var proposal = TransientJson.Read<AnalysisProposal>(Proposal) with
        {
            Tests = failures.Take(3).Select(test => new ProposedTestDecision
            {
                Ref = test.Ref,
                Classification = TestClassification.CodeIssue,
                Reason = "Deterministic",
            }).ToImmutableArray(),
        };

        var error = Assert.Throws<ValidationException>(() => AnalysisValidator.Validate(evidence, proposal, "microsoft/aspire"));

        Assert.Equal(
            "Analysis failed_tests do not match trusted test failure evidence (expected=81, proposed=3); " +
            "classify every collected test reference exactly once without unknown or duplicate references.",
            error.Message);
    }

    [Fact]
    public void PureValidationSanitizesCopiesOfReasonsWithoutRewritingProposal()
    {
        var evidence = CreateEvidence();
        var proposal = TransientJson.Read<AnalysisProposal>(Proposal);
        var reason = "token=proposal-secret; " + new string('x', count: 600);
        proposal = proposal with { Jobs = [proposal.Jobs[0] with { Reason = reason }] };

        var accepted = AnalysisValidator.Validate(evidence, proposal, "microsoft/aspire");

        Assert.Equal(reason, proposal.Jobs[0].Reason);
        Assert.StartsWith("token=[REDACTED]; ", accepted.Report.Jobs[0].Reason);
        Assert.Equal(500, accepted.Report.Jobs[0].Reason!.Length);
    }

    [Fact]
    public void PureValidationDerivesInfrastructureVerdictFromNewCause()
    {
        var evidence = CreateEvidence();
        var proposal = TransientJson.Read<AnalysisProposal>(Proposal);
        proposal = proposal with
        {
            Jobs = [proposal.Jobs[0] with { Classification = JobClassification.TransientInfrastructure }],
            Causes =
            [
                new NewCauseProposal
                {
                    Definition = new ProposedCauseDefinition { Id = "runner-lost", Type = CauseType.InfrastructureFailure, Title = "Runner lost", ErrorPattern = "Runner disconnected" },
                    JobIds = [9007199254740992], TestRefs = [], Reason = "Runner connection was interrupted",
                },
            ],
        };
        var accepted = AnalysisValidator.Validate(evidence, proposal, "microsoft/aspire");
        Assert.Equal(AnalysisVerdict.TransientInfrastructure, accepted.Report.Verdict);
        Assert.False(Assert.Single(accepted.Causes).IsExistingProposal);
        Assert.Equal("runner-lost", accepted.Causes[0].Definition!.Id);

        var known = evidence with { PriorCauseIdentities = [new PriorCauseIdentity { Id = "runner-lost", Type = CauseType.InfrastructureFailure }] };
        Assert.Throws<ValidationException>(() => AnalysisValidator.Validate(known, proposal, "microsoft/aspire"));
        var existingProposal = proposal with
        {
            Causes = [new ExistingCauseProposal { Id = "runner-lost", JobIds = [9007199254740992], TestRefs = [], Reason = "Same runner failure" }],
        };
        var reused = AnalysisValidator.Validate(known, existingProposal, "microsoft/aspire");
        Assert.True(Assert.Single(reused.Causes).IsExistingProposal);
        Assert.Null(reused.Causes[0].Definition);
        Assert.Throws<ValidationException>(() => AnalysisValidator.Validate(evidence, existingProposal, "microsoft/aspire"));
    }

    internal static EvidenceBundle CreateEvidence() => new()
    {
        SchemaVersion = 1,
        Run = new CollectedWorkflowRun
        {
            Repository = "microsoft/aspire", Id = 9007199254740993, Attempt = 1, HeadSha = "failed-sha",
            Scope = RunScope.PullRequest, Event = "pull_request", HeadBranch = "feature",
            Url = "https://github.com/microsoft/aspire/actions/runs/9007199254740993",
        },
        SubjectPr = null, MainContext = null, PriorCauseIdentities = [],
        Jobs =
        [
            new JobEvidence { Id = 9007199254740992, Name = "Tests", Url = null, Conclusion = "failure", Steps = [] },
        ],
        TestEvidence = new CollectedTestEvidence { State = EvidenceState.Complete, Failures = [], Problems = [] },
    };

    private const string Proposal = """
        {"schema_version":1,"run":{"repository":"microsoft/aspire","id":"9007199254740993","attempt":1},
        "jobs":[{"job_id":"9007199254740992","classification":"code-issue","reason":"Deterministic"}],
        "tests":[],"causes":[]}
        """;

    [Fact]
    public void StrictProposalPreservesAdjacentLargeIdentifiers()
    {
        var proposal = TransientJson.Read<AnalysisProposal>(Proposal);
        Assert.Equal(9007199254740993, proposal.Run.Id);
        Assert.Equal(9007199254740992, Assert.Single(proposal.Jobs).JobId);
        Assert.Equal(proposal, TransientJson.Read<AnalysisProposal>(TransientJson.Write(proposal)) with { Jobs = proposal.Jobs, Tests = proposal.Tests, Causes = proposal.Causes });
    }

    [Theory]
    [InlineData("0")]
    [InlineData("01")]
    [InlineData("+1")]
    [InlineData("-1")]
    [InlineData(" 1")]
    [InlineData("1.0")]
    [InlineData("1e1")]
    [InlineData("9223372036854775808")]
    public void CanonicalIdentifiersRejectNoncanonicalOrOverflowingValues(string id)
    {
        Assert.Throws<ValidationException>(() => TransientJson.ParseId(id));
    }

    [Theory]
    [InlineData("\"schema_version\":1", "\"schema_version\":1,\"schema_version\":1")]
    [InlineData("\"schema_version\":1", "\"schema_version\":2")]
    [InlineData("\"schema_version\":1", "\"schema_version\":1,\"schema_versio\\u006e\":1")]
    [InlineData("\"tests\":[]", "\"tests\":null")]
    [InlineData("\"tests\":[],", "")]
    [InlineData("\"causes\":[]", "\"causes\":[],\"verdict\":\"code-issue\"")]
    [InlineData("\"code-issue\"", "\"CODE-ISSUE\"")]
    [InlineData("\"id\":\"9007199254740993\"", "\"id\":9007199254740993")]
    [InlineData("\"attempt\":1", "\"attempt\":1.0")]
    [InlineData("\"attempt\":1", "\"attempt\":0")]
    [InlineData("\"attempt\":1", "\"attempt\":-1")]
    [InlineData("\"attempt\":1", "\"attempt\":2147483648")]
    [InlineData("\"Deterministic\"", "\"bad\\ud800\"")]
    public void StrictProposalRejectsAmbiguousOrUnsupportedShapes(string original, string replacement)
    {
        Assert.Throws<ValidationException>(() => TransientJson.Read<AnalysisProposal>(Proposal.Replace(original, replacement, StringComparison.Ordinal)));
    }

    [Fact]
    public void UnknownPropertyErrorsDoNotExposeSecretValuesOrWorkflowCommands()
    {
        var text = Proposal.Replace("\"causes\":[]", "\"causes\":[],\"token=contract-secret\\n::error::injected\":true", StringComparison.Ordinal);
        var error = Assert.Throws<ValidationException>(() => TransientJson.Read<AnalysisProposal>(text));
        Assert.False(error.Message.Contains("contract-secret", StringComparison.Ordinal));
        Assert.False(error.Message.Contains('\n'));
    }
}
