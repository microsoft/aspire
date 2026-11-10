// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using static AnalyzeCiFailure.CiFailureJson;

namespace AnalyzeCiFailure;

/// <summary>Renders validated failure decisions as a pull-request analysis comment.</summary>
internal static class PrCommentRenderer
{
    internal static string Render(AnalysisReport analysis, IEnumerable<WorkflowJob> collectedJobs, string runUrl)
    {
        if (ValidationRules.UsesAggregatePublication(analysis.Causes.Length))
        {
            return "<!-- analyze-ci-failure -->\n[automated] CI analysis identified " +
                analysis.Causes.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                $" independent causes. They are tracked together in one aggregate issue.\n\n[Source CI run]({runUrl})\n";
        }
        var jobs = collectedJobs.Select(job => job with { Name = DiagnosticTextSanitizer.Sanitize(job.Name!, limit: 500) }).ToArray();
        var classified = analysis.Jobs.ToDictionary(job => job.Id?.GetRawText() ?? "null");
        var jobList = string.Join(
            '\n',
            jobs.Select(job =>
        {
            var proposal = classified[job.Id?.GetRawText() ?? "null"];
            var classification = proposal.Classification is { } value ? EnumText(value) : "";
            return $"- {MarkdownFormatting.CodeSpan(job.Name!)} — {MarkdownFormatting.CodeSpan(DiagnosticTextSanitizer.Sanitize(proposal.Reason ?? "", limit: 500))} ({classification})";
        }));
        var tests = analysis.Tests.Where(test => test.Classification == TestClassification.Flaky).Select(test =>
        {
            var job = jobs.FirstOrDefault(j => j.Name == DiagnosticTextSanitizer.Sanitize(test.Job ?? "", limit: 500));
            var text = $"- {MarkdownFormatting.CodeSpan(DiagnosticTextSanitizer.Sanitize(test.Name ?? "", limit: 500))}" +
                (job is null ? "" : " in job " + MarkdownFormatting.CodeSpan(job.Name!)) +
                "\n  - **Error**:\n\n" + MarkdownFormatting.Indent(DiagnosticTextSanitizer.Sanitize(test.Error ?? "", limit: 1000, multiline: true), spaces: 8) + "\n";
            var stack = DiagnosticTextSanitizer.Sanitize(test.StackTrace ?? "", limit: 2000, multiline: true);
            if (stack.Length > 0)
            {
                text += "  - **Stack Trace** (first frames):\n\n" + MarkdownFormatting.Indent(string.Join('\n', stack.Split('\n').Take(5)), spaces: 8) + "\n";
            }
            foreach (var (value, heading) in new[] { (test.StandardOutput, "Standard Output"), (test.StandardError, "Standard Error") })
            {
                var diagnostic = DiagnosticTextSanitizer.Sanitize(value ?? "", limit: 4000, multiline: true);
                if (diagnostic.Length > 0)
                {
                    text += $"  - **{heading}** (sensitive values redacted):\n\n" + MarkdownFormatting.Indent(diagnostic, spaces: 8) + "\n";
                }
            }

            return text + "  - **Why likely flaky**: " + MarkdownFormatting.CodeSpan(DiagnosticTextSanitizer.Sanitize(test.Reason ?? "", limit: 500));
        });
        var testList = string.Join('\n', tests);
        var testSection = testList.Length == 0 ? "" : "\n\n**Suspected flaky test(s):**\n" + testList;
        var body = analysis.Verdict switch
        {
            AnalysisVerdict.TransientInfrastructure => MarkdownTemplate.Render(
                "CommentTransientInfrastructure",
                ("FAILED_JOBS", jobList),
                ("RUN_URL", runUrl)),
            AnalysisVerdict.FlakyTest => MarkdownTemplate.Render(
                "CommentFlakyTests",
                ("FAILED_JOBS", jobList),
                ("FLAKY_TEST_SECTION", testSection),
                ("RUN_URL", runUrl)),
            AnalysisVerdict.CodeIssue => MarkdownTemplate.Render("CommentCodeIssue", ("FAILED_JOBS", jobList)),
            _ => MarkdownTemplate.Render(
                "CommentMixedFailures",
                ("FAILED_JOBS", jobList),
                ("FLAKY_TEST_SECTION", testSection)),
        };

        // Preserve the final blank line in the published comment contract.
        return "<!-- analyze-ci-failure -->\n" + body + "\n";
    }
}
