// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using Aspire.TestUtilities;
using VerifyXunit;
using Xunit;

namespace Infrastructure.Tests;

public sealed partial class AnalyzeCiFailureWorkflowTests
{
    [Theory]
    [InlineData("unmanaged")]
    [InlineData("matching")]
    [InlineData("legacy-matching")]
    [RequiresTools(["bash", "jq", "git"])]
    public async Task AdoptionPreservesNotesAndRecordsRecognizableDestinationAcrossReplay(string scenario)
    {
        await WriteGroupedFixtureAsync();
        const string original = "<!-- CIF:legacy-fingerprint -->\r\nHuman diagnosis.\r\n\r\nKeep these notes exactly.\r\n";
        var target = AdoptionTarget(scenario switch
        {
            "matching" => ExistingGroupedIssue,
            "legacy-matching" => ExistingGroupedIssue.Replace("<!-- ci-failure-cause-type:flaky-test -->", "**Type**: flaky-test", StringComparison.Ordinal),
            _ => original,
        });
        await File.WriteAllTextAsync(Path.Combine(_workspace.Path, "issue-details.json"), target.ToJsonString());

        var first = await RunSuccessfulGroupedPublicationAsync(adoptIssue: "77");

        Assert.True(first.ExitCode == 0, first.Output);
        var firstBody = await File.ReadAllTextAsync(Path.Combine(_workspace.Path, "issue-body.md"));
        await Verifier.Verify(firstBody, "md").UseDirectory("Snapshots").UseParameters(scenario);
        if (scenario == "unmanaged")
        {
            Assert.EndsWith(original + "\n<!-- ci-failure-adoption-notes:end -->\n", firstBody, StringComparison.Ordinal);
        }
        var followUp = firstBody + "\nOperator follow-up: retain this too.\n";
        target["body"] = followUp;
        target["labels"] = new JsonArray(new JsonObject { ["name"] = "ci-failure-cause" }, new JsonObject { ["name"] = "area-testing" });
        await File.WriteAllTextAsync(Path.Combine(_workspace.Path, "issue-details.json"), target.ToJsonString());
        var fakeGh = Path.Combine(Assert.Single(Directory.GetDirectories(_workspace.Path, "fake-bin-*")), "gh");

        var replay = await RunTypedPublicationCommandAsync(fakeGh, "2026-10-22T01:00:00Z", ["--adopt-issue", "77"]);

        Assert.True(replay.ExitCode == 0, replay.Output);
        Assert.Equal(followUp, await File.ReadAllTextAsync(Path.Combine(_workspace.Path, "issue-body.md")));
        var normal = await RunTypedPublicationCommandAsync(fakeGh, "2026-10-23T01:00:00Z");
        Assert.True(normal.ExitCode == 0, normal.Output);
        Assert.Equal(followUp, await File.ReadAllTextAsync(Path.Combine(_workspace.Path, "issue-body.md")));
        var durable = await RunProcessAsync("git",
            ["--no-pager", "--git-dir", "remote.git", "show", "refs/heads/memory/ci-failure-analysis:causes/debugger-timeout.json"]);
        Assert.True(durable.ExitCode == 0, durable.Output);
        var history = JsonNode.Parse(durable.Output)!;
        Assert.Equal("https://github.com/microsoft/aspire/issues/77", history["issue_url"]!.GetValue<string>());
        Assert.Single(history["occurrences"]!.AsArray());
        var requests = await ReadHttpRequestsAsync();
        Assert.All(requests.Where(request => request!["method"]!.GetValue<string>() != "GET"), request =>
        {
            Assert.NotEqual("/repos/microsoft/aspire/issues", request!["path"]!.GetValue<string>());
            Assert.NotEqual("/repos/microsoft/aspire/issues/77/comments", request["path"]!.GetValue<string>());
            Assert.Null(request["body"]!["state"]);
            if (request["method"]!.GetValue<string>() == "PATCH")
            {
                Assert.Null(request["body"]!["labels"]);
                Assert.Null(request["body"]!["title"]);
            }
        });
        var additions = requests.Where(request => request!["path"]!.GetValue<string>() == "/repos/microsoft/aspire/issues/77/labels").ToArray();
        Assert.Equal(2, additions.Length);
        Assert.All(additions, request => Assert.Equal(
            ["ci-failure-cause", "test-failure"],
            request!["body"]!["labels"]!.AsArray().Select(label => label!.GetValue<string>())));
    }

    [Theory]
    [InlineData("closed")]
    [InlineData("locked")]
    [InlineData("unknown-lock")]
    [InlineData("pull-request")]
    [InlineData("wrong-repository")]
    [InlineData("wrong-number")]
    [InlineData("cause-marker")]
    [InlineData("type-marker")]
    [InlineData("managed-section")]
    [InlineData("broken-matching-section")]
    [InlineData("duplicate-type-marker")]
    [InlineData("overlapping-sections")]
    [InlineData("malformed-body")]
    [InlineData("oversized")]
    [InlineData("inventory-conflict")]
    [InlineData("history-conflict")]
    [RequiresTools(["bash", "jq", "git"])]
    public async Task AdoptionRejectsUnsafeTargetsBeforeAnyPersistentWrites(string scenario)
    {
        await WriteGroupedFixtureAsync();
        var target = AdoptionTarget("Human issue notes.");
        JsonObject? history = null;
        switch (scenario)
        {
            case "closed": target["state"] = "closed"; break;
            case "locked": target["locked"] = true; break;
            case "unknown-lock": target.Remove("locked"); break;
            case "pull-request": target["pull_request"] = new JsonObject(); break;
            case "wrong-repository": target["html_url"] = "https://github.com/other/repo/issues/77"; break;
            case "wrong-number": target["number"] = 78; break;
            case "cause-marker": target["body"] = "<!-- ci-failure-cause:other-cause -->\nHuman notes."; break;
            case "type-marker": target["body"] = "<!-- ci-failure-cause-type:infra-failure -->\nHuman notes."; break;
            case "managed-section": target["body"] = "<!-- ci-failure-occurrences:start -->\nKeep this."; break;
            case "broken-matching-section": target["body"] = ExistingGroupedIssue.Replace("<!-- ci-failure-occurrences:end -->", "", StringComparison.Ordinal); break;
            case "duplicate-type-marker": target["body"] = ExistingGroupedIssue + "\n<!-- ci-failure-cause-type:flaky-test -->"; break;
            case "overlapping-sections": target["body"] = ExistingGroupedIssue.Replace("Operator notes: keep this.", "<!-- ci-failure-tests:start -->", StringComparison.Ordinal) + "\n<!-- ci-failure-tests:end -->"; break;
            case "malformed-body": target["body"] = new JsonObject(); break;
            case "oversized": target["body"] = new string('x', 65536); break;
            case "inventory-conflict":
                var other = AdoptionTarget(ExistingGroupedIssue);
                other["number"] = 78;
                other["html_url"] = "https://github.com/microsoft/aspire/issues/78";
                other["labels"] = new JsonArray(new JsonObject { ["name"] = "ci-failure-cause" });
                await File.WriteAllTextAsync(Path.Combine(_workspace.Path, "issue.json"), new JsonArray(other).ToJsonString());
                break;
            case "history-conflict":
                history = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(_workspace.Path, "ci-analysis-output/causes/debugger-timeout.json")))!.AsObject();
                history["issue_url"] = "https://github.com/microsoft/aspire/issues/78";
                history["occurrences"] = new JsonArray();
                break;
        }
        await File.WriteAllTextAsync(Path.Combine(_workspace.Path, "issue-details.json"), target.ToJsonString());

        var result = await RunSuccessfulGroupedPublicationAsync(history, adoptIssue: "77");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains(scenario switch
        {
            "cause-marker" or "type-marker" or "managed-section" or "duplicate-type-marker" => "conflicting analyzer identity",
            "broken-matching-section" => "ambiguous managed occurrence section",
            "overlapping-sections" => "overlapping managed test and occurrence sections",
            "oversized" => "exceeds the publication budget",
            "inventory-conflict" => "different managed issue already owns this cause",
            "history-conflict" => "existing issue linkage",
            _ => "open, unlocked ordinary issue",
        }, result.Output, StringComparison.Ordinal);
        if (File.Exists(Path.Combine(_workspace.Path, "http-requests.jsonl")))
        {
            Assert.All(await ReadHttpRequestsAsync(), request => Assert.Equal("GET", request!["method"]!.GetValue<string>()));
        }
        var remote = await RunProcessAsync("git",
            ["--no-pager", "--git-dir", "remote.git", "show-ref", "--verify", "refs/heads/memory/ci-failure-analysis"]);
        Assert.Equal(_publicationHistoryBefore, remote);
        Assert.False(File.Exists(Path.Combine(_workspace.Path, "memory-repo/runs/123.json")));
    }

    [Fact]
    [RequiresTools(["bash", "jq", "git"])]
    public async Task AdoptionRejectsTargetLinkedFromAnotherCauseBeforeFurtherWrites()
    {
        await WriteGroupedFixtureAsync();
        await File.WriteAllTextAsync(Path.Combine(_workspace.Path, "issue-details.json"), AdoptionTarget("Human notes.").ToJsonString());
        var first = await RunSuccessfulGroupedPublicationAsync(adoptIssue: "77");
        Assert.True(first.ExitCode == 0, first.Output);
        var causeDirectory = Path.Combine(_workspace.Path, "memory-repo/causes");
        var history = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(causeDirectory, "debugger-timeout.json")))!;
        history["id"] = "another-cause";
        await File.WriteAllTextAsync(Path.Combine(causeDirectory, "another-cause.json"), history.ToJsonString());
        var mutations = (await ReadHttpRequestsAsync()).Count(request => request!["method"]!.GetValue<string>() != "GET");
        var fakeGh = Path.Combine(Assert.Single(Directory.GetDirectories(_workspace.Path, "fake-bin-*")), "gh");

        var result = await RunTypedPublicationCommandAsync(fakeGh, "2026-10-22T01:00:00Z", ["--adopt-issue", "77"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("already linked to a different cause in history", result.Output, StringComparison.Ordinal);
        Assert.Equal(mutations, (await ReadHttpRequestsAsync()).Count(request => request!["method"]!.GetValue<string>() != "GET"));
        var remote = await RunProcessAsync("git",
            ["--no-pager", "--git-dir", "remote.git", "show-ref", "--verify", "refs/heads/memory/ci-failure-analysis"]);
        Assert.Equal(_publicationHistoryBefore, remote);
    }

    [Theory]
    [InlineData("body")]
    [InlineData("label")]
    [RequiresTools(["bash", "jq", "git"])]
    public async Task AdoptionReservesDestinationBeforeEffectsAndRequiresSameTargetRepair(string failedEffect)
    {
        await WriteGroupedFixtureAsync();
        const string original = "<!-- CIF:legacy-fingerprint -->\nOperator notes: retain these.";
        var targetPath = Path.Combine(_workspace.Path, "issue-details.json");
        await File.WriteAllTextAsync(targetPath, AdoptionTarget(original).ToJsonString());
        await File.WriteAllTextAsync(Path.Combine(_workspace.Path, "persist-issue-effects"), "");
        await File.WriteAllTextAsync(Path.Combine(_workspace.Path,
            failedEffect == "body" ? "fail-issue-create-once" : "fail-issue-label-add-once"), "");

        var first = await RunSuccessfulGroupedPublicationAsync(adoptIssue: "77");

        Assert.Equal(1, first.ExitCode);
        Assert.Contains("HTTP 500", first.Output, StringComparison.Ordinal);
        var remote = await RunProcessAsync("git",
            ["--no-pager", "--git-dir", "remote.git", "show", "refs/heads/memory/ci-failure-analysis:causes/debugger-timeout.json"]);
        Assert.True(remote.ExitCode == 0, remote.Output);
        Assert.Equal("https://github.com/microsoft/aspire/issues/77", JsonNode.Parse(remote.Output)!["issue_url"]!.GetValue<string>());
        var target = JsonNode.Parse(await File.ReadAllTextAsync(targetPath))!;
        if (failedEffect == "body")
        {
            Assert.Equal(original, target["body"]!.GetValue<string>());
        }
        else
        {
            Assert.StartsWith("<!-- ci-failure-cause:debugger-timeout -->\n", target["body"]!.GetValue<string>(), StringComparison.Ordinal);
            Assert.Equal(["area-testing"], target["labels"]!.AsArray().Select(label => label!["name"]!.GetValue<string>()));
        }
        var mutations = (await ReadHttpRequestsAsync()).Count(request => request!["method"]!.GetValue<string>() != "GET");
        var fakeGh = Path.Combine(Assert.Single(Directory.GetDirectories(_workspace.Path, "fake-bin-*")), "gh");

        var normal = await RunTypedPublicationCommandAsync(fakeGh, "2026-10-22T01:00:00Z");
        var redirected = await RunTypedPublicationCommandAsync(fakeGh, "2026-10-22T01:00:00Z", ["--adopt-issue", "78"]);

        Assert.Equal(1, normal.ExitCode);
        Assert.Contains("retry publish --adopt-issue 77 for this same destination", normal.Output, StringComparison.Ordinal);
        Assert.Equal(1, redirected.ExitCode);
        Assert.Contains("existing issue linkage", redirected.Output, StringComparison.Ordinal);
        Assert.Equal(mutations, (await ReadHttpRequestsAsync()).Count(request => request!["method"]!.GetValue<string>() != "GET"));
        var unchanged = await RunProcessAsync("git",
            ["--no-pager", "--git-dir", "remote.git", "show-ref", "--verify", "refs/heads/memory/ci-failure-analysis"]);
        Assert.Equal(_publicationHistoryBefore, unchanged);

        var repaired = await RunTypedPublicationCommandAsync(fakeGh, "2026-10-22T01:00:00Z", ["--adopt-issue", "77"]);

        Assert.True(repaired.ExitCode == 0, repaired.Output);
        target = JsonNode.Parse(await File.ReadAllTextAsync(targetPath))!;
        Assert.EndsWith(original + "\n<!-- ci-failure-adoption-notes:end -->\n", target["body"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(["area-testing", "ci-failure-cause", "test-failure"],
            target["labels"]!.AsArray().Select(label => label!["name"]!.GetValue<string>()));
        var completed = await RunTypedPublicationCommandAsync(fakeGh, "2026-10-23T01:00:00Z");
        Assert.True(completed.ExitCode == 0, completed.Output);
        Assert.All(await ReadHttpRequestsAsync(), request =>
        {
            if (request!["method"]!.GetValue<string>() != "GET")
            {
                Assert.NotEqual("/repos/microsoft/aspire/issues", request["path"]!.GetValue<string>());
                Assert.NotEqual("/repos/microsoft/aspire/issues/78", request["path"]!.GetValue<string>());
            }
        });
        var history = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(_workspace.Path, "memory-repo/causes/debugger-timeout.json")))!;
        Assert.Equal("https://github.com/microsoft/aspire/issues/77", history["issue_url"]!.GetValue<string>());
        Assert.Single(history["occurrences"]!.AsArray());
    }

    [Theory]
    [InlineData("code-issue")]
    [InlineData("multiple-causes")]
    [InlineData("aggregate")]
    [InlineData("zero")]
    [InlineData("negative")]
    [InlineData("url")]
    [RequiresTools(["bash", "jq", "git"])]
    public async Task AdoptionRequiresSingleRecurringCauseAndPositiveIssueNumber(string scenario)
    {
        await WriteGroupedFixtureAsync();
        var directory = Path.Combine(_workspace.Path, "ci-analysis-output");
        var summaryPath = Path.Combine(directory, "analysis-result.json");
        var summary = JsonNode.Parse(await File.ReadAllTextAsync(summaryPath))!;
        if (scenario == "code-issue")
        {
            summary["verdict"] = "code-issue";
            summary["causes"] = new JsonArray();
            summary["failed_jobs"]![0]!["classification"] = "code-issue";
            foreach (var test in summary["failed_tests"]!.AsArray())
            {
                test!["classification"] = "code-issue";
            }
            File.Delete(Path.Combine(directory, "causes/debugger-timeout.json"));
        }
        else if (scenario is "multiple-causes" or "aggregate")
        {
            var cause = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "causes/debugger-timeout.json")))!;
            for (var index = 1; index < (scenario == "aggregate" ? 11 : 2); index++)
            {
                var id = $"other-cause-{index}";
                cause["id"] = id;
                summary["causes"]!.AsArray().Add(id);
                await File.WriteAllTextAsync(Path.Combine(directory, "causes", id + ".json"), cause.ToJsonString());
            }
        }
        await File.WriteAllTextAsync(summaryPath, summary.ToJsonString());

        var result = await RunSuccessfulGroupedPublicationAsync(adoptIssue: scenario switch
        {
            "zero" => "0",
            "negative" => "-1",
            "url" => "https://github.com/microsoft/aspire/issues/77",
            _ => "77",
        });

        Assert.Equal(1, result.ExitCode);
        Assert.Contains(scenario is "zero" or "negative" or "url" ? "canonical positive" : "exactly one validated recurring cause",
            result.Output, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_workspace.Path, "http-requests.jsonl")));
        Assert.False(Directory.Exists(Path.Combine(_workspace.Path, "memory-repo")));
    }

    private static JsonObject AdoptionTarget(string body) => new()
    {
        ["number"] = 77,
        ["html_url"] = "https://github.com/microsoft/aspire/issues/77",
        ["state"] = "open",
        ["locked"] = false,
        ["body"] = body,
        ["labels"] = new JsonArray(new JsonObject { ["name"] = "area-testing" }),
    };
}
