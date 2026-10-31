// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Collections.Immutable;
using AnalyzeCiFailure;
using Aspire.TestUtilities;
using Xunit;

namespace Infrastructure.Tests;

/// <summary>Exercises aggregate issue publication without discarding independent causes or history.</summary>
public sealed partial class AnalyzeCiFailureWorkflowTests
{
    [Theory]
    [InlineData(10, 10)]
    [InlineData(11, 1)]
    [InlineData(15, 1)]
    [RequiresTools(["bash", "git", "jq"])]
    public async Task OverflowUsesOneIssueWhileRetainingEveryCause(int causeCount, int issueCount)
    {
        var ids = Enumerable.Range(1, causeCount).Select(index => $"cause-{index}").ToArray();
        await WriteValidationFixtureAsync(
            JsonSerializer.Serialize(new
            {
                run_id = 123, run_scope = "main", verdict = "transient-infra",
                failed_jobs = new[] { new { id = 123, classification = "transient-infra" } },
                failed_tests = Array.Empty<object>(), causes = ids,
            }),
            """{"run_id":123,"run_scope":"main","pr_numbers":""}""",
            """[{"id":123,"name":"Tests"}]""",
            ids.ToDictionary(id => id + ".json", id => CreateCause(id, "infra-failure", jobId: 123)));

        var result = await RunSuccessfulGroupedPublicationAsync();

        Assert.True(result.ExitCode == 0, result.Output);
        var requests = await ReadHttpRequestsAsync();
        Assert.Equal(issueCount, requests.Count(request =>
            request!["method"]!.GetValue<string>() == "POST" &&
            request["path"]!.GetValue<string>() == "/repos/microsoft/aspire/issues"));
        var record = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(_workspace.Path, "memory-repo/runs/123.json")))!;
        Assert.Equal(ids.Order(StringComparer.Ordinal), record["causes"]!.AsArray().Select(id => id!.GetValue<string>()).Order(StringComparer.Ordinal));
        foreach (var id in ids)
        {
            Assert.True(File.Exists(Path.Combine(_workspace.Path, "memory-repo/causes", id + ".json")));
        }
        if (causeCount > 10)
        {
            var body = await File.ReadAllTextAsync(Path.Combine(_workspace.Path, "issue-body.md"));
            foreach (var id in ids)
            {
                Assert.Contains(id, body, StringComparison.Ordinal);
            }
            Assert.Equal("https://github.com/microsoft/aspire/issues/77", record["overflow_issue_url"]!.GetValue<string>());
        }
    }

    [Fact]
    [RequiresTools(["bash", "git", "jq"])]
    public async Task AggregateRecoversAfterCreationAndRetiresOnlyOwnedContinuationContent()
    {
        await WriteOverflowFixtureAsync(causeCount: 120, largeDiagnostics: true);
        await File.WriteAllTextAsync(Path.Combine(_workspace.Path, "fail-overflow-comment-once"), "");
        var first = await RunSuccessfulGroupedPublicationAsync();
        Assert.Equal(1, first.ExitCode);
        var fakeGh = Path.Combine(Assert.Single(Directory.GetDirectories(_workspace.Path, "fake-bin-*")), "gh");

        var retry = await RunPublicationCommandAsync(fakeGh);

        Assert.True(retry.ExitCode == 0, retry.Output);
        var issuePath = Path.Combine(_workspace.Path, "issue.json");
        var issues = JsonNode.Parse(await File.ReadAllTextAsync(issuePath))!;
        issues[0]!["body"] = issues[0]!["body"]!.GetValue<string>() + "Operator issue notes.";
        await File.WriteAllTextAsync(issuePath, issues.ToJsonString());
        var pagesPath = Path.Combine(_workspace.Path, "overflow-comments.json");
        var pages = JsonNode.Parse(await File.ReadAllTextAsync(pagesPath))!.AsArray();
        Assert.True(pages.Count > 0);
        pages[0]!["body"] = pages[0]!["body"]!.GetValue<string>() + "Operator continuation notes.";
        var pageCount = pages.Count;
        await File.WriteAllTextAsync(pagesPath, pages.ToJsonString());
        await WriteOverflowFixtureAsync(causeCount: 11, largeDiagnostics: false);
        for (var index = 12; index <= 120; index++)
        {
            File.Delete(Path.Combine(_workspace.Path, "ci-analysis-output/causes", $"cause-{index}.json"));
        }

        var shortened = await RunPublicationCommandAsync(fakeGh);

        Assert.True(shortened.ExitCode == 0, shortened.Output);
        var updated = JsonNode.Parse(await File.ReadAllTextAsync(pagesPath))!.AsArray();
        Assert.Equal(pageCount, updated.Count);
        Assert.Contains("Operator continuation notes.", updated[0]!["body"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.All(updated, page => Assert.Contains("superseded", page!["body"]!.GetValue<string>(), StringComparison.Ordinal));
        Assert.Contains("Operator issue notes.", await File.ReadAllTextAsync(Path.Combine(_workspace.Path, "issue-body.md")), StringComparison.Ordinal);
        var requests = await ReadHttpRequestsAsync();
        Assert.Equal(1, requests.Count(request => request!["method"]!.GetValue<string>() == "POST" &&
            request["path"]!.GetValue<string>() == "/repos/microsoft/aspire/issues"));
    }

    [Theory]
    [InlineData("locked")]
    [InlineData("human")]
    [InlineData("duplicate")]
    [InlineData("broken-markers")]
    [RequiresTools(["bash", "git", "jq"])]
    public async Task AggregatePreflightRejectsUnsafeDestinationsBeforeFurtherMutations(string scenario)
    {
        await WriteOverflowFixtureAsync(causeCount: 11, largeDiagnostics: false);
        var first = await RunSuccessfulGroupedPublicationAsync();
        Assert.True(first.ExitCode == 0, first.Output);
        var path = Path.Combine(_workspace.Path, "issue.json");
        var issues = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsArray();
        var issue = issues[0]!;
        if (scenario == "locked")
        {
            issue["locked"] = true;
        }
        else if (scenario == "human")
        {
            issue["user"]!["login"] = "maintainer";
        }
        else if (scenario == "broken-markers")
        {
            issue["body"] = "<!-- ci-failure-overflow:123 -->\nNo managed section.";
        }
        else
        {
            var duplicate = issue.DeepClone();
            duplicate["number"] = 78;
            duplicate["html_url"] = "https://github.com/microsoft/aspire/issues/78";
            issues.Add(duplicate);
        }
        await File.WriteAllTextAsync(path, issues.ToJsonString());
        var before = (await ReadHttpRequestsAsync()).Count(request => request!["method"]!.GetValue<string>() != "GET");
        var fakeGh = Path.Combine(Assert.Single(Directory.GetDirectories(_workspace.Path, "fake-bin-*")), "gh");

        var retry = await RunPublicationCommandAsync(fakeGh);

        Assert.Equal(1, retry.ExitCode);
        Assert.Equal(before, (await ReadHttpRequestsAsync()).Count(request => request!["method"]!.GetValue<string>() != "GET"));
    }

    [Fact]
    [RequiresTools(["bash", "git", "jq"])]
    public async Task AggregateReplayKeepsFirstStoredJobAttribution()
    {
        await WriteOverflowFixtureAsync(causeCount: 11, largeDiagnostics: false);
        var first = await RunSuccessfulGroupedPublicationAsync();
        Assert.True(first.ExitCode == 0, first.Output);
        await File.WriteAllTextAsync(Path.Combine(EvidenceDirectory, "diagnostics/failed-jobs.json"), """[{"id":456,"name":"Replacement job"}]""");
        var path = Path.Combine(_workspace.Path, "ci-analysis-output/analysis-result.json");
        var report = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        report["failed_jobs"]![0]!["id"] = 456;
        await File.WriteAllTextAsync(path, report.ToJsonString());
        foreach (var file in Directory.GetFiles(Path.Combine(_workspace.Path, "ci-analysis-output/causes"), "*.json"))
        {
            var cause = JsonNode.Parse(await File.ReadAllTextAsync(file))!;
            cause["job_ids"] = new JsonArray(456);
            await File.WriteAllTextAsync(file, cause.ToJsonString());
        }
        var fakeGh = Path.Combine(Assert.Single(Directory.GetDirectories(_workspace.Path, "fake-bin-*")), "gh");

        var retry = await RunPublicationCommandAsync(fakeGh);

        Assert.True(retry.ExitCode == 0, retry.Output);
        var body = await File.ReadAllTextAsync(Path.Combine(_workspace.Path, "issue-body.md"));
        Assert.Equal(11, body.Split("Stored affected jobs (first occurrence): ` Tests `", StringSplitOptions.None).Length - 1);
        Assert.Equal(0, body.Split("Replacement job", StringSplitOptions.None).Length - 1);
    }

    [Theory]
    [InlineData("<!-- ci-failure-overflow-content:start -->")]
    [InlineData("<!-- ci-failure-overflow-content:end -->")]
    public void AggregateInlineDiagnosticMarkersRemainDataDuringReplay(string diagnostic)
    {
        const string marker = "<!-- ci-failure-overflow:123 -->";
        var content = "Diagnostic: " + MarkdownFormatting.CodeSpan(diagnostic) + "\n";
        var first = OverflowIssueRenderer.Merge(existing: null, marker, content);

        var replay = OverflowIssueRenderer.Merge(first, marker, content);

        Assert.Equal(first, replay);
    }

    private async Task WriteOverflowFixtureAsync(int causeCount, bool largeDiagnostics)
    {
        var ids = Enumerable.Range(1, causeCount).Select(index => $"cause-{index}").ToArray();
        var definitions = ids.ToDictionary(id => id + ".json", id =>
        {
            var cause = JsonNode.Parse(CreateCause(id, "infra-failure", jobId: 123))!;
            if (largeDiagnostics)
            {
                cause["title"] = new string('t', count: 200) + id;
                cause["error_pattern"] = new string('e', count: 480) + id;
            }
            return cause.ToJsonString();
        });
        await WriteValidationFixtureAsync(
            JsonSerializer.Serialize(new
            {
                run_id = 123, run_scope = "main", verdict = "transient-infra",
                failed_jobs = new[] { new { id = 123, classification = "transient-infra" } },
                failed_tests = Array.Empty<object>(), causes = ids,
            }),
            """{"run_id":123,"run_scope":"main","pr_numbers":""}""",
            """[{"id":123,"name":"Tests"}]""",
            definitions);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void AggregateUpdatesPreserveOperatorNotesAndLineEndings(string newline)
    {
        const string marker = "<!-- ci-failure-overflow:123 -->";
        var existing = OverflowIssueRenderer.Merge(existing: null, marker, "Old report").ReplaceLineEndings(newline) + "Operator notes: keep this.";

        var updated = OverflowIssueRenderer.Merge(existing, marker, "New report");

        Assert.Equal(
            (marker + "\n<!-- ci-failure-overflow-content:start -->\nNew report\n<!-- ci-failure-overflow-content:end -->").ReplaceLineEndings("\n") +
            newline + "Operator notes: keep this.",
            updated.Replace(marker + "\r\n", marker + "\n", StringComparison.Ordinal));
    }

    [Fact]
    public void AggregatePagesRetainEveryEntryWithinTheIssueBodyBudget()
    {
        var rows = Enumerable.Range(1, count: 300).Select(index => $"cause-{index}: " + new string('x', count: 1000) + "\n").ToArray();

        var pages = OverflowIssueRenderer.Pages(rows, "https://github.com/microsoft/aspire/actions/runs/123");

        Assert.True(pages.Count > 1);
        Assert.All(pages, page => Assert.True(System.Text.Encoding.UTF8.GetByteCount(page) <= 65000));
        var complete = string.Concat(pages);
        foreach (var row in rows)
        {
            Assert.Equal(1, complete.Split(row, StringSplitOptions.None).Length - 1);
        }
    }

    [Theory]
    [InlineData(10, false)]
    [InlineData(11, true)]
    public void AggregatePrValidationUsesCompactRenderingBeforeTheCommentBudget(int causeCount, bool accepted)
    {
        var evidence = AnalyzeCiFailureFixtures.CreateEvidence(RunScope.PullRequest);
        evidence = evidence with
        {
            TestEvidence = evidence.TestEvidence with
            {
                Failures = Enumerable.Range(1, causeCount).Select(index => new CollectedTestFailure
                {
                    Ref = $"test-{index}", JobId = 123, Name = $"Tests.Case{index}", DisplayName = $"Tests.Case{index}",
                    Diagnostics = new CollectedDiagnostics
                    {
                        Error = "Intermittent timeout",
                        StackTrace = "",
                        StandardOutput = new string('x', count: 4500),
                        StandardError = new string('y', count: 4500),
                    },
                    Sources = [new TestResultSource { ArtifactId = 10, ResultFile = "results.trx", Format = TestArtifactFormat.Trx, ExecutionId = null }],
                }).ToImmutableArray(),
            },
        };
        var proposal = AnalyzeCiFailureFixtures.CreateInfrastructureProposal(evidence) with
        {
            Jobs = [new ProposedJobDecision { JobId = 123, Classification = JobClassification.FlakyTest, Reason = "Independent timeouts" }],
            Tests = evidence.TestEvidence.Failures.Select(test => new ProposedTestDecision
            {
                Ref = test.Ref, Classification = TestClassification.Flaky, Reason = "Independent timeout evidence",
            }).ToImmutableArray(),
            Causes = evidence.TestEvidence.Failures.Select((test, index) => (ProposedCause)new NewCauseProposal
            {
                Definition = new ProposedCauseDefinition { Id = $"cause-{index}", Type = CauseType.FlakyTest, Title = "Timeout", ErrorPattern = "Intermittent timeout" },
                JobIds = [123], TestRefs = [test.Ref], Reason = "Distinct observed mechanism",
            }).ToImmutableArray(),
        };

        if (!accepted)
        {
            var error = Assert.Throws<ValidationException>(() => AnalysisValidator.Validate(evidence, proposal, "microsoft/aspire"));
            Assert.Contains("65000-byte", error.Message, StringComparison.Ordinal);
            return;
        }
        var result = AnalysisValidator.Validate(evidence, proposal, "microsoft/aspire");
        var comment = PrCommentRenderer.Render(result.Report, result.Evidence.Jobs, result.Run.Url);
        Assert.Contains("11 independent causes", comment, StringComparison.Ordinal);
        Assert.Equal(11, result.Report.Tests.Length);
    }
}
