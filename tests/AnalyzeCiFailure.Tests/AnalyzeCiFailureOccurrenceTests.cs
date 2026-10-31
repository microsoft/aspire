// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using AnalyzeCiFailure;
using Aspire.TestUtilities;
using VerifyXunit;
using Xunit;

namespace Infrastructure.Tests;

/// <summary>Verifies cause occurrence merging, replay identity and preservation of operator-owned content.</summary>
public sealed partial class AnalyzeCiFailureWorkflowTests
{
    private const string StoredOccurrenceRow = "| 2026-10-20 | [122](https://github.com/microsoft/aspire/actions/runs/122) | ` Original job ` | #42 |";
    private const string CurrentOccurrenceRow = "| 2026-10-21 | [123](https://github.com/microsoft/aspire/actions/runs/123) | ` Tests ` | #42 |";
    private const string OperatorSuffix = "\nOperator suffix: preserve 日本語.\nSecond operator line.\n";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManagedOccurrenceAppendPreservesOperatorPrefixAndSuffix(bool crlf)
    {
        var body = ExistingGroupedIssue.Replace(
            "<!-- ci-failure-occurrences:end -->",
            StoredOccurrenceRow + "\n<!-- ci-failure-occurrences:end -->",
            StringComparison.Ordinal) + "\n" + OperatorSuffix;
        if (crlf)
        {
            body = body.ReplaceLineEndings("\r\n");
        }

        var updated = CauseIssueRenderer.AppendOccurrence(body, CurrentOccurrenceRow, total: 2);

        Assert.StartsWith(body[..body.IndexOf("<!-- ci-failure-occurrences:start -->", StringComparison.Ordinal)], updated, StringComparison.Ordinal);
        Assert.EndsWith(crlf ? OperatorSuffix.ReplaceLineEndings("\r\n") : OperatorSuffix, updated, StringComparison.Ordinal);
        await Verifier.Verify(updated, "md").UseDirectory("Snapshots").UseParameters(crlf);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManagedOccurrenceMainMigrationPreservesOperatorPrefixAndSuffix(bool crlf)
    {
        var body = ExistingGroupedIssue
            .Replace("flaky-test", "main-repository-breakage", StringComparison.Ordinal)
            .Replace("Operator notes:", "Old generated attribution.\n**Type**: main-repository-breakage\n\nOperator notes:", StringComparison.Ordinal)
            .Replace("<!-- ci-failure-occurrences:end -->", StoredOccurrenceRow + "\n<!-- ci-failure-occurrences:end -->", StringComparison.Ordinal)
            + "\n" + OperatorSuffix;
        var canonical = body.Replace("Old generated attribution.", "Trusted generated details.", StringComparison.Ordinal)
            .Replace("Operator notes: keep this.", "", StringComparison.Ordinal);
        if (crlf)
        {
            body = body.ReplaceLineEndings("\r\n");
        }

        var updated = CauseIssueRenderer.MigrateMainBody(body, canonical);

        Assert.EndsWith(crlf ? OperatorSuffix.ReplaceLineEndings("\r\n") : OperatorSuffix, updated, StringComparison.Ordinal);
        await Verifier.Verify(updated, "md").UseDirectory("Snapshots").UseParameters(crlf);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManagedOccurrenceFittingCountsSuffixWithoutTrimmingOperatorText(bool observations)
    {
        var oldest = StoredOccurrenceRow.Replace("Original job", new string('x', count: 1000), StringComparison.Ordinal);
        var suffix = OperatorSuffix + new string('語', count: 80) + "\n";
        var body = ExistingGroupedIssue
            .Replace("Operator notes: keep this.\n", "Operator notes: keep this.\n\n", StringComparison.Ordinal)
            .Replace("## Occurrences\n\n", "## Occurrences\n\nShowing 2 most recent of 2 occurrences.\n\n", StringComparison.Ordinal)
            .Replace("<!-- ci-failure-occurrences:end -->", oldest + "\n" + CurrentOccurrenceRow + "\n<!-- ci-failure-occurrences:end -->", StringComparison.Ordinal)
            + "\n" + suffix;
        JsonObject[] tests = observations
            ? [new JsonObject { ["name"] = new string('t', count: 500), ["job_id"] = 123, ["job"] = "Tests" }]
            : [];
        var budget = Encoding.UTF8.GetByteCount(body) - Encoding.UTF8.GetByteCount(oldest) - 1 + (observations ? 200 : 0);

        var updated = CauseIssueRenderer.FitIssue(body, tests, budget);

        Assert.NotNull(updated);
        Assert.InRange(Encoding.UTF8.GetByteCount(updated), 1, budget);
        Assert.EndsWith(suffix, updated, StringComparison.Ordinal);
        Assert.Equal([CurrentOccurrenceRow], updated.Split('\n').Where(line => line.StartsWith("| 2026-", StringComparison.Ordinal)));
        Assert.Null(CauseIssueRenderer.FitIssue(body, tests, Encoding.UTF8.GetByteCount(updated) - 1));
        await Verifier.Verify(updated, "md").UseDirectory("Snapshots").UseParameters(observations);
    }

    [Theory]
    [InlineData("duplicate-start")]
    [InlineData("duplicate-end")]
    [InlineData("reversed")]
    [InlineData("inside-notes")]
    [InlineData("legacy-trailing-notes")]
    public void ManagedOccurrenceAppendRejectsAmbiguousOrUnrecognizedRegion(string shape)
    {
        var body = shape switch
        {
            "duplicate-start" => ExistingGroupedIssue.Replace("## Occurrences", "<!-- ci-failure-occurrences:start -->\n## Occurrences", StringComparison.Ordinal),
            "duplicate-end" => ExistingGroupedIssue + "\n<!-- ci-failure-occurrences:end -->",
            "reversed" => ExistingGroupedIssue.Replace("occurrences:start", "occurrences:temporary", StringComparison.Ordinal)
                .Replace("occurrences:end", "occurrences:start", StringComparison.Ordinal)
                .Replace("occurrences:temporary", "occurrences:end", StringComparison.Ordinal),
            "inside-notes" => ExistingGroupedIssue.Replace("<!-- ci-failure-occurrences:end -->", "Operator content inside.\n<!-- ci-failure-occurrences:end -->", StringComparison.Ordinal),
            _ => ExistingGroupedIssue.Replace("<!-- ci-failure-occurrences:start -->\n", "", StringComparison.Ordinal)
                .Replace("<!-- ci-failure-occurrences:end -->", "Un-delimited trailing notes.", StringComparison.Ordinal),
        };

        var error = Assert.Throws<ValidationException>(() => CauseIssueRenderer.AppendOccurrence(body, CurrentOccurrenceRow, total: 1));

        Assert.Equal(
            shape is "duplicate-start" or "duplicate-end" or "reversed"
            ? "ambiguous managed occurrence section" : "unsupported occurrence section contents",
            error.Message);
    }

    [Theory]
    [InlineData("prefix")]
    [InlineData("suffix")]
    [InlineData("managed")]
    [InlineData("managed-with-notes")]
    [RequiresTools(["bash", "jq", "git"])]
    public async Task ManagedOccurrenceReplayGuardIgnoresHumanLinksOutsideRegion(string location)
    {
        await WriteGroupedFixtureAsync();
        const string link = "Human investigation: [123](https://github.com/microsoft/aspire/actions/runs/123).";
        var body = ExistingGroupedIssue;
        var expectedRow = CurrentOccurrenceRow;
        if (location.StartsWith("managed", StringComparison.Ordinal))
        {
            expectedRow = CurrentOccurrenceRow.Replace("` Tests `", "` Already displayed job `", StringComparison.Ordinal);
            body = body.Replace("<!-- ci-failure-occurrences:end -->", expectedRow + "\n<!-- ci-failure-occurrences:end -->", StringComparison.Ordinal);
        }
        if (location is "prefix" or "managed-with-notes")
        {
            body = body.Replace("Operator notes: keep this.", "Operator notes: keep this.\n" + link, StringComparison.Ordinal);
        }
        if (location is "suffix" or "managed-with-notes")
        {
            body += "\n\n" + link + "\n" + OperatorSuffix;
        }

        var result = await RunSuccessfulGroupedPublicationAsync(body: body);

        Assert.True(result.ExitCode == 0, result.Output);
        var path = Path.Combine(_workspace.Path, "issue-body.md");
        Assert.True(File.Exists(path), result.Output);
        var updated = await File.ReadAllTextAsync(path);
        Assert.Equal([expectedRow], updated.Split('\n').Where(line => line.StartsWith("| 2026-", StringComparison.Ordinal)));
        await Verifier.Verify(updated, "md").UseDirectory("Snapshots").UseParameters(location);
    }

    [Theory]
    [InlineData("pull-request")]
    [InlineData("main")]
    [RequiresTools(["bash", "jq", "git"])]
    public async Task ManagedOccurrenceReplayRestoresFirstStoredJobDateUrlAndSubject(string replayScope)
    {
        await WriteGroupedFixtureAsync();
        var first = await RunSuccessfulGroupedPublicationAsync();
        Assert.True(first.ExitCode == 0, first.Output);
        var bodyPath = Path.Combine(_workspace.Path, "issue-body.md");
        var body = await File.ReadAllTextAsync(bodyPath);
        var originalRow = Assert.Single(body.Split('\n'), line => line.StartsWith("| 2026-", StringComparison.Ordinal));
        var firstHistory = await RunProcessAsync(
            "git",
            ["--no-pager", "--git-dir", "remote.git",
            "show", "refs/heads/memory/ci-failure-analysis:causes/debugger-timeout.json"]);
        Assert.Equal(0, firstHistory.ExitCode);
        await File.WriteAllTextAsync(
            Path.Combine(_workspace.Path, "issue.json"),
            new JsonArray(new JsonObject
            {
                ["number"] = 77,
                ["state"] = "open",
                ["body"] = body.Replace(originalRow + "\n", "", StringComparison.Ordinal),
                ["labels"] = new JsonArray(new JsonObject { ["name"] = "ci-failure-cause" }),
            }).ToJsonString());
        File.Delete(bodyPath);
        await File.WriteAllTextAsync(
            Path.Combine(EvidenceDirectory, "diagnostics/failed-jobs.json"),
            """[{"id":123,"name":"Renamed trusted job"}]""");
        var trustedPath = Path.Combine(EvidenceDirectory, "test-failures.json");
        var collected = JsonNode.Parse(await File.ReadAllTextAsync(trustedPath))!;
        foreach (var test in collected.AsArray())
        {
            test!["job"] = "Renamed trusted job";
            test["error"] = "Changed trusted evidence";
        }
        await File.WriteAllTextAsync(trustedPath, collected.ToJsonString());
        var summaryPath = Path.Combine(_workspace.Path, "ci-analysis-output", "analysis-result.json");
        var summary = JsonNode.Parse(await File.ReadAllTextAsync(summaryPath))!;
        foreach (var test in summary["failed_tests"]!.AsArray())
        {
            test!["job"] = "Renamed trusted job";
            test["error"] = "Changed analyzer evidence";
            test["reason"] = "New analyzer explanation";
        }
        summary["run_scope"] = replayScope;
        summary["pr"] = replayScope == "main" ? null : new JsonObject { ["number"] = 43 };
        await File.WriteAllTextAsync(summaryPath, summary.ToJsonString());
        await File.WriteAllTextAsync(
            Path.Combine(EvidenceDirectory, "run-context.json"),
            new JsonObject { ["run_id"] = 123, ["run_scope"] = replayScope, ["pr_numbers"] = replayScope == "main" ? "" : "43" }.ToJsonString());
        var fakeGh = Path.Combine(Assert.Single(Directory.GetDirectories(_workspace.Path, "fake-bin-*")), "gh");

        var replay = await RunPublicationCommandAsync(fakeGh, "2026-10-23T12:00:00Z");

        Assert.True(replay.ExitCode == 0, replay.Output);
        Assert.True(File.Exists(bodyPath), replay.Output);
        var updated = await File.ReadAllTextAsync(bodyPath);
        var restoredRow = Assert.Single(updated.Split('\n'), line => line.StartsWith("| 2026-", StringComparison.Ordinal));
        Assert.Equal(originalRow, restoredRow);
        var replayHistory = await RunProcessAsync(
            "git",
            ["--no-pager", "--git-dir", "remote.git",
            "show", "refs/heads/memory/ci-failure-analysis:causes/debugger-timeout.json"]);
        Assert.Equal(0, replayHistory.ExitCode);
        Assert.True(
            JsonNode.DeepEquals(JsonNode.Parse(firstHistory.Output), JsonNode.Parse(replayHistory.Output)),
            "Replay must leave the first stored occurrence and its trusted diagnostics unchanged.");
        await Verifier.Verify(
            new JsonObject
            {
                ["row"] = restoredRow,
                ["occurrences"] = JsonNode.Parse(replayHistory.Output)!["occurrences"]!.DeepClone(),
            }.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }),
            "json").UseDirectory("Snapshots").UseParameters(replayScope);
    }
}
