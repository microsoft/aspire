// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using AnalyzeCiFailure;
using Xunit;
using static AnalyzeCiFailure.ValidationRules;

namespace Infrastructure.Tests;

/// <summary>Exercises evidence coverage, cause compatibility and attribution validation rules.</summary>
public sealed class AnalyzeCiFailureRuleTests
{
    [Theory]
    [InlineData("main", "", null, false)]
    [InlineData("pull-request", "42", "42", false)]
    [InlineData("pull-request", "42", null, true)]
    [InlineData("pull-request", "", null, true)]
    public void TrustedRunIdentityAcceptsMatchingRunAndSubject(string scope, string prNumbers, string? subject, bool explicitlyNull)
    {
        var evidence = AnalyzeCiFailureFixtures.CreateEvidence(CiFailureJson.ReadEnum<RunScope>(scope, "Invalid scope"));
        evidence = evidence with { SubjectPr = prNumbers.Length == 0 ? null : evidence.SubjectPr };
        var proposal = AnalyzeCiFailureFixtures.CreateInfrastructureProposal(evidence);
        var accepted = AnalysisValidator.Validate(evidence, proposal, "microsoft/aspire");
        Assert.Equal(prNumbers, accepted.Run.PrNumbers);
        Assert.Equal(AnalysisVerdict.TransientInfrastructure, accepted.Report.Verdict);
        Assert.Equal(evidence.SubjectPr?.Number.ToString(System.Globalization.CultureInfo.InvariantCulture), subject ?? (explicitlyNull && prNumbers.Length > 0 ? prNumbers : null));
    }

    [Theory]
    [InlineData("metadata-id")]
    [InlineData("url")]
    [InlineData("run-id")]
    [InlineData("scope")]
    [InlineData("main-subject")]
    [InlineData("ambiguous-subject")]
    [InlineData("wrong-subject")]
    [InlineData("missing-subject")]
    [InlineData("repository")]
    [InlineData("attempt")]
    public void RunIdentityRejectsReboundOrAmbiguousIdentity(string scenario)
    {
        var evidence = AnalyzeCiFailureFixtures.CreateEvidence(RunScope.PullRequest);
        var proposal = AnalyzeCiFailureFixtures.CreateInfrastructureProposal(evidence);
        var facts = JsonNode.Parse(TransientJson.Write(evidence))!;
        var decisions = JsonNode.Parse(TransientJson.Write(proposal))!;
        switch (scenario)
        {
            case "metadata-id": facts["run"]!["id"] = "456"; break;
            case "url": facts["run"]!["url"] = evidence.Run.Url + "/jobs/1"; break;
            case "run-id": decisions["run"]!["id"] = "456"; break;
            case "scope": facts["run"]!["scope"] = "main"; break;
            case "main-subject": facts["run"]!["event"] = "push"; facts["run"]!["head_branch"] = "main"; break;
            case "ambiguous-subject": facts["subject_pr"]!["number"] = "42 43"; break;
            case "wrong-subject": decisions["subject_pr_number"] = "43"; break;
            case "missing-subject": decisions["subject_pr_number"] = null; break;
            case "repository": decisions["run"]!["repository"] = "other/repository"; break;
            case "attempt": decisions["run"]!["attempt"] = 2; break;
        }
        Assert.Throws<ValidationException>(() => AnalysisValidator.Validate(
            TransientJson.Read<EvidenceBundle>(facts.ToJsonString()),
            TransientJson.Read<AnalysisProposal>(decisions.ToJsonString()),
            "microsoft/aspire"));
    }

    [Fact]
    public void JobCoverageAcceptsEveryTrustedIdInAnyOrderWithDuplicateJobNames()
    {
        var analysis = Analysis("code-issue", [Job("2", "code-issue"), Job("1", "code-issue")]);

        RequireExactJobCoverage(analysis, [TrustedJob("1", "Tests"), TrustedJob("2", "Tests")], Run("pull-request"));
    }

    [Theory]
    [InlineData("omitted")]
    [InlineData("duplicate")]
    [InlineData("unknown")]
    [InlineData("serialized-number")]
    public void JobCoverageRejectsMissingDuplicateUnknownOrRespelledIds(string scenario)
    {
        ImmutableArray<AnalysisJob> reported = scenario switch
        {
            "omitted" => [Job("1", "code-issue")],
            "duplicate" => [Job("1", "code-issue"), Job("1.0", "code-issue")],
            "unknown" => [Job("1", "code-issue"), Job("3", "code-issue")],
            _ => [Job("1", "code-issue"), Job("2.0", "code-issue")],
        };

        var error = Assert.Throws<ValidationException>(() => RequireExactJobCoverage(
            Analysis("code-issue", reported),
            [TrustedJob("1", "Tests"), TrustedJob("2", "Build")],
            Run("pull-request")));

        Assert.Equal("Analysis failed-job IDs do not match the trusted failed jobs", error.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("unrecognized")]
    public void JobCoverageRequiresRecognizedNonemptyClassifications(string classification)
    {
        ImmutableArray<AnalysisJob> jobs = classification.Length == 0 ? [] : [Job("1", classification)];

        var error = Assert.Throws<ValidationException>(() => RequireExactJobCoverage(
            Analysis("mixed", jobs),
            [TrustedJob("1", "Tests")],
            Run("pull-request")));

        Assert.Equal("Analysis must classify every failed job with a recognized classification", error.Message);
    }

    [Theory]
    [InlineData("main", "code-issue")]
    [InlineData("pull-request", "main-repository-breakage")]
    public void JobCoverageRejectsClassificationFromTheOtherScope(string scope, string classification)
    {
        var error = Assert.Throws<ValidationException>(() => RequireExactJobCoverage(
            Analysis("mixed", [Job("1", classification)]),
            [TrustedJob("1", "Tests")],
            Run(scope)));

        Assert.Equal($"Analysis contains a failed-job classification that is not permitted for run scope {scope}", error.Message);
    }

    [Fact]
    public void TestCoverageAcceptsExactParameterizedNamesAndJobPairsInAnyOrder()
    {
        var analysis = Analysis(
            "flaky-test",
            [Job("1", "flaky-test"), Job("2", "flaky-test")],
            [Test("Suite.Test(value: \"a:b\")", "Linux"), Test("Suite.Test(value: \"a:b\")", "Windows", jobId: 2), Test("Suite.Test(value: \"A:B\")", "Linux")]);
        ImmutableArray<TestIdentity> collected =
        [
            new("Suite.Test(value: \"A:B\")", "Linux", NormalizedJsonNumber.Parse("1")),
            new("Suite.Test(value: \"a:b\")", "Windows", NormalizedJsonNumber.Parse("2")),
            new("Suite.Test(value: \"a:b\")", "Linux", NormalizedJsonNumber.Parse("1")),
        ];

        RequireExactTestCoverage(analysis, collected, [TrustedJob("1", "Linux"), TrustedJob("2", "Windows")]);
    }

    [Theory]
    [InlineData("omitted")]
    [InlineData("duplicate")]
    [InlineData("swapped")]
    [InlineData("parameter-changed")]
    [InlineData("unknown-job")]
    [InlineData("duplicate-trusted")]
    [InlineData("missing-job-id")]
    public void TestCoverageRejectsAnythingOtherThanExactUniqueTrustedPairs(string scenario)
    {
        ImmutableArray<AnalysisTest> tests = [Test("Suite.Test(1)", "Linux"), Test("Suite.Test(2)", "Windows", jobId: 2)];
        ImmutableArray<TestIdentity> collected = [new("Suite.Test(1)", "Linux", NormalizedJsonNumber.Parse("1")), new("Suite.Test(2)", "Windows", NormalizedJsonNumber.Parse("2"))];
        ImmutableArray<FailedJob> jobs = [TrustedJob("1", "Linux"), TrustedJob("2", "Windows")];
        switch (scenario)
        {
            case "omitted": tests = [tests[0]]; break;
            case "duplicate": tests = [tests[0], tests[0], tests[1]]; break;
            case "swapped": tests = [tests[0] with { Job = "Windows" }, tests[1] with { Job = "Linux" }]; break;
            case "parameter-changed": tests = [tests[0], tests[1] with { Name = "Suite.Test(3)" }]; break;
            case "unknown-job": jobs = [jobs[0]]; break;
            case "duplicate-trusted": collected = [collected[0], collected[0], collected[1]]; break;
            case "missing-job-id": tests = [tests[0] with { JobId = null }, tests[1]]; break;
        }

        var error = Assert.Throws<ValidationException>(() => RequireExactTestCoverage(Analysis("flaky-test", [], tests), collected, jobs));

        Assert.Equal("Analysis failed_tests do not match trusted test failure evidence", error.Message);
    }

    [Fact]
    public void TestCoverageAcceptsNoReportedTestsWhenNoneAreTrusted()
    {
        RequireExactTestCoverage(Analysis("code-issue", [Job("1", "code-issue")]), [], [TrustedJob("1", "Build")]);
    }

    [Theory]
    [InlineData("empty-jobs")]
    [InlineData("duplicate-jobs")]
    [InlineData("infra-test-observations")]
    public void CauseFieldsRejectMissingOrDuplicateJobsAndInfraTestIdentity(string scenario)
    {
        var cause = scenario switch
        {
            "empty-jobs" => Cause("infra", "infra-failure", "", []),
            "duplicate-jobs" => Cause("infra", "infra-failure", "", ["1", "1.0"]),
            _ => Cause("infra", "infra-failure", "", ["1"]) with { Tests = [new TestObservation("Suite.Test(1)", NormalizedJsonNumber.Parse("1"))] },
        };

        var error = Assert.Throws<ValidationException>(() => RequireCauseFields(cause));

        Assert.Equal("Cause infra.json contains unsupported or publisher-owned fields", error.Message);
    }

    [Theory]
    [InlineData("pull-request", "transient-infra", "infra-failure")]
    [InlineData("main", "main-repository-breakage", "main-repository-breakage")]
    [InlineData("pull-request", "flaky-test", "flaky-test")]
    public void CauseCompatibilityAcceptsMatchingClassificationAndScope(string scope, string classification, string type)
    {
        var cause = Cause("cause", type, "", ["1"]);
        var analysis = Analysis("mixed", [Job("1", classification)], causeIds: ["cause"]);

        RequireCauseFields(cause);
        ValidateCauseCompatibility(cause, analysis, [TrustedJob("1", "Tests")], Run(scope));
    }

    [Theory]
    [InlineData("infra-failure", "code-issue", "1")]
    [InlineData("infra-failure", "flaky-test", "1")]
    [InlineData("main-repository-breakage", "transient-infra", "1")]
    [InlineData("flaky-test", "code-issue", "1")]
    [InlineData("flaky-test", "main-repository-breakage", "1")]
    [InlineData("infra-failure", "transient-infra", "2")]
    public void CausesCannotReferenceUnknownOrIncompatibleJobs(string type, string classification, string causeJob)
    {
        var cause = Cause("cause", type, "", [causeJob]);
        var analysis = Analysis("mixed", [Job("1", classification)], causeIds: ["cause"]);

        var error = Assert.Throws<ValidationException>(() => ValidateCauseCompatibility(cause, analysis, [TrustedJob("1", "Tests")], Run("main")));

        Assert.Equal("Cause cause.json references an unknown or incompatible failed job", error.Message);
    }

    [Fact]
    public void FlakyCauseCannotUseTransientEvidenceFromAnotherJob()
    {
        var analysis = Analysis(
            "mixed",
            [Job("1", "code-issue"), Job("2", "code-issue")],
            [Test("Suite.Test(1)", "Windows", jobId: 2)],
            ["flaky"]);

        var error = Assert.Throws<ValidationException>(() => ValidateCauseCompatibility(
            Cause("flaky", "flaky-test", "Suite.Test(1)", ["1"]),
            analysis,
            [TrustedJob("1", "Linux"), TrustedJob("2", "Windows")],
            Run("pull-request")));

        Assert.Equal("Cause flaky.json references an unknown or incompatible failed job", error.Message);
    }

    [Theory]
    [InlineData("main-repository-breakage", "pull-request")]
    [InlineData("code-issue", "main")]
    [InlineData("unknown", "main")]
    public void CausesCannotUseTypesOutsideTheirRunScope(string type, string scope)
    {
        var cause = Cause("cause", type, "", ["1"]);
        var analysis = Analysis("mixed", [Job("1", "main-repository-breakage")], causeIds: ["cause"]);

        var error = Assert.Throws<ValidationException>(() => ValidateCauseCompatibility(cause, analysis, [TrustedJob("1", "Tests")], Run(scope)));

        var displayedType = type is "code-issue" or "unknown" ? "0" : type;
        Assert.Equal($"Cause cause.json type {displayedType} is not permitted for run scope {scope}", error.Message);
    }

    [Fact]
    public void EveryCauseMustBeReferencedByTheSummary()
    {
        var error = Assert.Throws<ValidationException>(() => ValidateCauseCompatibility(
            Cause("infra", "infra-failure", "", ["1"]),
            Analysis("transient-infra", [Job("1", "transient-infra")]),
            [TrustedJob("1", "Build")],
            Run("main")));

        Assert.Equal("Cause infra.json is not referenced by the analysis summary", error.Message);
    }

    [Theory]
    [InlineData("flaky-test", "Suite.Test(1)", "Suite.Test(1)")]
    [InlineData("flaky-test", "Suite.Test(1)", "Suite.Test(2)")]
    [InlineData("flaky-test", "Suite.Test(1)", "")]
    [InlineData("infra-failure", "", "legacy-name")]
    [InlineData("main-repository-breakage", "current-pattern", "old-pattern")]
    public void StoredCauseIdentityChecksTypeNotDescriptiveLegacyLabel(string type, string currentTest, string priorTest)
    {
        RequireUnchangedCauseType(Cause("cause", type, currentTest, ["1"]), new StoredCauseIdentity(Value<CauseType>(type), priorTest));
    }

    [Theory]
    [InlineData("infra-failure", "Suite.Test(1)", "Cause cause.json cannot change type from infra-failure to flaky-test")]
    public void StoredCauseIdentityRejectsTypeChanges(string priorType, string priorTest, string message)
    {
        var error = Assert.Throws<ValidationException>(() => RequireUnchangedCauseType(
            Cause("cause", "flaky-test", "Suite.Test(1)", ["1"]),
            new StoredCauseIdentity(Value<CauseType>(priorType), priorTest)));

        Assert.Equal(message, error.Message);
    }

    [Theory]
    [InlineData("main", "main-repository-breakage")]
    [InlineData("pull-request", "code-issue")]
    [InlineData("main", "transient-infra")]
    [InlineData("pull-request", "flaky-test")]
    [InlineData("main", "mixed")]
    public void VerdictMayUseTheRunScopesClassifications(string scope, string verdict)
    {
        RequirePermittedVerdict(Analysis(verdict, []), Run(scope));
    }

    [Theory]
    [InlineData("main", "code-issue")]
    [InlineData("pull-request", "main-repository-breakage")]
    [InlineData("unknown", "mixed")]
    [InlineData("main", "unknown")]
    public void VerdictCannotUseAnotherScopeOrUnknownClassification(string scope, string verdict)
    {
        var error = Assert.Throws<ValidationException>(() => RequirePermittedVerdict(Analysis(verdict, []), Run(scope)));

        var displayedVerdict = verdict == "unknown" ? "0" : verdict;
        var displayedScope = scope == "unknown" ? "0" : scope;
        Assert.Equal($"Verdict {displayedVerdict} is not permitted for run scope {displayedScope}", error.Message);
    }

    [Theory]
    [InlineData("main", "main-repository-breakage")]
    [InlineData("pull-request", "code-issue")]
    public void MixedVerdictAcceptsDeterministicAndFlakyFailuresWithinOneJob(string scope, string classification)
    {
        ImmutableArray<FailureCause> causes = scope == "main"
            ? [Cause("main", "main-repository-breakage", "", ["1"]), Cause("flaky", "flaky-test", "Suite.Test(1)", ["1"])]
            : [Cause("flaky", "flaky-test", "Suite.Test(1)", ["1"])];
        var analysis = Analysis(
            "mixed",
            [Job("1", classification)],
            [Test("Suite.Test(1)", "Tests"), Test("Suite.Test(2)", "Tests", "code-issue")],
            causes.Select(c => c.Id).ToImmutableArray());
        ImmutableArray<FailedJob> jobs = [TrustedJob("1", "Tests")];

        RequireExactJobCoverage(analysis, jobs, Run(scope));
        foreach (var cause in causes)
        {
            ValidateCauseCompatibility(cause, analysis, jobs, Run(scope));
        }
        ValidateVerdict(analysis, causes, Run(scope));
        RequireCauseCoverage(analysis, causes);
    }

    [Theory]
    [InlineData("main", "main-repository-breakage", "A mixed verdict for main requires a main-breakage job and cause plus transient job or test evidence and cause")]
    [InlineData("pull-request", "code-issue", "A mixed verdict for a pull request requires a code-issue job plus transient job or test evidence and a transient cause")]
    public void MixedVerdictRequiresTransientEvidenceAndCause(string scope, string classification, string message)
    {
        var analysis = Analysis("mixed", [Job("1", classification)]);
        ImmutableArray<FailureCause> causes = scope == "main" ? [Cause("main", "main-repository-breakage", "", ["1"])] : [];

        var error = Assert.Throws<ValidationException>(() => ValidateVerdict(analysis, causes, Run(scope)));

        Assert.Equal(message, error.Message);
    }

    [Fact]
    public void CodeIssueVerdictAcceptsZeroCauses()
    {
        var analysis = Analysis("code-issue", [Job("1", "code-issue")], [Test("Suite.Test(1)", "Tests", "code-issue")]);

        ValidateVerdict(analysis, [], Run("pull-request"));
        RequireCauseCoverage(analysis, []);
    }

    [Theory]
    [InlineData("transient-infra", "infra-failure")]
    [InlineData("flaky-test", "flaky-test")]
    [InlineData("main-repository-breakage", "main-repository-breakage")]
    public void SingleClassificationVerdictAcceptsMatchingJobsAndCauses(string classification, string type)
    {
        ValidateVerdict(
            Analysis(classification, [Job("1", classification)]),
            [Cause("cause", type, "", ["1"])],
            Run("main"));
    }

    [Fact]
    public void FlakyVerdictAllowsInfrastructureFailuresAlongsideFlakyFailures()
    {
        ValidateVerdict(
            Analysis("flaky-test", [Job("1", "flaky-test"), Job("2", "transient-infra")]),
            [Cause("flaky", "flaky-test", "", ["1"]), Cause("infra", "infra-failure", "", ["2"])],
            Run("pull-request"));
    }

    [Theory]
    [InlineData("missing-infra-cause")]
    [InlineData("extra-flaky-cause")]
    [InlineData("extra-main-cause")]
    public void PersistedCauseTypesMustAgreeWithObservedClassifications(string scenario)
    {
        var analysis = scenario == "missing-infra-cause"
            ? Analysis("flaky-test", [Job("1", "flaky-test"), Job("2", "transient-infra")])
            : Analysis("mixed", [Job("1", "code-issue"), Job("2", "transient-infra")]);
        ImmutableArray<FailureCause> causes = scenario switch
        {
            "missing-infra-cause" => [Cause("flaky", "flaky-test", "", ["1"])],
            "extra-flaky-cause" => [Cause("infra", "infra-failure", "", ["2"]), Cause("flaky", "flaky-test", "", ["1"])],
            _ => [Cause("infra", "infra-failure", "", ["2"]), Cause("main", "main-repository-breakage", "", ["1"])],
        };

        var error = Assert.Throws<ValidationException>(() => ValidateVerdict(analysis, causes, Run("pull-request")));

        Assert.Equal("Failed-job classifications and persisted cause types do not match", error.Message);
    }

    [Theory]
    [InlineData("transient-infra", "A transient-infra verdict requires every failed job and cause to be an infrastructure failure")]
    [InlineData("flaky-test", "A flaky-test verdict requires at least one flaky job, only transient failed jobs, and only transient causes")]
    [InlineData("main-repository-breakage", "A main-repository-breakage verdict requires every failed job and cause to be a main repository breakage")]
    public void PersistedFailureVerdictsCannotHaveZeroCauses(string classification, string message)
    {
        var error = Assert.Throws<ValidationException>(() => ValidateVerdict(
            Analysis(classification, [Job("1", classification)]),
            [],
            Run("main")));

        Assert.Equal(message, error.Message);
    }

    [Theory]
    [InlineData("transient-infra", "code-issue")]
    [InlineData("flaky-test", "code-issue")]
    [InlineData("code-issue", "flaky")]
    [InlineData("main-repository-breakage", "flaky")]
    public void VerdictRejectsContradictoryTestClassifications(string verdict, string testClassification)
    {
        var error = Assert.Throws<ValidationException>(() => ValidateVerdict(
            Analysis(verdict, [Job("1", verdict)], [Test("Suite.Test(1)", "Tests", testClassification)]),
            [],
            Run("main")));

        Assert.Equal($"Analysis failed_tests are incompatible with verdict {verdict}", error.Message);
    }

    [Fact]
    public void DecisionProposalRejectsRepeatedCauseIds()
    {
        var evidence = AnalyzeCiFailureFixtures.CreateEvidence(RunScope.PullRequest);
        var proposal = AnalyzeCiFailureFixtures.CreateInfrastructureProposal(evidence);
        proposal = proposal with { Causes = [proposal.Causes[0], proposal.Causes[0]] };
        var error = Assert.Throws<ValidationException>(() => AnalysisValidator.Validate(evidence, proposal, "microsoft/aspire"));

        Assert.Equal("Cause IDs must be unique within the proposal.", error.Message);
    }

    [Fact]
    public void FlakyCoverageAllowsDuplicateJobNamesAndMultipleMatchingCauses()
    {
        ImmutableArray<FailedJob> jobs = [TrustedJob("1", "Tests"), TrustedJob("2", "Tests")];
        ImmutableArray<FailureCause> causes =
        [
            Cause("first", "flaky-test", "Suite.Test(1)", ["1", "2"]),
            Cause("second", "flaky-test", "Suite.Test(1)", ["1"]),
        ];
        var analysis = Analysis(
            "flaky-test",
            [Job("1", "flaky-test"), Job("2", "flaky-test")],
            [Test("Suite.Test(1)", "Tests", jobId: 1),
             Test("Suite.Test(1)", "Tests", jobId: 2)],
            ["first", "second"]);

        foreach (var cause in causes)
        {
            ValidateCauseCompatibility(cause, analysis, jobs, Run("pull-request"));
        }
        ValidateVerdict(analysis, causes, Run("pull-request"));
        RequireCauseCoverage(analysis, causes);
    }

    [Theory]
    [InlineData("transient-infra", "infra-failure")]
    [InlineData("flaky-test", "flaky-test")]
    [InlineData("main-repository-breakage", "main-repository-breakage")]
    public void EveryPersistedFailedJobRequiresItsOwnMatchingCause(string classification, string type)
    {
        var analysis = Analysis("mixed", [Job("1", classification), Job("2", classification)]);
        var error = Assert.Throws<ValidationException>(() => RequireCauseCoverage(
            analysis,
            [Cause("cause", type, "", ["1"])]));

        Assert.Equal("Every transient, flaky, and main-breakage failed job must be covered by a matching cause", error.Message);
    }

    [Fact]
    public void FlakyCauseRequiresExactParameterizedTestIdentity()
    {
        var error = Assert.Throws<ValidationException>(() => RequireCauseCoverage(
            Analysis("flaky-test", [Job("1", "flaky-test")], [Test("Suite.Test(1)", "Tests")]),
            [Cause("flaky", "flaky-test", "Suite.Test(2)", ["1"])]));

        Assert.Equal("Cause test observation must reference a validated flaky test in its trusted job", error.Message);
    }

    [Fact]
    public void FlakyCauseCannotRebindTestToAnotherFailedJob()
    {
        var error = Assert.Throws<ValidationException>(() => RequireCauseCoverage(
            Analysis("mixed", [Job("1", "code-issue"), Job("2", "code-issue")], [Test("Suite.Test(1)", "Windows", jobId: 2)]),
            [Cause("flaky", "flaky-test", "Suite.Test(1)", ["1"])]));

        Assert.Equal("Cause test observation must reference a validated flaky test in its trusted job", error.Message);
    }

    [Fact]
    public void EveryFlakyTestJobPairRequiresAtLeastOneMatchingCause()
    {
        var error = Assert.Throws<ValidationException>(() => RequireCauseCoverage(
            Analysis(
                "mixed",
                [Job("1", "code-issue"), Job("2", "code-issue")],
                [Test("Suite.Test(1)", "Linux"), Test("Suite.Test(1)", "Windows", jobId: 2)]),
            [Cause("flaky", "flaky-test", "Suite.Test(1)", ["1"])]));

        Assert.Equal("Every flaky test and job observation must be covered by a matching cause", error.Message);
    }

    [Fact]
    public void ZeroCauseCoverageRetainsTheExistingNoCauseTestJoin()
    {
        // Verdict validation rejects a mixed/flaky proposal without causes separately.
        RequireCauseCoverage(Analysis("mixed", [Job("1", "code-issue")], [Test("Suite.Test(1)", "Tests")]), []);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(9)]
    [InlineData(10)]
    public void IndividualPublicationHandlesCountsThroughTen(int causeCount)
    {
        Assert.False(UsesAggregatePublication(causeCount));
    }

    [Theory]
    [InlineData(11)]
    [InlineData(12)]
    public void AggregatePublicationHandlesCountsAboveTen(int causeCount)
    {
        Assert.True(UsesAggregatePublication(causeCount));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(64999)]
    [InlineData(65000)]
    public void CommentBudgetAcceptsUpTo65000RenderedBytes(int bytes)
    {
        RequireCommentBudget(bytes);
    }

    [Theory]
    [InlineData(null, "Unable to render the PR comment during validation")]
    [InlineData(65001, "Rendered PR comment exceeds the 65000-byte publication budget")]
    public void CommentBudgetRejectsRenderFailureOrOversize(int? bytes, string message)
    {
        Assert.Equal(message, Assert.Throws<ValidationException>(() => RequireCommentBudget(bytes)).Message);
    }

    private static ValidatedRun Run(string scope) => new(Value<RunScope>(scope), "https://github.com/microsoft/aspire/actions/runs/123", scope == "main" ? "" : "42");
    private static AnalysisJob Job(string id, string classification) => new() { Id = CiFailureJson.ToContract<System.Text.Json.JsonElement>(CiFailureJson.Parse(id)), Classification = Value<JobClassification>(classification) };
    private static FailedJob TrustedJob(string id, string name) => new(NormalizedJsonNumber.Parse(id), id, name);
    private static AnalysisTest Test(string name, string job, string classification = "flaky", long jobId = 1)
        => new() { Name = name, Job = job, JobId = JsonSerializer.SerializeToElement(jobId), Classification = Value<TestClassification>(classification) };
    private static FailureCause Cause(string id, string type, string testName, ImmutableArray<string> jobIds)
        => new(
            id,
            id + ".json",
            Value<CauseType>(type),
            jobIds.Select(NormalizedJsonNumber.Parse).ToImmutableArray(),
            type == "flaky-test" ? jobIds.Select(jobId => new TestObservation(testName, NormalizedJsonNumber.Parse(jobId))).ToImmutableArray() : default);
    private static AnalysisReport Analysis(
        string verdict,
        ImmutableArray<AnalysisJob> jobs,
        ImmutableArray<AnalysisTest> tests = default,
        ImmutableArray<string> causeIds = default)
        => new() { Verdict = Value<AnalysisVerdict>(verdict), Jobs = jobs, Tests = tests.IsDefault ? [] : tests, Causes = causeIds.IsDefault ? [] : causeIds };

    // Negative cases deliberately use an undefined enum; malformed strings are
    // now rejected at the JSON boundary before reaching the domain rules.
    private static T Value<T>(string text) where T : struct, Enum
        => CiFailureJson.TryReadEnum<T>(text, out var value) ? value : default;
}
