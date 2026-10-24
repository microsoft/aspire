// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using AnalyzeCiFailure;
using Xunit;
using static AnalyzeCiFailure.ValidationRules;

namespace Infrastructure.Tests;

public sealed class AnalyzeCiFailureRuleTests
{
    [Theory]
    [InlineData("main", "", null, false)]
    [InlineData("pull-request", "42", "42", false)]
    [InlineData("pull-request", "42", null, true)]
    [InlineData("pull-request", "", null, true)]
    public void TrustedRunIdentityAcceptsMatchingRunAndSubject(string scope, string prNumbers, string? subject, bool explicitlyNull)
    {
        var trusted = new TrustedRunIdentity("123", "123", scope, "https://github.com/microsoft/aspire/actions/runs/123", prNumbers);
        var proposed = new ProposedRunIdentity("123", scope, false, subject, explicitlyNull);

        RequireRunIdentity(trusted, proposed);
    }

    [Theory]
    [InlineData("metadata-id", "Trusted run metadata is invalid")]
    [InlineData("url", "Trusted run metadata is invalid")]
    [InlineData("run-id", "Analysis result does not match trusted run context")]
    [InlineData("scope", "Analysis result does not match trusted run context")]
    [InlineData("main-subject", "Main run analysis must not identify a subject PR")]
    [InlineData("ambiguous-subject", "Trusted run context must contain one unambiguous subject PR")]
    [InlineData("wrong-subject", "Pull request analysis must identify a trusted subject PR")]
    [InlineData("missing-subject", "Pull request analysis must identify a trusted subject PR")]
    public void RunIdentityRejectsReboundOrAmbiguousIdentity(string scenario, string message)
    {
        var trusted = new TrustedRunIdentity("123", "123", "pull-request", "https://github.com/microsoft/aspire/actions/runs/123", "42");
        var proposed = new ProposedRunIdentity("123", "pull-request", false, "42", false);
        switch (scenario)
        {
            case "metadata-id": trusted = trusted with { MetadataId = "456" }; break;
            case "url": trusted = trusted with { Url = trusted.Url + "/jobs/1" }; break;
            case "run-id": proposed = proposed with { Id = "456" }; break;
            case "scope": proposed = proposed with { Scope = "main" }; break;
            case "main-subject":
                trusted = trusted with { Scope = "main" };
                proposed = proposed with { Scope = "main", HasMainSubjectPr = true };
                break;
            case "ambiguous-subject": trusted = trusted with { PrNumbers = "42 43" }; break;
            case "wrong-subject": proposed = proposed with { SubjectPrNumber = "43" }; break;
            case "missing-subject": proposed = proposed with { SubjectPrNumber = null }; break;
        }

        Assert.Equal(message, Assert.Throws<ValidationException>(() => RequireRunIdentity(trusted, proposed)).Message);
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
        ImmutableArray<ClassifiedJob> reported = scenario switch
        {
            "omitted" => [Job("1", "code-issue")],
            "duplicate" => [Job("1", "code-issue"), Job("1.0", "code-issue")],
            "unknown" => [Job("1", "code-issue"), Job("3", "code-issue")],
            _ => [Job("1", "code-issue"), Job("2.0", "code-issue")],
        };

        var error = Assert.Throws<ValidationException>(() => RequireExactJobCoverage(
            Analysis("code-issue", reported), [TrustedJob("1", "Tests"), TrustedJob("2", "Build")], Run("pull-request")));

        Assert.Equal("Analysis failed-job IDs do not match the trusted failed jobs", error.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("unrecognized")]
    public void JobCoverageRequiresRecognizedNonemptyClassifications(string classification)
    {
        ImmutableArray<ClassifiedJob> jobs = classification.Length == 0 ? [] : [Job("1", classification)];

        var error = Assert.Throws<ValidationException>(() => RequireExactJobCoverage(
            Analysis("mixed", jobs), [TrustedJob("1", "Tests")], Run("pull-request")));

        Assert.Equal("Analysis must classify every failed job with a recognized classification", error.Message);
    }

    [Theory]
    [InlineData("main", "code-issue")]
    [InlineData("pull-request", "main-repository-breakage")]
    public void JobCoverageRejectsClassificationFromTheOtherScope(string scope, string classification)
    {
        var error = Assert.Throws<ValidationException>(() => RequireExactJobCoverage(
            Analysis("mixed", [Job("1", classification)]), [TrustedJob("1", "Tests")], Run(scope)));

        Assert.Equal($"Analysis contains a failed-job classification that is not permitted for run scope {scope}", error.Message);
    }

    [Fact]
    public void TestCoverageAcceptsExactParameterizedNamesAndJobPairsInAnyOrder()
    {
        var analysis = Analysis("flaky-test", [Job("1", "flaky-test"), Job("2", "flaky-test")],
            [Test("Suite.Test(value: \"a:b\")", "Linux"), Test("Suite.Test(value: \"a:b\")", "Windows"), Test("Suite.Test(value: \"A:B\")", "Linux")]);
        ImmutableArray<TestIdentity> trusted =
        [
            new("Suite.Test(value: \"A:B\")", "Linux"),
            new("Suite.Test(value: \"a:b\")", "Windows"),
            new("Suite.Test(value: \"a:b\")", "Linux"),
        ];

        RequireExactTestCoverage(analysis, trusted, [TrustedJob("1", "Linux"), TrustedJob("2", "Windows")]);
    }

    [Theory]
    [InlineData("omitted")]
    [InlineData("duplicate")]
    [InlineData("swapped")]
    [InlineData("parameter-changed")]
    [InlineData("unknown-job")]
    [InlineData("duplicate-trusted")]
    public void TestCoverageRejectsAnythingOtherThanExactUniqueTrustedPairs(string scenario)
    {
        ImmutableArray<FailedTest> tests = [Test("Suite.Test(1)", "Linux"), Test("Suite.Test(2)", "Windows")];
        ImmutableArray<TestIdentity> trusted = [new("Suite.Test(1)", "Linux"), new("Suite.Test(2)", "Windows")];
        ImmutableArray<FailedJob> jobs = [TrustedJob("1", "Linux"), TrustedJob("2", "Windows")];
        switch (scenario)
        {
            case "omitted": tests = [tests[0]]; break;
            case "duplicate": tests = [tests[0], tests[0], tests[1]]; break;
            case "swapped": tests = [tests[0] with { Job = "Windows" }, tests[1] with { Job = "Linux" }]; break;
            case "parameter-changed": tests = [tests[0], tests[1] with { Name = "Suite.Test(3)" }]; break;
            case "unknown-job": jobs = [jobs[0]]; break;
            case "duplicate-trusted": trusted = [trusted[0], trusted[0], trusted[1]]; break;
        }

        var error = Assert.Throws<ValidationException>(() => RequireExactTestCoverage(Analysis("flaky-test", [], tests), trusted, jobs));

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
    [InlineData("infra-test-name")]
    public void CauseFieldsRejectMissingOrDuplicateJobsAndInfraTestIdentity(string scenario)
    {
        var cause = scenario switch
        {
            "empty-jobs" => Cause("infra", "infra-failure", "", []),
            "duplicate-jobs" => Cause("infra", "infra-failure", "", ["1", "1.0"]),
            _ => Cause("infra", "infra-failure", "Suite.Test(1)", ["1"]),
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
        var analysis = Analysis("mixed", [Job("1", "code-issue"), Job("2", "code-issue")],
            [Test("Suite.Test(1)", "Windows")], ["flaky"]);

        var error = Assert.Throws<ValidationException>(() => ValidateCauseCompatibility(
            Cause("flaky", "flaky-test", "Suite.Test(1)", ["1"]), analysis,
            [TrustedJob("1", "Linux"), TrustedJob("2", "Windows")], Run("pull-request")));

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

        Assert.Equal($"Cause cause.json type {type} is not permitted for run scope {scope}", error.Message);
    }

    [Fact]
    public void EveryCauseMustBeReferencedByTheSummary()
    {
        var error = Assert.Throws<ValidationException>(() => ValidateCauseCompatibility(
            Cause("infra", "infra-failure", "", ["1"]), Analysis("transient-infra", [Job("1", "transient-infra")]),
            [TrustedJob("1", "Build")], Run("main")));

        Assert.Equal("Cause infra.json is not referenced by the analysis summary", error.Message);
    }

    [Theory]
    [InlineData("flaky-test", "Suite.Test(1)", "Suite.Test(1)")]
    [InlineData("infra-failure", "", "legacy-name")]
    [InlineData("main-repository-breakage", "current-pattern", "old-pattern")]
    public void StoredCauseIdentityAllowsUnchangedTypeAndExactFlakyTest(string type, string currentTest, string priorTest)
    {
        RequireUnchangedCauseIdentity(Cause("cause", type, currentTest, ["1"]), new StoredCauseIdentity(type, priorTest));
    }

    [Theory]
    [InlineData("infra-failure", "Suite.Test(1)", "Cause cause.json cannot change type from infra-failure to flaky-test")]
    [InlineData("flaky-test", "Suite.Test(2)", "Cause cause.json cannot change stored test_name")]
    [InlineData("flaky-test", "", "Cause cause.json cannot change stored test_name")]
    public void StoredCauseIdentityRejectsTypeOrParameterizedTestChanges(string priorType, string priorTest, string message)
    {
        var error = Assert.Throws<ValidationException>(() => RequireUnchangedCauseIdentity(
            Cause("cause", "flaky-test", "Suite.Test(1)", ["1"]), new StoredCauseIdentity(priorType, priorTest)));

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

        Assert.Equal($"Verdict {verdict} is not permitted for run scope {scope}", error.Message);
    }

    [Theory]
    [InlineData("main", "main-repository-breakage")]
    [InlineData("pull-request", "code-issue")]
    public void MixedVerdictAcceptsDeterministicAndFlakyFailuresWithinOneJob(string scope, string classification)
    {
        ImmutableArray<FailureCause> causes = scope == "main"
            ? [Cause("main", "main-repository-breakage", "", ["1"]), Cause("flaky", "flaky-test", "Suite.Test(1)", ["1"])]
            : [Cause("flaky", "flaky-test", "Suite.Test(1)", ["1"])];
        var analysis = Analysis("mixed", [Job("1", classification)],
            [Test("Suite.Test(1)", "Tests"), Test("Suite.Test(2)", "Tests", "code-issue")],
            causes.Select(c => c.Id).ToImmutableArray());
        ImmutableArray<FailedJob> jobs = [TrustedJob("1", "Tests")];

        RequireExactJobCoverage(analysis, jobs, Run(scope));
        foreach (var cause in causes)
        {
            ValidateCauseCompatibility(cause, analysis, jobs, Run(scope));
        }
        ValidateVerdict(analysis, causes, Run(scope));
        RequireCauseCoverage(analysis, causes, jobs);
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

        RequireExactCauseCoverage(analysis, []);
        ValidateVerdict(analysis, [], Run("pull-request"));
        RequireCauseCoverage(analysis, [], [TrustedJob("1", "Tests")]);
    }

    [Theory]
    [InlineData("transient-infra", "infra-failure")]
    [InlineData("flaky-test", "flaky-test")]
    [InlineData("main-repository-breakage", "main-repository-breakage")]
    public void SingleClassificationVerdictAcceptsMatchingJobsAndCauses(string classification, string type)
    {
        ValidateVerdict(Analysis(classification, [Job("1", classification)]),
            [Cause("cause", type, "", ["1"])], Run("main"));
    }

    [Fact]
    public void FlakyVerdictAllowsInfrastructureFailuresAlongsideFlakyFailures()
    {
        ValidateVerdict(Analysis("flaky-test", [Job("1", "flaky-test"), Job("2", "transient-infra")]),
            [Cause("flaky", "flaky-test", "", ["1"]), Cause("infra", "infra-failure", "", ["2"])], Run("pull-request"));
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
            Analysis(classification, [Job("1", classification)]), [], Run("main")));

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
            Analysis(verdict, [Job("1", verdict)], [Test("Suite.Test(1)", "Tests", testClassification)]), [], Run("main")));

        Assert.Equal($"Analysis failed_tests are incompatible with verdict {verdict}", error.Message);
    }

    [Theory]
    [InlineData("omitted")]
    [InlineData("duplicate")]
    [InlineData("extra")]
    public void SummaryCauseIdsMustUniquelyMatchGeneratedCauses(string scenario)
    {
        ImmutableArray<string> ids = scenario switch
        {
            "omitted" => [],
            "duplicate" => ["infra", "infra"],
            _ => ["infra", "extra"],
        };
        var error = Assert.Throws<ValidationException>(() => RequireExactCauseCoverage(
            Analysis("transient-infra", [Job("1", "transient-infra")], causeIds: ids), [Cause("infra", "infra-failure", "", ["1"])]));

        Assert.Equal("Analysis cause IDs must uniquely match the generated cause files", error.Message);
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
        var analysis = Analysis("flaky-test", [Job("1", "flaky-test"), Job("2", "flaky-test")],
            [Test("Suite.Test(1)", "Tests")], ["first", "second"]);

        foreach (var cause in causes)
        {
            ValidateCauseCompatibility(cause, analysis, jobs, Run("pull-request"));
        }
        RequireExactCauseCoverage(analysis, causes);
        ValidateVerdict(analysis, causes, Run("pull-request"));
        RequireCauseCoverage(analysis, causes, jobs);
    }

    [Theory]
    [InlineData("transient-infra", "infra-failure")]
    [InlineData("flaky-test", "flaky-test")]
    [InlineData("main-repository-breakage", "main-repository-breakage")]
    public void EveryPersistedFailedJobRequiresItsOwnMatchingCause(string classification, string type)
    {
        var analysis = Analysis("mixed", [Job("1", classification), Job("2", classification)]);
        var error = Assert.Throws<ValidationException>(() => RequireCauseCoverage(
            analysis, [Cause("cause", type, "", ["1"])], [TrustedJob("1", "Linux"), TrustedJob("2", "Windows")]));

        Assert.Equal("Every transient, flaky, and main-breakage failed job must be covered by a matching cause", error.Message);
    }

    [Fact]
    public void FlakyCauseRequiresExactParameterizedTestIdentity()
    {
        var error = Assert.Throws<ValidationException>(() => RequireCauseCoverage(
            Analysis("flaky-test", [Job("1", "flaky-test")], [Test("Suite.Test(1)", "Tests")]),
            [Cause("flaky", "flaky-test", "Suite.Test(2)", ["1"])], [TrustedJob("1", "Tests")]));

        Assert.Equal("Flaky-test cause must reference a validated flaky test", error.Message);
    }

    [Fact]
    public void FlakyCauseCannotRebindTestToAnotherFailedJob()
    {
        var error = Assert.Throws<ValidationException>(() => RequireCauseCoverage(
            Analysis("mixed", [Job("1", "code-issue"), Job("2", "code-issue")], [Test("Suite.Test(1)", "Windows")]),
            [Cause("flaky", "flaky-test", "Suite.Test(1)", ["1"])], [TrustedJob("1", "Linux"), TrustedJob("2", "Windows")]));

        Assert.Equal("Cause flaky.json references an unknown or incompatible failed job", error.Message);
    }

    [Fact]
    public void EveryFlakyTestJobPairRequiresAtLeastOneMatchingCause()
    {
        var error = Assert.Throws<ValidationException>(() => RequireCauseCoverage(
            Analysis("mixed", [Job("1", "code-issue"), Job("2", "code-issue")],
                [Test("Suite.Test(1)", "Linux"), Test("Suite.Test(1)", "Windows")]),
            [Cause("flaky", "flaky-test", "Suite.Test(1)", ["1"])], [TrustedJob("1", "Linux"), TrustedJob("2", "Windows")]));

        Assert.Equal("Every flaky test and job must be covered by a matching cause", error.Message);
    }

    [Fact]
    public void ZeroCauseCoverageRetainsTheExistingNoCauseTestJoin()
    {
        // Verdict validation rejects a mixed/flaky proposal without causes separately.
        RequireCauseCoverage(Analysis("mixed", [Job("1", "code-issue")], [Test("Suite.Test(1)", "Tests")]), [], [TrustedJob("1", "Tests")]);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(10, 10)]
    [InlineData(9, 10)]
    public void CauseBudgetAcceptsCountsThroughTen(int summaryCount, int fileCount)
    {
        RequireCauseBudget(summaryCount, fileCount);
    }

    [Theory]
    [InlineData(11, 10)]
    [InlineData(10, 11)]
    [InlineData(11, 11)]
    public void CauseBudgetRejectsEitherCountAboveTen(int summaryCount, int fileCount)
    {
        var error = Assert.Throws<ValidationException>(() => RequireCauseBudget(summaryCount, fileCount));

        Assert.Equal("Analysis exceeds the 10-cause publication budget", error.Message);
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

    private static ValidatedRun Run(string scope) => new(scope, "https://github.com/microsoft/aspire/actions/runs/123", scope == "main" ? "" : "42");
    private static ClassifiedJob Job(string id, string classification) => new(NumericId.Parse(id), id, classification);
    private static FailedJob TrustedJob(string id, string name) => new(NumericId.Parse(id), id, name);
    private static FailedTest Test(string name, string job, string classification = "flaky") => new(name, job, classification);
    private static FailureCause Cause(string id, string type, string testName, ImmutableArray<string> jobIds)
        => new(id, id + ".json", type, testName, jobIds.Select(NumericId.Parse).ToImmutableArray());
    private static FailureAnalysis Analysis(string verdict, ImmutableArray<ClassifiedJob> jobs,
        ImmutableArray<FailedTest> tests = default, ImmutableArray<string> causeIds = default)
        => new(verdict, jobs, tests.IsDefault ? [] : tests, causeIds.IsDefault ? [] : causeIds);
}
