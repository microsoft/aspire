// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using static AnalyzeCiFailure.CiFailureJson;
using static AnalyzeCiFailure.ValidationRules;

namespace AnalyzeCiFailure;

/// <summary>Validates complete proposal coverage against trusted evidence and derives the authorized diagnosis.</summary>
internal sealed partial class AnalysisValidator
{
    /// <summary>Binds decision-only references to immutable collected facts without reading or changing files.</summary>
    public static ValidatedAnalysis Validate(EvidenceBundle evidence, AnalysisProposal proposal, string expectedRepository)
    {
        // Establish the run boundary before interpreting any agent decisions.
        ValidateBundle(evidence, expectedRepository);
        if (proposal.SchemaVersion != 1 || proposal.Run.Repository != expectedRepository ||
            proposal.Run.Id != evidence.Run.Id || proposal.Run.Attempt != evidence.Run.Attempt)
        {
            throw new ValidationException("The proposal version, repository, run ID, or attempt does not match the collected evidence.");
        }
        if (evidence.TestEvidence.State == EvidenceState.Unavailable)
        {
            throw new ValidationException("Test evidence is unavailable; diagnosis may continue, but publication and reruns are not authorized.");
        }

        // Every collected job and test must have exactly one decision, with no invented references.
        if (proposal.Jobs.IsDefault || proposal.Tests.IsDefault || proposal.Causes.IsDefault)
        {
            throw new ValidationException("The proposal must contain jobs, tests, and causes arrays.");
        }
        if (proposal.Jobs.Any(job => job is null) || proposal.Tests.Any(test => test is null) || proposal.Causes.Any(cause => cause is null))
        {
            throw new ValidationException("Proposal arrays must contain objects, not null.");
        }
        if (proposal.Jobs.Select(job => job.JobId).Distinct().Count() != evidence.Jobs.Length ||
            proposal.Jobs.Length != evidence.Jobs.Length ||
            proposal.Jobs.Any(job => !evidence.Jobs.Any(collected => collected.Id == job.JobId)))
        {
            throw new ValidationException("Analysis failed-job IDs do not match the trusted failed jobs; classify every collected job exactly once without unknown or duplicate IDs.");
        }
        if (proposal.Tests.Length != evidence.TestEvidence.Failures.Length ||
            proposal.Tests.Select(test => test.Ref).Distinct(StringComparer.Ordinal).Count() != proposal.Tests.Length ||
            proposal.Tests.Any(test => !evidence.TestEvidence.Failures.Any(collected => collected.Ref == test.Ref)))
        {
            throw new ValidationException(TestMismatch +
                $" (expected={evidence.TestEvidence.Failures.Length}, proposed={proposal.Tests.Length}); " +
                "classify every collected test reference exactly once without unknown or duplicate references.");
        }
        foreach (var job in evidence.Jobs.Where(job => job.RunnerFailure is not null &&
            !evidence.TestEvidence.Failures.Any(test => test.JobId == job.Id)))
        {
            if (proposal.Jobs.Single(decision => decision.JobId == job.Id).Classification != JobClassification.TransientInfrastructure)
            {
                throw new ValidationException("A confirmed runner-loss job without observed test failures must be classified as infrastructure.");
            }
        }

        // Bind decisions to collected diagnostics; the proposal supplies classifications, not facts.
        var jobs = evidence.Jobs.Select(job => new FailedJob(Id(job.Id), Decimal(job.Id), DiagnosticTextSanitizer.Sanitize(job.Name, limit: 500))).ToImmutableArray();
        var collectedTests = PublicationEvidenceProjection.ProjectTestFailures(evidence);
        var boundTests = proposal.Tests.Select(decision =>
        {
            var index = FindTestIndex(evidence, decision.Ref);
            var test = collectedTests[index];
            if (!Enum.IsDefined(decision.Classification))
            {
                throw new ValidationException("Test classification must be 'flaky', 'code-issue' or 'transient-infra'.");
            }

            return new AnalysisTest
            {
                Name = test.Name,
                DisplayName = test.DisplayName,
                Job = test.Job,
                JobId = test.JobId,
                ObservationId = test.ObservationId,
                Classification = decision.Classification,
                Reason = SanitizeReason(decision.Reason),
                Error = test.Error,
                StackTrace = test.StackTrace,
                StandardOutput = test.StandardOutput,
                StandardError = test.StandardError,
                ResultFile = test.ResultFile,
                ResultFormat = test.ResultFormat,
                ExecutionId = test.ExecutionId,
            };
        }).ToImmutableArray();

        // Resolve cause references against the collected catalogue without reading fresh history here.
        var causes = proposal.Causes.Select(cause => BindCause(cause, evidence, collectedTests)).ToImmutableArray();
        if (causes.Select(cause => cause.Id).Distinct(StringComparer.Ordinal).Count() != causes.Length)
        {
            throw new ValidationException("Cause IDs must be unique within the proposal.");
        }

        var report = new AnalysisReport
        {
            Jobs = proposal.Jobs.Select(job => new AnalysisJob
            {
                Id = JsonNumber(job.JobId),
                Classification = job.Classification,
                Reason = SanitizeReason(job.Reason),
            }).ToImmutableArray(),
            Tests = boundTests,
            Causes = causes.Select(cause => cause.Id).ToImmutableArray(),
        };
        var run = new ValidatedRun(evidence.Run.Scope, evidence.Run.Url, evidence.SubjectPr is { } pr ? Decimal(pr.Number) : "");

        // Apply coverage and compatibility rules before deriving the overall verdict.
        RequireExactJobCoverage(report, jobs, run);
        RequireExactTestCoverage(
            report,
            collectedTests.Select(test => new TestIdentity(test.Name, test.Job, AsNormalizedNumber(test.JobId), test.ObservationId)).ToImmutableArray(),
            jobs);
        foreach (var cause in causes)
        {
            RequireCauseFields(cause);
            ValidateCauseCompatibility(cause, report, jobs, run);
        }

        RequireCauseClassificationCoverage(report, causes);
        RequireCauseCoverage(report, causes);

        // A verdict is accepted only when the existing rule matrix admits exactly one choice.
        var acceptedVerdicts = ImmutableArray.CreateBuilder<AnalysisReport>();
        foreach (var verdict in Enum.GetValues<AnalysisVerdict>())
        {
            var candidate = report with { Verdict = verdict };
            try
            {
                RequirePermittedVerdict(candidate, run);
                ValidateVerdict(candidate, causes, run);
                acceptedVerdicts.Add(candidate);
            }
            catch (ValidationException)
            {
                // Derive only a uniquely accepted verdict from the complete existing rule sequence.
            }
        }

        if (acceptedVerdicts.Count != 1)
        {
            throw new ValidationException(acceptedVerdicts.Count == 0
                ? "The proposed job/test classifications and cause references do not form a supported, fully covered failure decision."
                : "The proposed decisions admit multiple verdicts; publication is not authorized.");
        }

        // Capture the existing rendering shapes so later file changes cannot alter accepted facts.
        var acceptedReport = acceptedVerdicts[0];
        var capturedJobs = evidence.Jobs.Select(job => new WorkflowJob
        {
            Id = JsonNumber(job.Id),
            Name = job.Name,
            HtmlUrl = job.Url,
            Conclusion = string.IsNullOrEmpty(job.Conclusion) ? null : JsonSerializer.SerializeToElement(job.Conclusion),
            Steps = JsonSerializer.SerializeToElement(job.Steps.Select(step => new { number = step.Number, name = step.Name, conclusion = step.Conclusion })),
        }).ToImmutableArray();

        var runEvidence = new RunEvidence(
            new RunContext
            {
                RunId = JsonNumber(evidence.Run.Id),
                RunAttempt = JsonSerializer.SerializeToElement(evidence.Run.Attempt),
                RunScope = evidence.Run.Scope,
                HeadSha = JsonSerializer.SerializeToElement(evidence.Run.HeadSha),
                Event = evidence.Run.Event,
                HeadBranch = evidence.Run.HeadBranch,
                PrNumbers = run.PrNumbers,
            },
            new WorkflowRun { HtmlUrl = evidence.Run.Url, HeadSha = JsonSerializer.SerializeToElement(evidence.Run.HeadSha) },
            capturedJobs);
        ValidateCommentBudget(acceptedReport, run, runEvidence);

        return new ValidatedAnalysis(acceptedReport, run, jobs, causes, collectedTests, runEvidence)
        {
            Bundle = evidence,
            CapturedPublication = PublicationEvidence.FromBundle(evidence),
        };
    }

    private static FailureCause BindCause(ProposedCause proposal, EvidenceBundle evidence, ImmutableArray<TestFailureEvidence> tests)
    {
        if (proposal.JobIds.IsDefaultOrEmpty || proposal.TestRefs.IsDefault ||
            proposal.JobIds.Distinct().Count() != proposal.JobIds.Length ||
            proposal.TestRefs.Distinct(StringComparer.Ordinal).Count() != proposal.TestRefs.Length ||
            proposal.JobIds.Any(id => !evidence.Jobs.Any(job => job.Id == id)))
        {
            throw new ValidationException("Each cause must reference unique collected job IDs and test references, with at least one job.");
        }
        _ = SanitizeReason(proposal.Reason);
        string id;
        CauseType type;
        CauseDefinition? definition = null;
        if (proposal is ExistingCauseProposal existing)
        {
            id = existing.Id;
            var identity = evidence.PriorCauseIdentities.SingleOrDefault(cause => cause.Id == id)
                ?? throw new ValidationException("An existing cause ID must occur in the collected prior-cause catalogue.");
            type = identity.Type;
        }
        else if (proposal is NewCauseProposal created && created.Definition is { } proposedDefinition)
        {
            id = proposedDefinition.Id;
            type = proposedDefinition.Type;
            if (evidence.PriorCauseIdentities.Any(cause => cause.Id == id))
            {
                throw new ValidationException("A new cause ID must not already occur in the collected catalogue.");
            }
            var title = DiagnosticTextSanitizer.Sanitize(proposedDefinition.Title);
            var pattern = DiagnosticTextSanitizer.Sanitize(proposedDefinition.ErrorPattern, multiline: true);
            if (!Safe(JsonSerializer.SerializeToNode(title), max: 238) || string.IsNullOrWhiteSpace(title) ||
                !Safe(JsonSerializer.SerializeToNode(pattern), max: 500, multiline: true) || string.IsNullOrWhiteSpace(pattern))
            {
                throw new ValidationException("A new cause needs a visible title of at most 238 characters and an error pattern of at most 500 characters.");
            }
            definition = new CauseDefinition { Id = id, Type = type, Title = title, ErrorPattern = pattern };
        }
        else
        {
            throw new ValidationException("Cause kind must be 'existing' or 'new', with only its documented fields.");
        }
        if (id is null || !CauseIdSlugRegex().IsMatch(id) || !Enum.IsDefined(type))
        {
            throw new ValidationException("A cause needs a lowercase hyphenated ID and a recognized cause type.");
        }
        if (type != CauseType.FlakyTest && proposal.TestRefs.Length > 0)
        {
            throw new ValidationException("Infrastructure and main-breakage causes must not reference tests.");
        }
        var references = proposal.TestRefs.Select(reference =>
        {
            var index = FindTestIndex(evidence, reference);
            if (index < 0)
            {
                throw new ValidationException("A cause references a test that is not in the collected evidence.");
            }
            var test = tests[index];
            return new TestObservation(test.Name, AsNormalizedNumber(test.JobId)!.Value, test.ObservationId);
        }).ToImmutableArray();
        return new FailureCause(
            id,
            id + ".json",
            type,
            proposal.JobIds.Select(Id).ToImmutableArray(),
            type == CauseType.FlakyTest ? references : default)
        {
            Definition = definition,
            IsExistingProposal = proposal is ExistingCauseProposal,
        };
    }

    internal static void ValidateBundle(EvidenceBundle evidence, string expectedRepository)
    {
        if (!GitHubRepositoryNameRegex().IsMatch(expectedRepository) ||
            evidence.SchemaVersion != 1 || evidence.Run is null || evidence.Run.Repository != expectedRepository ||
            evidence.Run.Id <= 0 || evidence.Run.Attempt <= 0 || !Enum.IsDefined(evidence.Run.Scope) ||
            evidence.Run.Url != $"https://github.com/{expectedRepository}/actions/runs/{Decimal(evidence.Run.Id)}")
        {
            throw new ValidationException("The evidence version, repository, run ID, attempt, scope, or GitHub run URL is invalid.");
        }
        var main = evidence.Run.Scope == RunScope.Main;
        if (main != (evidence.Run.Event == "push" && evidence.Run.HeadBranch == "main") ||
            !main && evidence.Run.Event is not ("pull_request" or "pull_request_target") ||
            main && evidence.SubjectPr is not null || main != (evidence.MainContext is not null) ||
            evidence.SubjectPr is { Number: <= 0 })
        {
            throw new ValidationException("The evidence scope, event, branch, PR, and main context do not agree.");
        }
        if (evidence.MainContext is { } context &&
            (context.CandidateHistory is null || !Enum.IsDefined(context.CandidateHistory.State) ||
             (context.CandidateHistory.State == CandidateHistoryState.Available) != (context.CandidateHistory.Candidates is not null) ||
             context.CandidateHistory.Candidates is { } candidates && (candidates.IsDefault || candidates.Any(candidate =>
                 candidate is null || candidate.PullRequest is null || candidate.PullRequest.Number <= 0)) ||
             context.LastSuccessfulRun is { Id: <= 0 } || context.TriggeringMerge is { Number: <= 0 }))
        {
            throw new ValidationException("The captured main context has an invalid history state, candidate array, or identifier.");
        }
        if (evidence.Jobs.IsDefaultOrEmpty || evidence.Jobs.Any(job => job is null || job.Id <= 0 ||
                job.Name is null || job.Conclusion is null || job.Steps.IsDefault ||
                job.RunnerFailure is not null && job.RunnerFailure != WorkflowRunEvidenceCollector.RunnerLostAnnotation ||
                job.Steps.Any(step => step is null || step.Number <= 0 || step.Name is null)) ||
            evidence.Jobs.Select(job => job.Id).Distinct().Count() != evidence.Jobs.Length)
        {
            throw new ValidationException("Evidence jobs must be a nonempty array with unique positive IDs, names, and complete step arrays.");
        }
        if (evidence.TestEvidence is not { } tests || !Enum.IsDefined(tests.State) ||
            tests.Failures.IsDefault || tests.Problems.IsDefault || tests.Problems.Any(problem => problem is null || problem.Stage is null || problem.Message is null) ||
            tests.State == EvidenceState.NotApplicable && tests.Failures.Length != 0 ||
            tests.Failures.Select(test => test?.Ref).Distinct(StringComparer.Ordinal).Count() != tests.Failures.Length)
        {
            throw new ValidationException("Test evidence must have a recognized state, unique references, and empty failures when not applicable.");
        }
        foreach (var test in tests.Failures)
        {
            if (test is null || string.IsNullOrEmpty(test.Ref) || string.IsNullOrEmpty(test.Name) ||
                test.DisplayName is null || !evidence.Jobs.Any(job => job.Id == test.JobId) ||
                test.Diagnostics is not { Error: not null, StackTrace: not null, StandardOutput: not null, StandardError: not null } ||
                test.Sources.IsDefaultOrEmpty || test.Sources.Any(source => source is null || source.ArtifactId <= 0 ||
                    string.IsNullOrEmpty(source.ResultFile) || !Enum.IsDefined(source.Format)))
            {
                throw new ValidationException("Each test failure needs an opaque reference, a collected job, all diagnostic strings, and at least one valid result source.");
            }
        }
        if (evidence.PriorCauseIdentities.IsDefault || evidence.PriorCauseIdentities.Any(cause => cause is null ||
                cause.Id is null || !CauseIdSlugRegex().IsMatch(cause.Id) || !Enum.IsDefined(cause.Type)) ||
            evidence.PriorCauseIdentities.Select(cause => cause.Id).Distinct(StringComparer.Ordinal).Count() != evidence.PriorCauseIdentities.Length)
        {
            throw new ValidationException("The prior-cause catalogue must contain unique readable cause identities and types.");
        }
    }

    private static string SanitizeReason(string value) => value is not null
        ? DiagnosticTextSanitizer.Sanitize(value, limit: 500)
        : throw new ValidationException("A decision reason must be a string.");
    private static string Decimal(long value) => value.ToString(CultureInfo.InvariantCulture);
    private static NormalizedJsonNumber Id(long value) => NormalizedJsonNumber.Parse(Decimal(value));
    private static JsonElement JsonNumber(long value) => JsonSerializer.SerializeToElement(value);

    private static int FindTestIndex(EvidenceBundle evidence, string reference)
    {
        for (var index = 0; index < evidence.TestEvidence.Failures.Length; index++)
        {
            if (evidence.TestEvidence.Failures[index].Ref == reference)
            {
                return index;
            }
        }
        return -1;
    }

    private static void ValidateCommentBudget(AnalysisReport report, ValidatedRun run, RunEvidence evidence)
    {
        if (run.Scope == RunScope.PullRequest && PositivePullRequestNumberRegex().IsMatch(run.PrNumbers))
        {
            ValidationRules.RequireCommentBudget(Encoding.UTF8.GetByteCount(PrCommentRenderer.Render(
                report,
                evidence.Jobs.Select(job => job with { Name = DiagnosticTextSanitizer.Sanitize(job.Name!, limit: 500) }),
                run.Url)));
        }
    }

    [GeneratedRegex(@"^[a-z0-9]+(-[a-z0-9]+)*\z")]
    private static partial Regex CauseIdSlugRegex();

    [GeneratedRegex(@"^[1-9][0-9]*\z")]
    private static partial Regex PositivePullRequestNumberRegex();

    [GeneratedRegex(@"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+\z")]
    private static partial Regex GitHubRepositoryNameRegex();
}
