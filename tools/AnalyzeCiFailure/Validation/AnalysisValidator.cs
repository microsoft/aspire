// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using AnalyzeCiFailure.Contracts;
using static AnalyzeCiFailure.Common.WorkflowCommands;

namespace AnalyzeCiFailure.Validation;

/// <summary>
/// The trust boundary between the analysis agent and publication.
/// </summary>
/// <remarks>
/// <para>
/// The agent writes <c>analysis-result.json</c> and one file per cause, but its output is untrusted:
/// it reads attacker-controllable logs and test output. Before anything is commented on a PR or
/// persisted as a cause, the agent's files are sanitized in place and checked against data the
/// workflow collected itself under <c>ci-failure-data</c>.
/// </para>
/// <para>
/// This class is only the order of the steps. <see cref="ValidationInputs"/> reads the files and
/// <see cref="ValidationRules"/> holds the rules, with their limits in <see cref="ValidationPolicy"/>.
/// The first broken rule is reported as a single <c>::error::</c> line and a non-zero exit code.
/// </para>
/// </remarks>
internal sealed class AnalysisValidator(ValidationPaths paths, ValidationInputs inputs, PublicationScripts scripts)
{
    /// <summary>Validates the analysis and returns the process exit code.</summary>
    public static async Task<int> RunAsync(ValidationPaths paths, TextWriter output, CancellationToken cancellationToken = default)
    {
        var scratch = Directory.CreateTempSubdirectory("analyze-ci-failure-");
        try
        {
            var scripts = new PublicationScripts(paths, output);
            var validator = new AnalysisValidator(paths, new ValidationInputs(paths, scripts, scratch.FullName), scripts);
            await validator.ValidateAsync(cancellationToken);
            return 0;
        }
        catch (ValidationException ex)
        {
            await output.WriteLineAsync(Error(ex.Message).AsMemory(), cancellationToken);
            return 1;
        }
        finally
        {
            scratch.Delete(recursive: true);
        }
    }

    private async Task ValidateAsync(CancellationToken cancellationToken)
    {
        // Step 1: every input must exist. Missing trusted data means collection failed, and
        // validating against nothing would accept anything.
        inputs.RequireAllPresent();

        // Step 2: sanitize the agent's analysis in place. All later checks, and every later publish
        // step, read the sanitized copy.
        await inputs.SanitizeAnalysisAsync(cancellationToken);

        // Step 3: establish which run this workflow is analyzing, from data it collected itself.
        var run = inputs.ReadTrustedRun();

        // Step 4: the analysis must describe that run. Identity is checked before the rest of the
        // document is bound, so analysis of the wrong run is reported as such.
        ValidationRules.AnalysisMustDescribeRun(inputs.ReadAnalysis<AnalysisRunIdentity>(run), run);

        // Step 5: the PR the comment will be posted on must be the run's own PR.
        var document = inputs.ReadAnalysis<AnalysisDocument>(run);
        ValidationRules.SubjectPullRequestMustBeTrusted(document, run);

        // Step 6: the agent's summary must have the expected shape and only publishable text.
        var analysis = ValidationRules.AnalysisMustBePublishable(document);

        // Step 7: read the workflow's own list of failed jobs.
        var trustedJobs = await inputs.ReadTrustedFailedJobsAsync(cancellationToken);

        // Step 8: reported test failures must match the test results the workflow collected. The
        // published diagnostics are then taken from those results, not from the agent.
        var testEvidence = await inputs.ReadTestEvidenceAsync(cancellationToken);
        ValidationRules.FailedTestsMustMatchEvidence(analysis, testEvidence, trustedJobs);
        if (testEvidence.Failures is { } trustedFailures && analysis.FailedTests.Count > 0)
        {
            inputs.ReplaceTestDiagnostics(trustedFailures);
        }

        // Step 9: the verdict must be one the run scope allows.
        ValidationRules.VerdictMustBeAllowedForScope(analysis.Verdict, run);

        // Step 10: cap the number of causes.
        var causeFiles = inputs.ListCauseFiles();
        ValidationRules.CausesMustFitBudget(analysis, causeFiles.Length);

        // Step 11: the analysis must classify exactly the jobs that really failed.
        ValidationRules.FailedJobsMustMatchTrustedJobs(analysis, trustedJobs, run);

        // Step 12: each cause file must be valid on its own and must not rewrite stored history.
        var causes = new List<Cause>();
        foreach (var causeFile in causeFiles)
        {
            var fileName = Path.GetFileName(causeFile);
            var cause = ValidationRules.CauseMustBeValid(fileName, await inputs.ReadCauseAsync(causeFile, cancellationToken), analysis, trustedJobs, run);
            if (inputs.ReadPriorCause(fileName) is { } prior)
            {
                ValidationRules.CauseMustMatchPriorCause(cause, prior);
            }
            causes.Add(cause);
        }

        // Step 13: the summary's cause list and the cause files must name the same causes.
        ValidationRules.CauseIdsMustMatchCauseFiles(analysis, causes);

        // Step 14: the verdict, job classifications, and cause types must tell the same story.
        var counts = ClassificationCounts.From(analysis, causes);
        ValidationRules.VerdictMustMatchClassifications(analysis.Verdict, counts, run);
        ValidationRules.ClassificationsMustMatchCauseTypes(counts);

        // Step 15: every failure that will be persisted must be covered by a matching cause.
        ValidationRules.CausesMustCoverPersistedFailures(analysis, causes, trustedJobs);

        // Step 16: render the PR comment now, so an oversized or unrenderable comment fails
        // validation before anything is persisted rather than part-way through publishing.
        if (run.Scope == RunScopes.PullRequest && run.PrNumber > 0)
        {
            ValidationRules.CommentMustFitBudget(
                await scripts.TryMeasureCommentAsync(paths.AnalysisFile, inputs.TrustedFailedJobsFile, run.Url, cancellationToken));
        }
    }
}
