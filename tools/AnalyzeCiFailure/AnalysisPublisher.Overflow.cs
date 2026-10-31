// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using static AnalyzeCiFailure.CiFailureJson;

namespace AnalyzeCiFailure;

/// <summary>Preflights and publishes one run-level destination for many independent causes.</summary>
internal sealed partial class AnalysisPublisher
{
    private async Task<OverflowPublication> PrepareOverflowAsync(
        ValidatedAnalysis analysis,
        List<PreparedCause> causes,
        JsonObject? storedRun)
    {
        var runId = analysis.Bundle.Run.Id.ToString(CultureInfo.InvariantCulture);
        var marker = $"<!-- ci-failure-overflow:{runId} -->";
        var inventory = await LoadIssueInventoryAsync();
        var matches = inventory.OfType<JsonObject>().Where(issue =>
            OverflowIssueRenderer.HasMarker(Text(issue["body"]), marker))
            .DistinctBy(issue => GitHubJson.ReadRequiredId(issue["number"])).ToArray();
        if (matches.Length > 1)
        {
            throw new ValidationException("Multiple aggregate issues identify this source run.");
        }
        JsonObject? existing = null;
        var linked = Text(storedRun?["overflow_issue_url"]);
        if (linked is not null || matches.Length == 1)
        {
            var number = linked is null ? GitHubJson.ReadRequiredId(matches[0]["number"]).ToString(CultureInfo.InvariantCulture) :
                linked[(linked.LastIndexOf('/') + 1)..];
            if (linked is not null && (linked != $"https://github.com/{_repository}/issues/{number}" ||
                !long.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var linkedId) ||
                linkedId <= 0 || linkedId.ToString(CultureInfo.InvariantCulture) != number ||
                matches.Any(issue => GetValueText(issue["number"]) != number)))
            {
                throw new ValidationException("Stored aggregate issue link conflicts with the source run.");
            }
            existing = await _github.GetAsync($"repos/{_repository}/issues/{number}") as JsonObject;
            if (existing is null || GetValueText(existing["number"]) != number ||
                Text(existing["html_url"]) != $"https://github.com/{_repository}/issues/{number}" ||
                Text(Field(existing["user"], "login")) != "github-actions[bot]" ||
                !IsFalse(existing["locked"]) || Text(existing["state"]) is not ("open" or "closed") ||
                Text(existing["state_reason"]) == "duplicate" || existing["pull_request"] is not null ||
                !OverflowIssueRenderer.HasMarker(Text(existing["body"]), marker))
            {
                throw new ValidationException("Aggregate issue destination is not a uniquely identified, unlocked automation issue.");
            }
        }

        var pages = OverflowIssueRenderer.Pages(Rows(), analysis.Run.Url);
        var body = OverflowIssueRenderer.Merge(Text(existing?["body"]), marker, pages[0]);
        var comments = existing is null ? [] :
            await _github.GetRowsAsync($"repos/{_repository}/issues/{GetValueText(existing["number"])}/comments?per_page=100");
        var owned = new Dictionary<int, JsonObject>();
        var ownedIds = new HashSet<long>();
        var prefix = $"<!-- ci-failure-overflow:{runId}:page:";
        foreach (var comment in comments.OfType<JsonObject>().Where(comment =>
            Text(Field(comment["user"], "login")) == "github-actions[bot]"))
        {
            var text = Text(comment["body"]) ?? "";
            if (!text.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }
            var end = text.IndexOf(" -->", prefix.Length, StringComparison.Ordinal);
            if (end < 0 || !int.TryParse(text.AsSpan(prefix.Length, end - prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var page) ||
                page < 2 || page.ToString(CultureInfo.InvariantCulture) != text[prefix.Length..end] ||
                !OverflowIssueRenderer.HasMarker(text, text[..(end + 4)]) ||
                !GitHubJson.TryReadId(comment["id"], out var id) || !ownedIds.Add(id) || !owned.TryAdd(page, comment))
            {
                throw new ValidationException("Aggregate continuation ownership or page identity is ambiguous.");
            }
        }
        var continuations = new List<OverflowContinuation>();
        foreach (var page in Enumerable.Range(start: 2, Math.Max(0, pages.Count - 1)).Concat(owned.Keys).Distinct().Order())
        {
            var content = page <= pages.Count ? pages[page - 1] :
                "[automated] This continuation is superseded by the updated aggregate report.";
            owned.TryGetValue(page, out var previous);
            var pageMarker = prefix + page.ToString(CultureInfo.InvariantCulture) + " -->";
            continuations.Add(new(
                previous is null ? null : GetValueText(previous["id"]),
                OverflowIssueRenderer.Merge(Text(previous?["body"]), pageMarker, content)));
        }
        return new(existing, body, continuations,
            $"[CI Failure] {causes.Count.ToString(CultureInfo.InvariantCulture)} independent causes in run {runId}");

        IEnumerable<string> Rows()
        {
            foreach (var item in causes)
            {
                var id = MarkdownFormatting.CodeSpan(item.Cause.Id);
                var title = MarkdownFormatting.CodeSpan(DiagnosticTextSanitizer.Sanitize(Text(item.History["title"]) ?? item.Cause.Id, limit: 238));
                yield return $"### {id}: {title}\n\n- Type: {EnumText(item.Cause.Type)}\n";
                var diagnostic = DiagnosticTextSanitizer.Sanitize(Text(item.History["error_pattern"]) ?? "", limit: 500);
                yield return "- Diagnostic: " + MarkdownFormatting.CodeSpan(diagnostic) + "\n";
                var issueUrl = Text(item.History["issue_url"]);
                if (issueUrl is not null && System.Text.RegularExpressions.Regex.IsMatch(
                    issueUrl, "^" + System.Text.RegularExpressions.Regex.Escape($"https://github.com/{_repository}/issues/") + @"[1-9][0-9]*\z"))
                {
                    yield return $"- Existing cause issue: {issueUrl}\n";
                }
                if (item.Replayed)
                {
                    // The stored job attribution is an opaque aggregate, not a reversible
                    // list. A later proposal must not replace its original membership.
                    var occurrence = ((JsonArray)item.History["occurrences"]!).First(value =>
                        ReadNormalizedNumber(Field(value, "run_id")) == NormalizedJsonNumber.Parse(runId));
                    var storedJobs = DiagnosticTextSanitizer.Sanitize(Text(occurrence?["job"]) ?? "");
                    if (storedJobs.Length == 0)
                    {
                        yield return "- Stored affected-job attribution is unavailable in the first occurrence.\n";
                    }
                    foreach (var chunk in storedJobs.EnumerateRunes().Chunk(size: 1000))
                    {
                        yield return "- Stored affected jobs (first occurrence): " +
                            MarkdownFormatting.CodeSpan(string.Concat(chunk)) + "\n";
                    }
                    yield return "\n";
                    continue;
                }
                foreach (var job in analysis.Bundle.Jobs.Where(job =>
                    item.Cause.JobIds.Contains(NormalizedJsonNumber.Parse(job.Id.ToString(CultureInfo.InvariantCulture)))))
                {
                    var name = MarkdownFormatting.CodeSpan(DiagnosticTextSanitizer.Sanitize(job.Name, limit: 500));
                    var count = analysis.Bundle.TestEvidence.Failures.Count(test => test.JobId == job.Id);
                    yield return $"- {id} job {job.Id.ToString(CultureInfo.InvariantCulture)}: {name}; " +
                        $"{count.ToString(CultureInfo.InvariantCulture)} observed test failures. " +
                        $"[Job]({analysis.Run.Url}/job/{job.Id.ToString(CultureInfo.InvariantCulture)})\n";
                }
                yield return "\n";
            }
        }
    }

    private async Task<string> PublishOverflowAsync(OverflowPublication publication)
    {
        string url;
        if (publication.Existing is null)
        {
            url = await ExecuteEffectAsync(
                "issue-create",
                cause: null,
                destination: null,
                HttpMethod.Post,
                $"repos/{_repository}/issues",
                new JsonObject
                {
                    ["title"] = publication.Title,
                    ["body"] = publication.Body,
                    ["labels"] = new JsonArray("ci-failure-cause"),
                },
                HttpStatusCode.Created);
        }
        else
        {
            url = Text(publication.Existing["html_url"])!;
            await ExecuteEffectAsync(
                "overflow-issue-edit",
                cause: null,
                url,
                HttpMethod.Patch,
                $"repos/{_repository}/issues/{GetValueText(publication.Existing["number"])}",
                new JsonObject { ["body"] = publication.Body, ["title"] = publication.Title },
                HttpStatusCode.OK);
        }
        var number = url[(url.LastIndexOf('/') + 1)..];
        foreach (var page in publication.Continuations)
        {
            var create = page.Id is null;
            await ExecuteEffectAsync(
                create ? "overflow-comment-create" : "overflow-comment-edit",
                cause: null,
                create ? url : url + "#issuecomment-" + page.Id,
                create ? HttpMethod.Post : HttpMethod.Patch,
                create ? $"repos/{_repository}/issues/{number}/comments" : $"repos/{_repository}/issues/comments/{page.Id}",
                new JsonObject { ["body"] = page.Body },
                create ? HttpStatusCode.Created : HttpStatusCode.OK);
        }
        return url;
    }

    /// <summary>Holds the completely preflighted aggregate issue and continuation bodies.</summary>
    private sealed record OverflowPublication(JsonObject? Existing, string Body, List<OverflowContinuation> Continuations, string Title);

    /// <summary>Identifies an owned continuation to update or a new page to create.</summary>
    private sealed record OverflowContinuation(string? Id, string Body);
}
