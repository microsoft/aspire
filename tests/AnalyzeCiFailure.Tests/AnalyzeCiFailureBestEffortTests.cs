// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO.Compression;
using System.Text.Json.Nodes;
using AnalyzeCiFailure;
using Xunit;

namespace Infrastructure.Tests;

/// <summary>Checks retained partial results and cumulative work budgets across failed acquisitions.</summary>
public sealed partial class AnalyzeCiFailureWorkflowTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConfirmedRunnerLossCannotBecomeRepositoryBreakageWithoutObservedTests(bool runnerLost)
    {
        var evidence = AnalyzeCiFailureFixtures.CreateEvidence(RunScope.Main);
        evidence = evidence with
        {
            Jobs = [evidence.Jobs[0] with { RunnerFailure = runnerLost ? WorkflowRunEvidenceCollector.RunnerLostAnnotation : null }],
        };
        var proposal = AnalyzeCiFailureFixtures.CreateInfrastructureProposal(evidence) with
        {
            Jobs = [new ProposedJobDecision { JobId = 123, Classification = JobClassification.MainRepositoryBreakage, Reason = "Claimed repository failure" }],
            Causes =
            [
                new NewCauseProposal
                {
                    Definition = new ProposedCauseDefinition { Id = "claimed-breakage", Type = CauseType.MainRepositoryBreakage, Title = "Claimed breakage", ErrorPattern = "Build failed" },
                    JobIds = [123], TestRefs = [], Reason = "Claimed repository failure",
                },
            ],
        };
        if (runnerLost)
        {
            var error = Assert.Throws<ValidationException>(() => AnalysisValidator.Validate(evidence, proposal, "microsoft/aspire"));
            Assert.Contains("confirmed runner-loss", error.Message, StringComparison.Ordinal);
            return;
        }
        Assert.Equal(AnalysisVerdict.MainRepositoryBreakage, AnalysisValidator.Validate(evidence, proposal, "microsoft/aspire").Report.Verdict);
    }

    [Fact]
    public async Task MalformedFileDoesNotDiscardTheOtherReportsInItsArtifact()
    {
        var root = Path.Combine(_workspace.Path, "partial-results");
        var directory = Directory.CreateDirectory(Path.Combine(root, "10")).FullName;
        await File.WriteAllTextAsync(Path.Combine(directory, "00001.trx"), FrameworkResult("Tests.First", "first", "first"));
        await File.WriteAllTextAsync(Path.Combine(directory, "00002.trx"), "malformed");
        await File.WriteAllTextAsync(Path.Combine(directory, "00003.trx"), FrameworkResult("Tests.Last", "last", "last"));
        var evidence = AnalyzeCiFailureFixtures.CreateEvidence(RunScope.PullRequest);
        var artifact = new SelectedTestArtifact(
            new WorkflowArtifact(JsonValue.Create(10), "results", JsonValue.Create(1), false, "2026-10-29T00:00:00Z"),
            new TestArtifactExpectation("Tests", "results", TestArtifactFormat.Trx, TestEvidencePolicy.Required));
        var problems = new List<string>();

        var failures = TestFailureReader.ConsolidateFailures(
            TestFailureReader.ReadAvailable(root, evidence.Jobs, [artifact], problems.Add, reportNonTestFailure: (_, _) => { }));
        Assert.Equal(["Tests.First", "Tests.Last"], failures.Select(failure => failure.Name));
        Assert.Contains("00002.trx", Assert.Single(problems), StringComparison.Ordinal);
        var partial = evidence with
        {
            TestEvidence = evidence.TestEvidence with { State = EvidenceState.Unavailable, Failures = failures },
        };
        var reloaded = TransientJson.Read<EvidenceBundle>(TransientJson.Write(partial));
        AnalysisValidator.ValidateBundle(reloaded, "microsoft/aspire");
        var error = Assert.Throws<ValidationException>(() => AnalysisValidator.Validate(
            reloaded, AnalyzeCiFailureFixtures.CreateInfrastructureProposal(reloaded), "microsoft/aspire"));
        Assert.Contains("publication and reruns are not authorized", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailedExtractionsConsumeTheSharedExpansionBudget()
    {
        var archivePath = Path.Combine(_workspace.Path, "failed-expansion.zip");
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            await WriteZipEntryAsync(archive, "results.trx", "12345678");
            await WriteZipEntryAsync(archive, "../unsafe", "unsafe");
        }
        long remaining = 10;

        Assert.Throws<ValidationException>(() => TestResultArchiveExtractor.ExtractTracked(
            archivePath,
            Path.Combine(_workspace.Path, "first"),
            remaining,
            expectedBytes: null,
            format: "trx",
            count => remaining -= count));
        Assert.Equal(2, remaining);
        Assert.Throws<ValidationException>(() => TestResultArchiveExtractor.ExtractTracked(
            archivePath,
            Path.Combine(_workspace.Path, "second"),
            remaining,
            expectedBytes: null,
            format: "trx",
            count => remaining -= count));
        Assert.Equal(-1, remaining);
    }
}
