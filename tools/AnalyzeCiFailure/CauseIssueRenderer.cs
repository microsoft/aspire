// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using static AnalyzeCiFailure.CiFailureJson;

namespace AnalyzeCiFailure;

/// <summary>Renders recurring-cause issues while preserving operator text and managed occurrence sections.</summary>
internal static partial class CauseIssueRenderer
{
    private const string TestsStart = "<!-- ci-failure-tests:start -->";
    private const string TestsEnd = "<!-- ci-failure-tests:end -->";
    private const string OccurrencesStart = "<!-- ci-failure-occurrences:start -->";
    private const string OccurrencesEnd = "<!-- ci-failure-occurrences:end -->";
    private const string OccurrencesHeader = "| Date | Build | Job | Context |\n|------|-------|-----|----|";

    /// <summary>Carries a rendered cause issue body, title and labels, including an unavailable body when rendering is unsafe.</summary>
    internal sealed record IssueDraft(string? Body, string Title, string Labels);

    internal static string JobNames(IEnumerable<string> names, string format)
    {
        var safe = names.Select(name => DiagnosticTextSanitizer.Sanitize(name, limit: 500)).ToArray();
        return format == "plain" ? string.Join(", ", safe) :
            string.Join("<br>", safe.Select(name => MarkdownFormatting.CodeSpan(format == "table" ? name.Replace("|", "\\|", StringComparison.Ordinal) : name)));
    }

    internal static IssueDraft RenderCanonicalIssue(
        JsonObject cause,
        RunContext? context,
        PublicationEvidence evidence,
        string runUrl,
        string scope,
        string pr,
        string jobs,
        string row)
    {
        cause = (JsonObject)DiagnosticTextSanitizer.SanitizeCause(cause)!;
        var id = Text(cause["id"])!;
        var type = Text(cause["type"])!;
        var main = type == "main-repository-breakage";
        var title = DiagnosticTextSanitizer.Sanitize(Text(cause["title"]) ?? "", limit: 238);
        var test = DiagnosticTextSanitizer.Sanitize(Text(cause["test_name"]) ?? "", limit: 500);
        if (string.IsNullOrWhiteSpace(title))
        {
            title = id;
        }
        if (main)
        {
            title = "Main branch CI failure at " + (AsString(context?.HeadSha) ?? "unknown");
        }
        var total = cause.ContainsKey("occurrences") ? (cause["occurrences"] as JsonArray)?.Count ?? 0 : 1;
        if (!(total > 0))
        {
            throw new ValidationException("invalid stored occurrence history");
        }
        var buildContext = new StringBuilder();
        if (main)
        {
            buildContext.Append("Affected branch: `main`\n")
                .Append("Last successful main SHA: `").Append(AsString(evidence.LastSuccess?.HeadSha) ?? "unknown").Append("`\n")
                .Append("Failed main SHA: `").Append(AsString(context?.HeadSha) ?? "unknown").Append("`\n");
            if (evidence.CandidateState == CandidateHistoryState.Available && AsNormalizedNumber(evidence.TriggeringMerge?.Number) is not null)
            {
                buildContext.Append("Triggering merge PR (context only, not necessarily causal): #")
                    .Append(evidence.TriggeringMerge!.Number!.Value.GetRawText()).Append(' ')
                    .Append(MarkdownFormatting.CodeSpan(DiagnosticTextSanitizer.Sanitize(evidence.TriggeringMerge.Title ?? "", limit: 238))).Append('\n');
            }
        }
        else
        {
            buildContext.Append(test.Length == 0 ? $"Build error leg: {jobs}\n" : $"Build error leg or test failing: {jobs} / {MarkdownFormatting.CodeSpan(test)}\n");
        }
        if (scope == "pull-request" && pr != "0")
        {
            buildContext.Append("Pull request: #").Append(pr).Append('\n');
        }
        var error = Text(cause["error_pattern"]) ?? "";
        if (string.IsNullOrWhiteSpace(error))
        {
            error = "No diagnostic pattern recorded.";
        }
        error = main
            ? "The main branch CI run failed. See the linked workflow run and trusted commit context above for diagnostics."
            : DiagnosticTextSanitizer.Sanitize(error, limit: 500, multiline: true);
        var body = MarkdownTemplate.Render(
            "CauseIssue",
            ("CAUSE_ID", id),
            ("CAUSE_TYPE", type),
            ("RUN_URL", runUrl),
            ("BUILD_CONTEXT", buildContext.ToString()),
            ("ERROR", MarkdownFormatting.Indent(error)),
            ("DESCRIPTION", MarkdownFormatting.CodeSpan(title)),
            ("OCCURRENCES", OccurrenceSection([row], total).TrimEnd('\n')));
        var issueTitle = (main ? "[Main CI Failure] " : "[CI Failure] ") + title;
        if (!(issueTitle.EnumerateRunes().Count() <= 256))
        {
            throw new ValidationException("IssueDraft title exceeds GitHub's 256-character limit");
        }
        var labels = "ci-failure-cause" + (main ? ",main-ci-break" : type == "flaky-test" ? ",test-failure" : "");

        return new IssueDraft(body, issueTitle, labels);
    }

    internal static string AppendOccurrence(string body, string row, int total)
    {
        if (!IsOccurrence(row))
        {
            throw new ValidationException("invalid occurrence row");
        }
        var (prefix, section, suffix, legacy) = OccurrenceParts(body);
        var rows = OccurrenceRows(section, legacy).Append(row).ToArray();
        if (!(total >= rows.Length))
        {
            throw new ValidationException("occurrence total is smaller than the rendered history");
        }

        return prefix.TrimEnd('\r', '\n') + (prefix.EndsWith("\r\n", StringComparison.Ordinal) ? "\r\n\r\n" : "\n\n")
            + OccurrenceSection(rows, total) + suffix;
    }

    internal static bool HasOccurrence(string body, string runId)
    {
        var parts = OccurrenceParts(body);

        return OccurrenceRows(parts.Section, parts.Legacy).Any(row => OccurrenceRun(row) == runId);
    }

    internal static string MigrateMainBody(string current, string canonical)
    {
        var oldParts = OccurrenceParts(current);
        var newParts = OccurrenceParts(canonical);
        if (!(oldParts.Prefix.Split('\n')[0].TrimEnd('\r') == newParts.Prefix.Split('\n')[0].TrimEnd('\r')))
        {
            throw new ValidationException("issue identity marker does not match");
        }
        static (string Generated, string Notes) Prefix(string prefix)
        {
            var types = Markers(prefix, "**Type**: main-repository-breakage");
            if (!(types.Count == 1))
            {
                throw new ValidationException("ambiguous main issue type");
            }
            var end = types[0].Index + types[0].Length;

            return (prefix[..end], prefix[end..].Trim('\r', '\n'));
        }
        var oldPrefix = Prefix(oldParts.Prefix);
        var newPrefix = Prefix(newParts.Prefix);
        var occurrences = oldParts.Legacy ? oldParts.Section : OccurrencesStart + oldParts.Section + OccurrencesEnd + "\n";

        return newPrefix.Generated + (oldPrefix.Notes.Length > 0 ? "\n\n" + oldPrefix.Notes : "") + "\n\n" + occurrences + oldParts.Suffix;
    }

    internal static string? FitIssue(string body, JsonObject[] observations, int maxBytes = ValidationRules.MaximumCommentBytes)
    {
        // All issue-body byte fitting happens here. Only publisher-owned tests and
        // earlier occurrence rows can be omitted; operator notes and the latest row stay.
        var rows = observations.Select(t =>
        {
            var row = $"- {MarkdownFormatting.CodeSpan(DiagnosticTextSanitizer.Sanitize(Text(t["name"])!, limit: 500))} — job {GetValueText(t["job_id"])} ({MarkdownFormatting.CodeSpan(DiagnosticTextSanitizer.Sanitize(Text(t["job"])!, limit: 500))})";
            if (Text(t["observation_id"]) is { } observation)
            {
                row += $" — {MarkdownFormatting.CodeSpan(DiagnosticTextSanitizer.Sanitize(observation, limit: 100))}, result {MarkdownFormatting.CodeSpan(DiagnosticTextSanitizer.Sanitize(Text(t["result_file"]) ?? "", limit: 500))}";
                if (Text(t["execution_id"]) is { } execution)
                {
                    row += $", execution {MarkdownFormatting.CodeSpan(DiagnosticTextSanitizer.Sanitize(execution, limit: 500))}";
                }
                foreach (var field in new[] { "error", "stack_trace", "standard_output", "standard_error" })
                {
                    if (Text(t[field]) is { Length: > 0 } diagnostic)
                    {
                        // Indented code cannot interrupt the label's paragraph.
                        // https://spec.commonmark.org/0.31.2/#indented-code-blocks
                        row += $"\n\n    {field}:\n\n" + MarkdownFormatting.Indent(DiagnosticTextSanitizer.Sanitize(diagnostic, limit: 4000, multiline: true), spaces: 8);
                    }
                }
            }
            return row;
        }).ToArray();
        var projected = ProjectTests(body, rows, omitted: 0);
        if (projected is null || Encoding.UTF8.GetByteCount(projected) <= maxBytes)
        {
            return projected;
        }

        (string Prefix, string Section, string Suffix, bool Legacy) parts;
        string[] occurrences;
        try
        {
            parts = OccurrenceParts(body);
            // Replay can skip appending a row. Validate the entire section before
            // any fitting, so unrecognized operator content is never discarded.
            occurrences = OccurrenceRows(parts.Section, parts.Legacy);
        }
        catch (ValidationException)
        {
            return null;
        }

        // Redact/render each historical observation only once. Suffix byte sums
        // select the retained rows without repeatedly rebuilding large sections.
        var rowBytes = SuffixBytes(rows);
        var allOmitted = ProjectTests(body, rows, rows.Length)!;
        var baseBytes = Encoding.UTF8.GetByteCount(allOmitted) - Encoding.UTF8.GetByteCount(OmittedNotice(rows.Length));
        for (var omitted = 1; omitted <= rows.Length; omitted++)
        {
            var size = baseBytes + rowBytes[omitted] + Math.Max(0, rows.Length - omitted - 1) +
                Encoding.UTF8.GetByteCount(OmittedNotice(omitted));
            if (size <= maxBytes)
            {
                return ProjectTests(body, rows, omitted);
            }
        }

        var count = OccurrenceSummaryCountRegex().Match(parts.Section);
        if (occurrences.Length <= 1 || !count.Success ||
            !int.TryParse(count.Groups["total"].Value, out var total))
        {
            return null;
        }
        var prefix = parts.Prefix.TrimEnd('\r', '\n') + (parts.Prefix.EndsWith("\r\n", StringComparison.Ordinal) ? "\r\n\r\n" : "\n\n");
        var emptySection = prefix + OccurrenceSection([], total) + parts.Suffix;
        var emptyBytes = Encoding.UTF8.GetByteCount(ProjectTests(emptySection, rows, rows.Length)!);
        var occurrenceBytes = SuffixBytes(occurrences);
        for (var removed = 1; removed < occurrences.Length; removed++)
        {
            var remaining = occurrences.Length - removed;
            var size = emptyBytes + occurrenceBytes[removed] + remaining - 1 +
                remaining.ToString(System.Globalization.CultureInfo.InvariantCulture).Length - 1;
            if (size <= maxBytes)
            {
                return ProjectTests(prefix + OccurrenceSection(occurrences[removed..], total) + parts.Suffix, rows, rows.Length);
            }
        }

        return null;
    }

    private static long[] SuffixBytes(string[] rows)
    {
        var sizes = new long[rows.Length + 1];
        for (var i = rows.Length - 1; i >= 0; i--)
        {
            sizes[i] = sizes[i + 1] + Encoding.UTF8.GetByteCount(rows[i]);
        }

        return sizes;
    }

    private static string[] OccurrenceRows(string section, bool legacy)
    {
        var lines = section.Split('\n');
        if (!lines.All(line => line.Length == 0 || line is "## Occurrences"
            or "| Date | Build | Job | Context |" or "|------|-------|-----|----|" ||
            legacy && line == "| Date | Build | Job | PR |" ||
            OccurrenceSummaryRegex().IsMatch(line) || IsOccurrence(line)))
        {
            throw new ValidationException("unsupported occurrence section contents");
        }

        return lines.Where(IsOccurrence).ToArray();
    }

    private static bool IsOccurrence(string row) => OccurrenceRun(row).Length > 0;

    private static string OccurrenceRun(string row)
        // Managed rows have the shape:
        // | 2026-10-21 | [123](https://github.com/owner/repo/actions/runs/123) | ` Job ` | #42 |
        // Only the build column identifies a run; links in job text or operator notes do not.
        => OccurrenceRowRegex().Match(row)
            .Groups["run"].Value;

    private static string OccurrenceSection(string[] rows, int total)
        => $"{OccurrencesStart}\n## Occurrences\n\nShowing {rows.Length} most recent of {total} occurrences.\n\n{OccurrencesHeader}\n{string.Join('\n', rows)}\n{OccurrencesEnd}\n";

    private static (string Prefix, string Section, string Suffix, bool Legacy) OccurrenceParts(string body)
    {
        var start = Markers(body, OccurrencesStart);
        var end = Markers(body, OccurrencesEnd);
        if (start.Count > 0 || end.Count > 0)
        {
            if (!(start.Count == 1 && end.Count == 1 &&
                end[0].Index >= start[0].Index + start[0].Length))
            {
                throw new ValidationException("ambiguous managed occurrence section");
            }
            var after = end[0].Index + end[0].Length;
            // Own the closing delimiter's line ending, but retain all later operator
            // text verbatim, including CRLF and blank lines, outside the managed region.
            if (body.AsSpan(after).StartsWith("\r\n"))
            {
                after += 2;
            }
            else if (body.AsSpan(after).StartsWith("\n"))
            {
                after++;
            }

            return (body[..start[0].Index], body[(start[0].Index + start[0].Length)..end[0].Index]
                .Replace("\r\n", "\n", StringComparison.Ordinal), body[after..], false);
        }
        body = body.Replace("\r\n", "\n", StringComparison.Ordinal);
        var legacy = body.Split("\n## Occurrences\n", StringSplitOptions.None);
        if (!(legacy.Length == 2))
        {
            throw new ValidationException("unsupported legacy occurrence section");
        }

        return (legacy[0], "## Occurrences\n" + legacy[1], "", true);
    }

    internal static JsonObject[] Observations(JsonObject history)
        => ((JsonArray)history["occurrences"]!)
            .SelectMany(o => (Field(o, "tests") as JsonArray ?? []).OfType<JsonObject>()
                .Select(test => (Test: test, Run: GetValueText(Field(o, "run_id")))))
            .DistinctBy(item => (Text(item.Test["name"]), ReadNormalizedNumber(item.Test["job_id"]),
                Text(item.Test["observation_id"]), Text(item.Test["observation_id"]) is null ? null : item.Run))
            .Select(item => item.Test).ToArray();

    private static string? ProjectTests(string body, string[] rows, int omitted)
    {
        if (rows.Length == 0)
        {
            return body;
        }

        var start = Markers(body, TestsStart);
        var end = Markers(body, TestsEnd);
        string prefix;
        string suffix;
        if (start.Count == 0 && end.Count == 0)
        {
            // This section is publisher-owned; all existing operator text stays intact.
            var occurrences = Markers(body, OccurrencesStart);
            if (occurrences.Count > 1)
            {
                return null;
            }

            prefix = occurrences.Count == 1 ? body[..occurrences[0].Index] : body.TrimEnd() + "\n\n";
            suffix = occurrences.Count == 1 ? "\n\n" + body[occurrences[0].Index..] : "\n";
        }
        else if (start.Count != 1 || end.Count != 1 || end[0].Index < start[0].Index + start[0].Length)
        {
            return null;
        }
        else
        {
            prefix = body[..start[0].Index];
            suffix = body[(end[0].Index + end[0].Length)..];
        }

        const string header = TestsStart + "\n## Observed tests\n\n";
        const string footer = "\n" + TestsEnd;
        // Only the managed presentation is bounded. Preserve durable observations,
        // operator text, and the separately managed latest occurrence.
        var section = header + string.Join("\n", rows.Skip(omitted)) + OmittedNotice(omitted) + footer;

        return prefix + section + suffix;
    }

    private static string OmittedNotice(int omitted)
        => omitted == 0 ? "" : FormattableString.Invariant($"\n\n{omitted} earlier observations omitted; complete records remain in cause history.");

    private static MatchCollection Markers(string body, string marker)
        // Exact names can include "<!-- ci-failure-tests:end -->" inside code spans.
        // Only a whole line owns a section; leave CRLF bytes outside the marker.
        => Regex.Matches(body, "^" + Regex.Escape(marker) + @"(?=\r?$)", RegexOptions.Multiline | RegexOptions.CultureInvariant);

    [GeneratedRegex(@"Showing [0-9]+ most recent of (?<total>[0-9]+) occurrences\.")]
    private static partial Regex OccurrenceSummaryCountRegex();

    [GeneratedRegex(@"^Showing [0-9]+ most recent of [0-9]+ occurrences\.\z")]
    private static partial Regex OccurrenceSummaryRegex();

    [GeneratedRegex(@"^\| [0-9]{4}-[0-9]{2}-[0-9]{2} \| \[(?<run>[0-9]+)\]\(https://github\.com/[^\n]+\) \| .* \| (main|unavailable|#[0-9]+) \|\z")]
    private static partial Regex OccurrenceRowRegex();

}
