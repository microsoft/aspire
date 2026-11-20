// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.TestUtilities;
using VerifyXunit;
using Xunit;

namespace Infrastructure.Tests;

/// <summary>Exercises fresh destination checks, durable occurrences and recovery from partial publication.</summary>
public sealed partial class AnalyzeCiFailureWorkflowTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MainLabelUpsertUsesCreateOrUpdateWithoutReplacingIssueLabels(bool existing)
    {
        if (existing)
        {
            await File.WriteAllTextAsync(Path.Combine(_workspace.Path, "existing-main-label"), "");
        }

        await MainPublicationRefreshesMetadataAndPreservesNotesAcrossMigration("valid");
        var requests = await ReadHttpRequestsAsync();
        var upsert = Assert.Single(
            requests,
            request =>
            request!["method"]!.GetValue<string>() != "GET" && request["path"]!.GetValue<string>().StartsWith("/repos/microsoft/aspire/labels", StringComparison.Ordinal))!;
        Assert.Equal(existing ? "PATCH" : "POST", upsert["method"]!.GetValue<string>());
        Assert.Equal(
            existing ? "/repos/microsoft/aspire/labels/main-ci-break" : "/repos/microsoft/aspire/labels",
            upsert["path"]!.GetValue<string>());
        Assert.Equal("main-ci-break", upsert["body"]!["name"]!.GetValue<string>());
        Assert.Equal("b60205", upsert["body"]!["color"]!.GetValue<string>());
        Assert.All(
            requests.Where(request => request!["method"]!.GetValue<string>() == "PATCH" &&
                request["path"]!.GetValue<string>().StartsWith("/repos/microsoft/aspire/issues/", StringComparison.Ordinal)),
            request => Assert.Null(request!["body"]!["labels"]));
    }

    private CommandResult? _publicationHistoryBefore;
    [Fact]
    [RequiresTools(["bash", "jq", "git"])]
    public async Task MalformedIssueInventoryCannotAuthorizePublication()
    {
        await WriteGroupedFixtureAsync();
        await File.WriteAllTextAsync(Path.Combine(_workspace.Path, "issue.json"), "[null,5]");

        var result = await RunSuccessfulGroupedPublicationAsync();

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("expected an object", result.Output, StringComparison.Ordinal);
        var calls = await File.ReadAllLinesAsync(Path.Combine(_workspace.Path, "gh-calls.log"));
        Assert.All(calls, call => Assert.StartsWith("api ", call, StringComparison.Ordinal));
        Assert.False(File.Exists(Path.Combine(_workspace.Path, "memory-repo", "runs", "123.json")));
        var branch = await RunProcessAsync(
            "git",
            ["--no-pager", "--git-dir", "remote.git",
            "show-ref", "--verify", "--quiet", "refs/heads/memory/ci-failure-analysis"]);
        Assert.Equal(1, branch.ExitCode);
    }

    [Theory]
    [InlineData("invalid/repository/name", "2026-10-26T12:00:00Z", "Invalid publication repository")]
    [InlineData("microsoft/aspire", "not-a-date", "Invalid observation timestamp")]
    public void PublisherRejectsInvalidArgumentsDuringConstruction(string repository, string observedAt, string message)
    {
        var memoryDirectory = Path.Combine(_workspace.Path, "memory-repo");

        var error = Assert.Throws<AnalyzeCiFailure.ValidationException>(() =>
            new AnalyzeCiFailure.AnalysisPublisher(
                _workspace.Path,
                Path.Combine(_workspace.Path, "analysis"),
                memoryDirectory,
                repository,
                observedAt,
                new AnalyzeCiFailure.AnalysisDiagnostics(),
                CreateGitHubClient()));

        Assert.Equal(message, error.Message);
        Assert.False(Directory.Exists(memoryDirectory));
    }

    private const string ExistingGroupedIssue = """
        <!-- ci-failure-cause:debugger-timeout -->
        <!-- ci-failure-cause-type:flaky-test -->
        Operator notes: keep this.
        <!-- ci-failure-occurrences:start -->
        ## Occurrences

        | Date | Build | Job | Context |
        |------|-------|-----|----|
        <!-- ci-failure-occurrences:end -->
        """;

    [Theory]
    [InlineData("valid")]
    [InlineData("wrong-job")]
    [InlineData("wrong-test-in-flaky-job")]
    [InlineData("uncovered-flaky-observation")]
    [InlineData("valid-shared-cause")]
    [InlineData("legacy-valid")]
    [InlineData("legacy-wrong-job")]
    [InlineData("legacy-missing-numeric-identity")]
    [RequiresTools(["bash", "jq"])]
    public async Task CauseReferencesBindNumericJobsRatherThanSharedDisplayNames(string scenario)
    {
        var wrongJob = scenario is "wrong-job" or "wrong-test-in-flaky-job" or "legacy-wrong-job";
        await WriteValidationFixtureAsync(
            """{"run_id":123,"run_scope":"pull-request","verdict":"mixed","pr":{"number":42},"failed_jobs":[{"id":123,"classification":"code-issue"},{"id":456,"classification":"code-issue"}],"failed_tests":[{"name":"Tests.Flaky","job":"Same job name","job_id":123,"error":"Intermittent timeout","classification":"flaky","reason":"Intermittent"},{"name":"Tests.Flaky","job":"Same job name","job_id":456,"error":"Deterministic assertion","classification":"code-issue","reason":"Deterministic"}],"causes":["flaky-failure"]}""",
            """{"run_id":123,"run_scope":"pull-request","pr_numbers":"42"}""",
            """[{"id":123,"name":"Same job name"},{"id":456,"name":"Same job name"}]""",
            "flaky-failure.json",
            CreateCause("flaky-failure", "flaky-test", wrongJob ? 456 : 123));
        var directory = Path.Combine(_workspace.Path, "ci-analysis-output");
        var path = Path.Combine(directory, "analysis-result.json");
        var analysis = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        var tests = analysis["failed_tests"]!.AsArray();
        if (scenario is "uncovered-flaky-observation" or "valid-shared-cause")
        {
            tests[1]!["classification"] = "flaky";
        }
        if (scenario.StartsWith("legacy-", StringComparison.Ordinal))
        {
            tests[1]!["name"] = "Tests.Other";
        }
        if (scenario == "wrong-test-in-flaky-job")
        {
            tests.Add(new JsonObject
            {
                ["name"] = "Tests.Other",
                ["job"] = "Same job name",
                ["job_id"] = 456,
                ["error"] = "Another timeout",
                ["classification"] = "flaky",
                ["reason"] = "Intermittent",
            });
            analysis["causes"]!.AsArray().Add("other-failure");
            var other = JsonNode.Parse(CreateCause("other-failure", "flaky-test", jobId: 456))!;
            other["test_name"] = "Tests.Other";
            await File.WriteAllTextAsync(Path.Combine(directory, "causes", "other-failure.json"), other.ToJsonString());
        }
        var collected = new JsonArray(tests.Select(test => (JsonNode)new JsonObject
        {
            ["test"] = test!["name"]!.DeepClone(),
            ["job"] = test["job"]!.DeepClone(),
            ["job_id"] = scenario == "legacy-missing-numeric-identity" ? null : test["job_id"]!.DeepClone(),
            ["error"] = test["error"]!.DeepClone(),
        }).ToArray());
        await File.WriteAllTextAsync(Path.Combine(EvidenceDirectory, "test-failures.json"), collected.ToJsonString());
        if (scenario.StartsWith("legacy-", StringComparison.Ordinal))
        {
            foreach (var test in tests)
            {
                test!.AsObject().Remove("job_id");
            }
        }
        await File.WriteAllTextAsync(path, analysis.ToJsonString());
        if (scenario == "valid-shared-cause")
        {
            await File.WriteAllTextAsync(
                Path.Combine(directory, "causes", "flaky-failure.json"),
                CreateCause("flaky-failure", "flaky-test", jobId: 123, 456));
        }

        var result = await RunValidationScriptAsync("");

        Assert.Equal(scenario is "valid" or "valid-shared-cause" or "legacy-valid" ? 0 : 1, result.ExitCode);
        if (wrongJob)
        {
            Assert.Contains(
                scenario == "wrong-test-in-flaky-job" ? "Cause test observation must reference a validated flaky test in its trusted job" :
                "references an unknown or incompatible failed job",
                result.Output,
                StringComparison.Ordinal);
        }
        else if (scenario == "uncovered-flaky-observation")
        {
            Assert.Contains("Every flaky test and job observation must be covered", result.Output, StringComparison.Ordinal);
        }
        else if (scenario == "legacy-missing-numeric-identity")
        {
            Assert.Contains("a collected job", result.Output, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("duplicate", false)]
    [InlineData("duplicate", true)]
    [InlineData("locked", false)]
    [InlineData("locked", true)]
    [InlineData("lookup-failure", false)]
    [InlineData("lookup-failure", true)]
    [InlineData("mismatched-detail", false)]
    [InlineData("mismatched-detail", true)]
    [InlineData("invalid-detail", false)]
    [InlineData("invalid-detail", true)]
    [RequiresTools(["bash", "jq", "git"])]
    public async Task PublicationRejectsInvalidFreshIssueDestinationBeforeAnyHistoryPush(string scenario, bool storedLink)
    {
        await WriteGroupedFixtureAsync();
        var issue = new JsonObject
        {
            ["number"] = 77,
            ["state"] = scenario == "duplicate" ? "closed" : "open",
            ["state_reason"] = scenario == "duplicate" ? "duplicate" : null,
            ["locked"] = scenario == "locked",
            ["body"] = ExistingGroupedIssue,
            ["labels"] = new JsonArray(new JsonObject { ["name"] = "ci-failure-cause" }),
        };
        if (scenario is "mismatched-detail" or "invalid-detail")
        {
            issue["number"] = scenario == "mismatched-detail" ? JsonValue.Create(value: 78) : null;
        }
        await File.WriteAllTextAsync(Path.Combine(_workspace.Path, "issue-details.json"), issue.ToJsonString());
        if (scenario == "lookup-failure")
        {
            await File.WriteAllTextAsync(Path.Combine(_workspace.Path, "issue-lookup-failure"), "");
        }
        JsonObject? history = storedLink
            ? JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(_workspace.Path, "ci-analysis-output", "causes", "debugger-timeout.json")))!.AsObject()
            : null;
        if (history is not null)
        {
            history["issue_url"] = "https://github.com/microsoft/aspire/issues/77";
            history["occurrences"] = new JsonArray();
        }

        var result = await RunSuccessfulGroupedPublicationAsync(history, ExistingGroupedIssue);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(
            scenario switch
            {
                "duplicate" => "closed as a duplicate",
                "locked" => "locked",
                "lookup-failure" => "GET /repos/microsoft/aspire/issues/77: HTTP 500.",
                _ => storedLink ? "Linked cause issue detail does not match" : "detail lookup returned invalid results",
            },
            result.Output,
            StringComparison.Ordinal);
        var calls = await File.ReadAllLinesAsync(Path.Combine(_workspace.Path, "gh-calls.log"));
        Assert.All(calls, call => Assert.StartsWith("api ", call, StringComparison.Ordinal));
        Assert.Contains("api repos/microsoft/aspire/issues/77", calls);
        var remote = await RunProcessAsync(
            "git",
            ["--no-pager", "--git-dir", "remote.git",
            "show-ref", "--verify", "refs/heads/memory/ci-failure-analysis"]);
        Assert.Equal(_publicationHistoryBefore, remote);
        Assert.False(File.Exists(Path.Combine(_workspace.Path, "memory-repo", "runs", "123.json")));
    }

    [Theory]
    [InlineData("new")]
    [InlineData("existing")]
    [InlineData("closed")]
    [InlineData("code-issue")]
    [RequiresTools(["bash", "jq", "git"])]
    public async Task PublishCreatesOrUpdatesBotCommentOnlyWhilePullRequestRemainsActionable(string scenario)
    {
        await WriteGroupedFixtureAsync();
        if (scenario == "existing")
        {
            await File.WriteAllTextAsync(
                Path.Combine(_workspace.Path, "comments.json"),
                """[[{"id":99,"body":"<!-- analyze-ci-failure -->\nPrior analysis","user":{"login":"github-actions[bot]"}},{"id":1,"body":"<!-- analyze-ci-failure -->\nHuman note","user":{"login":"maintainer"}}]]""");
        }
        if (scenario == "closed")
        {
            await File.WriteAllTextAsync(Path.Combine(_workspace.Path, "close-after-lookup"), "");
        }
        if (scenario == "code-issue")
        {
            var summaryPath = Path.Combine(_workspace.Path, "ci-analysis-output", "analysis-result.json");
            var summary = JsonNode.Parse(await File.ReadAllTextAsync(summaryPath))!;
            summary["verdict"] = "code-issue";
            summary["causes"] = new JsonArray();
            summary["failed_jobs"]![0]!["classification"] = "code-issue";
            foreach (var test in summary["failed_tests"]!.AsArray())
            {
                test!["classification"] = "code-issue";
            }
            await File.WriteAllTextAsync(summaryPath, summary.ToJsonString());
            File.Delete(Path.Combine(_workspace.Path, "ci-analysis-output", "causes", "debugger-timeout.json"));
        }

        var result = await RunSuccessfulGroupedPublicationAsync();

        Assert.True(result.ExitCode == 0, result.Output);
        var calls = await File.ReadAllLinesAsync(Path.Combine(_workspace.Path, "gh-calls.log"));
        Assert.Equal(2, calls.Count(call => call == "api repos/microsoft/aspire/pulls/42"));
        if (scenario == "closed")
        {
            Assert.Equal(
                ["api repos/microsoft/aspire/pulls/42",
                "api --method GET --paginate --slurp repos/microsoft/aspire/issues/42/comments?per_page=100",
                "api repos/microsoft/aspire/pulls/42"],
                calls.Where(call => call.Contains("pulls/42", StringComparison.Ordinal) ||
                    call.Contains("comments", StringComparison.Ordinal) || call.StartsWith("pr comment ", StringComparison.Ordinal)));
        }
        else if (scenario == "existing")
        {
            Assert.Contains(calls, call => call.StartsWith("api --method PATCH repos/microsoft/aspire/issues/comments/99 --input ", StringComparison.Ordinal));
            var request = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(_workspace.Path, "published-comment-request.json")))!;
            Assert.StartsWith("<!-- analyze-ci-failure -->\n", request["body"]!.GetValue<string>(), StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains(calls, call => call.StartsWith("pr comment 42 --repo microsoft/aspire --body-file ", StringComparison.Ordinal));
            Assert.StartsWith(
                "<!-- analyze-ci-failure -->\n",
                await File.ReadAllTextAsync(Path.Combine(_workspace.Path, "pr-comment.md")),
                StringComparison.Ordinal);
        }
        if (scenario == "code-issue")
        {
            Assert.Equal(
                ["api repos/microsoft/aspire/pulls/42",
                "api --method GET --paginate --slurp repos/microsoft/aspire/issues/42/comments?per_page=100",
                "api repos/microsoft/aspire/pulls/42"],
                calls.Take(3));
            Assert.Equal(4, calls.Length);
            Assert.StartsWith("pr comment 42 --repo microsoft/aspire --body-file ", calls[3], StringComparison.Ordinal);
            var posted = Assert.Single(
                await ReadHttpRequestsAsync(),
                request =>
                request!["method"]!.GetValue<string>() == "POST" && request["path"]!.GetValue<string>() == "/repos/microsoft/aspire/issues/42/comments")!;
            Assert.Equal(
                await File.ReadAllTextAsync(Path.Combine(_workspace.Path, "pr-comment.md")),
                posted["body"]!["body"]!.GetValue<string>());
            var branch = await RunProcessAsync(
                "git",
                ["--no-pager", "--git-dir", "remote.git",
                "show-ref", "--verify", "--quiet", "refs/heads/memory/ci-failure-analysis"]);
            Assert.Equal(1, branch.ExitCode);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [RequiresTools(["bash", "jq", "git"])]
    public async Task GroupedPublicationReopensAndLinksClosedIssueWhenBodyProjectionIsUnsupported(bool oversized)
    {
        await WriteGroupedFixtureAsync();
        var body = ExistingGroupedIssue.Replace(
            "Operator notes: keep this.",
            oversized
            ? "Operator notes: keep this." + new string('x', count: 65000)
            : "Operator notes: keep this.\n<!-- ci-failure-tests:start -->",
            StringComparison.Ordinal);

        var result = await RunSuccessfulGroupedPublicationAsync(body: body, state: "closed");

        Assert.True(result.ExitCode == 0, result.Output);
        var calls = await File.ReadAllTextAsync(Path.Combine(_workspace.Path, "gh-calls.log"));
        Assert.Contains("issue reopen 77 --repo microsoft/aspire", calls, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_workspace.Path, "issue-body.md")));
        var remote = await RunProcessAsync(
            "git",
            ["--no-pager", "--git-dir", "remote.git",
            "show", "refs/heads/memory/ci-failure-analysis:causes/debugger-timeout.json"]);
        Assert.Equal("https://github.com/microsoft/aspire/issues/77", JsonNode.Parse(remote.Output)!["issue_url"]!.GetValue<string>());
        var commits = await RunProcessAsync(
            "git",
            ["--no-pager", "--git-dir", "remote.git",
            "rev-list", "--count", "refs/heads/memory/ci-failure-analysis"]);
        Assert.Equal("2", commits.Output.Trim());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [RequiresTools(["bash", "jq", "git"])]
    public async Task GroupedPublicationUsesFreshIssueBodyAndStateAfterInventoryLookup(bool storedLink)
    {
        await WriteGroupedFixtureAsync();
        await File.WriteAllTextAsync(
            Path.Combine(_workspace.Path, "issue-details.json"),
            new JsonObject
            {
                ["number"] = 77,
                ["state"] = "closed",
                ["body"] = ExistingGroupedIssue.Replace("keep this.", "new operator note.", StringComparison.Ordinal),
                ["labels"] = new JsonArray(new JsonObject { ["name"] = "ci-failure-cause" }),
            }.ToJsonString());

        var history = storedLink ? JsonNode.Parse("""
            {"id":"debugger-timeout","type":"flaky-test","title":"Debugger timeout","error_pattern":"Connection timed out","issue_url":"https://github.com/microsoft/aspire/issues/77","occurrences":[]}
            """)!.AsObject() : null;
        var result = await RunSuccessfulGroupedPublicationAsync(history, ExistingGroupedIssue);

        Assert.True(result.ExitCode == 0, result.Output);
        Assert.Contains("new operator note.", await File.ReadAllTextAsync(Path.Combine(_workspace.Path, "issue-body.md")), StringComparison.Ordinal);
        var calls = await File.ReadAllTextAsync(Path.Combine(_workspace.Path, "gh-calls.log"));
        Assert.Contains("api repos/microsoft/aspire/issues/77", calls, StringComparison.Ordinal);
        Assert.Contains("issue reopen 77 --repo microsoft/aspire", calls, StringComparison.Ordinal);
        Assert.Contains("issue edit 77 --repo microsoft/aspire --body-file", calls, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("unsupported")]
    [InlineData("oversized")]
    [InlineData("custom-history")]
    [RequiresTools(["bash", "jq", "git"])]
    public async Task MainPublicationRefreshesMetadataAndPreservesNotesAcrossMigration(string shape)
    {
        await WriteGroupedFixtureAsync();
        var data = EvidenceDirectory;
        await File.WriteAllTextAsync(
            Path.Combine(data, "run-context.json"),
            """{"run_id":123,"run_scope":"main","pr_numbers":"","head_sha":"failed-sha"}""");
        await File.WriteAllTextAsync(Path.Combine(data, "test-failures.json"), "[]");
        await File.WriteAllTextAsync(Path.Combine(data, "diagnostics/last-successful-main-run.json"), """{"id":100,"head_sha":"last-successful-sha","created_at":""}""");
        var analysis = Path.Combine(_workspace.Path, "ci-analysis-output");
        var summaryPath = Path.Combine(analysis, "analysis-result.json");
        var summary = JsonNode.Parse(await File.ReadAllTextAsync(summaryPath))!;
        summary["run_scope"] = "main";
        summary["verdict"] = "main-repository-breakage";
        summary["pr"] = null;
        summary["failed_tests"] = new JsonArray();
        summary["failed_jobs"]![0]!["classification"] = "main-repository-breakage";
        await File.WriteAllTextAsync(summaryPath, summary.ToJsonString());
        var causePath = Path.Combine(analysis, "causes", "debugger-timeout.json");
        var cause = JsonNode.Parse(await File.ReadAllTextAsync(causePath))!;
        cause["type"] = "main-repository-breakage";
        cause.AsObject().Remove("tests");
        await File.WriteAllTextAsync(causePath, cause.ToJsonString());
        var body = ExistingGroupedIssue.Replace("cause-type:flaky-test", "cause-type:main-repository-breakage", StringComparison.Ordinal);
        if (shape != "unsupported")
        {
            body = body.Replace("Operator notes:", "**Type**: main-repository-breakage\n\nOperator notes:", StringComparison.Ordinal);
        }
        if (shape == "oversized")
        {
            body = body.Replace("Operator notes: keep this.", "Operator notes: keep this." + new string('x', count: 65000), StringComparison.Ordinal);
        }
        if (shape == "custom-history")
        {
            body = body.Replace("**Type**:", "Old generated attribution: #999 caused this failure.\n\n**Type**:", StringComparison.Ordinal)
                .Replace(
                    "<!-- ci-failure-occurrences:end -->",
                    "| historical custom row: preserve this | notes |\n<!-- ci-failure-occurrences:end -->",
                    StringComparison.Ordinal);
        }

        var result = await RunSuccessfulGroupedPublicationAsync(body: body, state: "closed");

        Assert.True(result.ExitCode == 0, result.Output);
        if (shape == "unsupported")
        {
            Assert.Contains("Unable to migrate main issue body", result.Output, StringComparison.Ordinal);
            Assert.Contains("ambiguous main issue type", result.Output, StringComparison.Ordinal);
        }
        var calls = await File.ReadAllTextAsync(Path.Combine(_workspace.Path, "gh-calls.log"));
        Assert.Contains("label create main-ci-break --repo microsoft/aspire --color b60205 --description Deterministic repository breakage on the main branch --force", calls, StringComparison.Ordinal);
        var requests = await ReadHttpRequestsAsync();
        var edit = Assert.Single(
            requests,
            request =>
            request!["method"]!.GetValue<string>() == "PATCH" && request["path"]!.GetValue<string>() == "/repos/microsoft/aspire/issues/77" &&
            request["body"]!["title"] is not null)!;
        Assert.Equal("[Main CI Failure] Main branch CI failure at failed-sha", edit["body"]!["title"]!.GetValue<string>());
        Assert.Null(edit["body"]!["labels"]);
        var addedLabels = Assert.Single(
            requests,
            request =>
            request!["method"]!.GetValue<string>() == "POST" && request["path"]!.GetValue<string>() == "/repos/microsoft/aspire/issues/77/labels")!;
        Assert.Equal(
            ["ci-failure-cause", "main-ci-break"],
            addedLabels["body"]!["labels"]!.AsArray().Select(label => label!.GetValue<string>()));
        Assert.Contains("issue reopen 77 --repo microsoft/aspire", calls, StringComparison.Ordinal);
        var bodyPath = Path.Combine(_workspace.Path, "issue-body.md");
        var updated = shape == "oversized" ? body : await File.ReadAllTextAsync(bodyPath);
        Assert.Contains("Operator notes: keep this.", updated, StringComparison.Ordinal);
        if (shape is "valid" or "custom-history")
        {
            Assert.Contains("Failed main SHA: `failed-sha`", updated, StringComparison.Ordinal);
            Assert.Contains("Last successful main SHA: `last-successful-sha`", updated, StringComparison.Ordinal);
        }
        if (shape == "custom-history")
        {
            Assert.Contains("| historical custom row: preserve this | notes |", updated, StringComparison.Ordinal);
            Assert.Equal(-1, updated.IndexOf("Old generated attribution:", StringComparison.Ordinal));
        }
        if (shape == "oversized")
        {
            Assert.False(File.Exists(bodyPath));
        }
    }

    [Fact]
    [RequiresTools(["bash", "jq", "git"])]
    public async Task GroupedPublicationRendersEachProducingJobAsItsOwnCodeSpan()
    {
        await WriteGroupedFixtureAsync();
        var data = EvidenceDirectory;
        await File.WriteAllTextAsync(
            Path.Combine(data, "diagnostics/failed-jobs.json"),
            """[{"id":123,"name":"Tests"},{"id":124,"name":"Other | Linux"}]""");
        var trustedPath = Path.Combine(data, "test-failures.json");
        var collected = JsonNode.Parse(await File.ReadAllTextAsync(trustedPath))!;
        collected[1]!["job_id"] = 124;
        collected[1]!["job"] = "Other | Linux";
        await File.WriteAllTextAsync(trustedPath, collected.ToJsonString());
        var analysis = Path.Combine(_workspace.Path, "ci-analysis-output");
        var summaryPath = Path.Combine(analysis, "analysis-result.json");
        var summary = JsonNode.Parse(await File.ReadAllTextAsync(summaryPath))!;
        summary["failed_jobs"]!.AsArray().Add(new JsonObject { ["id"] = 124, ["classification"] = "flaky-test" });
        summary["failed_tests"]![1]!["job_id"] = 124;
        summary["failed_tests"]![1]!["job"] = "Other | Linux";
        await File.WriteAllTextAsync(summaryPath, summary.ToJsonString());
        var causePath = Path.Combine(analysis, "causes", "debugger-timeout.json");
        var cause = JsonNode.Parse(await File.ReadAllTextAsync(causePath))!;
        cause["job_ids"]!.AsArray().Add(124);
        cause["tests"]![1]!["job_id"] = 124;
        await File.WriteAllTextAsync(causePath, cause.ToJsonString());

        var result = await RunSuccessfulGroupedPublicationAsync();

        Assert.True(result.ExitCode == 0, result.Output);
        var body = await File.ReadAllTextAsync(Path.Combine(_workspace.Path, "issue-body.md"));
        Assert.Contains("| ` Tests `<br>` Other \\| Linux ` |", body, StringComparison.Ordinal);
    }

    [Fact]
    [RequiresTools(["bash", "jq"])]
    public async Task GroupedTruncatedModernNameDoesNotMatchFullTrustedIdentity()
    {
        await WriteGroupedFixtureAsync();
        var path = Path.Combine(EvidenceDirectory, "test-failures.json");
        var collected = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        var name = "Tests.Method(value: 1/2)" + new string('x', count: 520);
        collected[0]!["test"] = name;
        await File.WriteAllTextAsync(path, collected.ToJsonString());
        var summaryPath = Path.Combine(_workspace.Path, "ci-analysis-output", "analysis-result.json");
        var summary = JsonNode.Parse(await File.ReadAllTextAsync(summaryPath))!;
        summary["failed_tests"]![0]!["name"] = name[..500];
        await File.WriteAllTextAsync(summaryPath, summary.ToJsonString());
        var causePath = Path.Combine(_workspace.Path, "ci-analysis-output", "causes", "debugger-timeout.json");
        var cause = JsonNode.Parse(await File.ReadAllTextAsync(causePath))!;
        cause["tests"]![0]!["name"] = name[..500];
        await File.WriteAllTextAsync(causePath, cause.ToJsonString());

        var result = await RunValidationScriptAsync("");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("do not match trusted test failure evidence", result.Output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [RequiresTools(["bash", "jq", "git"])]
    public async Task GroupedLongRawIdentityValidatesAndPublishesBoundedDisplay(bool control)
    {
        await WriteGroupedFixtureAsync();
        var name = control ? "Tests.Method(value: \u202eone/two)" :
            "Tests.Method(value: " + new string('x', count: 520) + ", token: \"fake-value\")";
        var dataPath = Path.Combine(EvidenceDirectory, "test-failures.json");
        var collected = JsonNode.Parse(await File.ReadAllTextAsync(dataPath))!;
        collected[0]!["test"] = name;
        await File.WriteAllTextAsync(dataPath, collected.ToJsonString());
        var summaryPath = Path.Combine(_workspace.Path, "ci-analysis-output", "analysis-result.json");
        var summary = JsonNode.Parse(await File.ReadAllTextAsync(summaryPath))!;
        summary["failed_tests"]![0]!["name"] = name;
        await File.WriteAllTextAsync(summaryPath, summary.ToJsonString());
        var causePath = Path.Combine(_workspace.Path, "ci-analysis-output", "causes", "debugger-timeout.json");
        var cause = JsonNode.Parse(await File.ReadAllTextAsync(causePath))!;
        cause["tests"]![0]!["name"] = name;
        await File.WriteAllTextAsync(causePath, cause.ToJsonString());

        var validated = await RunValidationScriptAsync("");
        Assert.True(validated.ExitCode == 0, validated.Output);
        var published = await RunSuccessfulGroupedPublicationAsync();
        Assert.True(published.ExitCode == 0, published.Output);
        var history = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(_workspace.Path, "memory-repo", "causes", "debugger-timeout.json")))!;
        Assert.Equal(
            new[] { control ? "Tests.Method(value: one/two)" : name[..500], "Tests.Other" },
            history["occurrences"]![0]!["tests"]!.AsArray().Select(test => test!["name"]!.GetValue<string>()));
        summary = AnalyzeCiFailure.CiFailureJson.ToNode(AnalyzeCiFailureFixtures.Validate(_workspace.Path).Report)!;
        Assert.Equal(name, summary["failed_tests"]![0]!["name"]!.GetValue<string>());
        Assert.InRange(System.Text.Encoding.UTF8.GetByteCount(await File.ReadAllTextAsync(Path.Combine(_workspace.Path, "issue-body.md"))), 1, 65000);
    }

    [Theory]
    [InlineData(130)]
    [InlineData(400)]
    [InlineData(800)]
    [RequiresTools(["bash", "jq", "git"])]
    public async Task GroupedPublicationBoundsHistoricalObservedTestsAndKeepsLatestOccurrenceAndNotes(int count)
    {
        await WriteGroupedFixtureAsync();
        var observations = new JsonArray(Enumerable.Range(start: 1, count).Select(index => (JsonNode)new JsonObject
        {
            ["run_id"] = index + 1000,
            ["run_url"] = $"https://github.com/microsoft/aspire/actions/runs/{index + 1000}",
            ["observed_at"] = $"2026-09-{index % 28 + 1:D2}T00:00:00Z",
            ["job"] = "Tests",
            ["pr_number"] = 42,
            ["tests"] = new JsonArray(new JsonObject
            {
                ["name"] = "Tests.Method(value: " + new string('x', count: 470) + ")",
                ["job"] = "Tests",
                ["job_id"] = index + 2000,
            }),
        }).ToArray());
        var history = new JsonObject
        {
            ["id"] = "debugger-timeout",
            ["type"] = "flaky-test",
            ["title"] = "Debugger timeout",
            ["error_pattern"] = "Connection timed out",
            ["occurrences"] = observations,
        };
        const string body = """
            <!-- ci-failure-cause:debugger-timeout -->
            <!-- ci-failure-cause-type:flaky-test -->
            Operator notes: keep this.
            <!-- ci-failure-occurrences:start -->
            ## Occurrences

            | Date | Build | Job | Context |
            |------|-------|-----|----|
            <!-- ci-failure-occurrences:end -->
            """;

        var published = await RunSuccessfulGroupedPublicationAsync(history, body);

        Assert.True(published.ExitCode == 0, published.Output);
        var updated = await File.ReadAllTextAsync(Path.Combine(_workspace.Path, "issue-body.md"));
        Assert.Contains("Operator notes: keep this.", updated, StringComparison.Ordinal);
        Assert.Contains("[123](https://github.com/microsoft/aspire/actions/runs/123)", updated, StringComparison.Ordinal);
        Assert.Contains("Tests.Method(value: 1/2)", updated, StringComparison.Ordinal);
        Assert.InRange(System.Text.Encoding.UTF8.GetByteCount(updated), 1, 65000);
        history = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(_workspace.Path, "memory-repo", "causes", "debugger-timeout.json")))!.AsObject();
        Assert.Equal(count + 1, history["occurrences"]!.AsArray().Count);
    }

    [Fact]
    [RequiresTools(["bash", "jq", "git"])]
    public async Task ReplayedPublicationNeverTrimsOperatorTextInsideUnsupportedOccurrenceSection()
    {
        await WriteGroupedFixtureAsync();
        var body = ExistingGroupedIssue.Replace(
            "<!-- ci-failure-occurrences:end -->",
            """
            | 2026-10-20 | [122](https://github.com/microsoft/aspire/actions/runs/122) | ` Tests ` | #42 |
            | 2026-10-21 | [123](https://github.com/microsoft/aspire/actions/runs/123) | ` Tests ` | #42 |
            Protected operator content inside occurrence section:
            PADDING
            <!-- ci-failure-occurrences:end -->
            """,
            StringComparison.Ordinal);
        body = body.Replace("## Occurrences\n\n", "## Occurrences\n\nShowing 2 most recent of 2 occurrences.\n\n", StringComparison.Ordinal);
        body = body.Replace("PADDING", new string('x', 65287 - System.Text.Encoding.UTF8.GetByteCount(body) + "PADDING".Length), StringComparison.Ordinal);
        Assert.Equal(65287, System.Text.Encoding.UTF8.GetByteCount(body));

        var result = await RunSuccessfulGroupedPublicationAsync(body: body, state: "closed");

        Assert.True(result.ExitCode == 0, result.Output);
        Assert.False(File.Exists(Path.Combine(_workspace.Path, "issue-body.md")));
        var calls = await File.ReadAllTextAsync(Path.Combine(_workspace.Path, "gh-calls.log"));
        Assert.Contains("issue reopen 77 --repo microsoft/aspire", calls, StringComparison.Ordinal);
        var remote = await RunProcessAsync(
            "git",
            ["--no-pager", "--git-dir", "remote.git",
            "show", "refs/heads/memory/ci-failure-analysis:causes/debugger-timeout.json"]);
        Assert.Equal("https://github.com/microsoft/aspire/issues/77", JsonNode.Parse(remote.Output)!["issue_url"]!.GetValue<string>());
        var original = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(_workspace.Path, "issue.json")))!;
        Assert.Equal(body, original[0]!["body"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [RequiresTools(["bash", "jq"])]
    public async Task ValidatorRebindsEachMixedModernAndLegacyObservationToItsTrustedDiagnostics(bool modernNeedsRedaction)
    {
        await WriteGroupedFixtureAsync();
        var modern = modernNeedsRedaction ? "Tests.Modern(token: \"modern-secret\")" : "Tests.Modern(value: a/b)";
        const string legacy = "Tests.Legacy(token: \"legacy-secret\")";
        var data = Path.Combine(EvidenceDirectory, "test-failures.json");
        var collected = JsonNode.Parse(await File.ReadAllTextAsync(data))!;
        collected[0]!["test"] = modern;
        collected[0]!["error"] = "Trusted modern diagnostic";
        collected[1]!["test"] = legacy;
        collected[1]!["error"] = "Trusted legacy diagnostic";
        await File.WriteAllTextAsync(data, collected.ToJsonString());
        var analysis = Path.Combine(_workspace.Path, "ci-analysis-output");
        var summaryPath = Path.Combine(analysis, "analysis-result.json");
        var summary = JsonNode.Parse(await File.ReadAllTextAsync(summaryPath))!;
        summary["failed_tests"]![0]!["name"] = modern;
        summary["failed_tests"]![1]!["name"] = legacy;
        summary["failed_tests"]![1]!.AsObject().Remove("job_id");
        summary["causes"]!.AsArray().Add("legacy-timeout");
        await File.WriteAllTextAsync(summaryPath, summary.ToJsonString());
        var causePath = Path.Combine(analysis, "causes", "debugger-timeout.json");
        var cause = JsonNode.Parse(await File.ReadAllTextAsync(causePath))!;
        cause["tests"]![0]!["name"] = modern;
        cause["tests"]!.AsArray().RemoveAt(index: 1);
        await File.WriteAllTextAsync(causePath, cause.ToJsonString());
        cause["id"] = "legacy-timeout";
        cause.AsObject().Remove("tests");
        cause["test_name"] = legacy;
        await File.WriteAllTextAsync(Path.Combine(analysis, "causes", "legacy-timeout.json"), cause.ToJsonString());

        var result = await RunValidationScriptAsync("");

        Assert.True(result.ExitCode == 0, result.Output);
        summary = AnalyzeCiFailure.CiFailureJson.ToNode(AnalyzeCiFailureFixtures.Validate(_workspace.Path).Report)!;
        Assert.Equal(modern, summary["failed_tests"]![0]!["name"]!.GetValue<string>());
        Assert.Equal(legacy, summary["failed_tests"]![1]!["name"]!.GetValue<string>());
        Assert.Equal("Tests.Legacy(token:[REDACTED]", summary["failed_tests"]![1]!["display_name"]!.GetValue<string>());
        Assert.Equal("Trusted modern diagnostic", summary["failed_tests"]![0]!["error"]!.GetValue<string>());
        Assert.Equal("Trusted legacy diagnostic", summary["failed_tests"]![1]!["error"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("occurrences:start", false, false)]
    [InlineData("tests:end", false, true)]
    [InlineData("occurrences:start", true, true)]
    [InlineData("tests:end", true, false)]
    [RequiresTools(["bash", "jq", "git"])]
    public async Task PublicationTreatsInlineManagedMarkersAsTestOrJobText(string marker, bool inJob, bool crlf)
    {
        await WriteGroupedFixtureAsync();
        var text = $"Name <!-- ci-failure-{marker} --> value";
        var data = EvidenceDirectory;
        var analysis = Path.Combine(_workspace.Path, "ci-analysis-output");
        var summaryPath = Path.Combine(analysis, "analysis-result.json");
        var causePath = Path.Combine(analysis, "causes", "debugger-timeout.json");
        var trustedPath = Path.Combine(data, "test-failures.json");
        var summary = JsonNode.Parse(await File.ReadAllTextAsync(summaryPath))!;
        var cause = JsonNode.Parse(await File.ReadAllTextAsync(causePath))!;
        var collected = JsonNode.Parse(await File.ReadAllTextAsync(trustedPath))!;
        if (inJob)
        {
            await File.WriteAllTextAsync(
                Path.Combine(data, "diagnostics/failed-jobs.json"),
                new JsonArray(new JsonObject { ["id"] = 123, ["name"] = text }).ToJsonString());
            foreach (var test in summary["failed_tests"]!.AsArray())
            {
                test!["job"] = text;
            }
            foreach (var test in collected.AsArray())
            {
                test!["job"] = text;
            }
        }
        else
        {
            summary["failed_tests"]![0]!["name"] = text;
            cause["tests"]![0]!["name"] = text;
            collected[0]!["test"] = text;
        }
        await File.WriteAllTextAsync(summaryPath, summary.ToJsonString());
        await File.WriteAllTextAsync(causePath, cause.ToJsonString());
        await File.WriteAllTextAsync(trustedPath, collected.ToJsonString());

        var first = await RunSuccessfulGroupedPublicationAsync();

        Assert.True(first.ExitCode == 0, first.Output);
        var body = await File.ReadAllTextAsync(Path.Combine(_workspace.Path, "issue-body.md"));
        if (crlf)
        {
            body = body.ReplaceLineEndings("\r\n");
        }
        await File.WriteAllTextAsync(
            Path.Combine(_workspace.Path, "issue.json"),
            new JsonArray(new JsonObject
            {
                ["number"] = 77,
                ["state"] = "open",
                ["body"] = body,
                ["labels"] = new JsonArray(new JsonObject { ["name"] = "ci-failure-cause" }),
            }).ToJsonString());
        foreach (var file in new[] { Path.Combine(data, "run-context.json"), Path.Combine(data, "diagnostics/run.json"), summaryPath })
        {
            var run = JsonNode.Parse(await File.ReadAllTextAsync(file))!;
            run[Path.GetFileName(file) == "run.json" ? "id" : "run_id"] = 124;
            if (Path.GetFileName(file) == "run.json")
            {
                run["html_url"] = "https://github.com/microsoft/aspire/actions/runs/124";
            }
            await File.WriteAllTextAsync(file, run.ToJsonString());
        }
        var fakeGh = Path.Combine(Assert.Single(Directory.GetDirectories(_workspace.Path, "fake-bin-*")), "gh");

        var second = await RunPublicationCommandAsync(fakeGh);

        Assert.True(second.ExitCode == 0, second.Output);
        var updated = await File.ReadAllTextAsync(Path.Combine(_workspace.Path, "issue-body.md"));
        Assert.Contains("[123](https://github.com/microsoft/aspire/actions/runs/123)", updated, StringComparison.Ordinal);
        Assert.Contains("[124](https://github.com/microsoft/aspire/actions/runs/124)", updated, StringComparison.Ordinal);
        Assert.Contains(text, updated, StringComparison.Ordinal);
        foreach (var managed in new[] { "occurrences:start", "occurrences:end", "tests:start", "tests:end" })
        {
            Assert.Equal(1, updated.Split('\n').Count(line => line.TrimEnd('\r') == $"<!-- ci-failure-{managed} -->"));
        }
    }

    [Theory]
    [InlineData("occurrences:start")]
    [InlineData("tests:end")]
    [RequiresTools(["bash", "jq", "git"])]
    public async Task PublicationKeepsGenuinelyAmbiguousWholeLineMarkersUnsupported(string marker)
    {
        await WriteGroupedFixtureAsync();
        var body = marker == "occurrences:start"
            ? ExistingGroupedIssue.Replace("## Occurrences\n", "<!-- ci-failure-occurrences:start -->\n## Occurrences\n", StringComparison.Ordinal)
            : ExistingGroupedIssue.Replace(
                "Operator notes: keep this.",
                "Operator notes: keep this.\n<!-- ci-failure-tests:start -->\nold tests\n<!-- ci-failure-tests:end -->\n<!-- ci-failure-tests:end -->",
                StringComparison.Ordinal);

        var result = await RunSuccessfulGroupedPublicationAsync(body: body, state: "closed");

        Assert.True(result.ExitCode == 0, result.Output);
        Assert.False(File.Exists(Path.Combine(_workspace.Path, "issue-body.md")));
        var calls = await File.ReadAllTextAsync(Path.Combine(_workspace.Path, "gh-calls.log"));
        Assert.Contains("issue reopen 77 --repo microsoft/aspire", calls, StringComparison.Ordinal);
        var original = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(_workspace.Path, "issue.json")))!;
        Assert.Equal(body, original[0]!["body"]!.GetValue<string>());
    }

    [Fact]
    [RequiresTools(["bash", "jq", "git"])]
    public async Task SameRunPublicationReplayRecognizesCrLfManagedSectionsWithoutAppendingOccurrence()
    {
        await WriteGroupedFixtureAsync();
        var first = await RunSuccessfulGroupedPublicationAsync();
        Assert.True(first.ExitCode == 0, first.Output);
        var bodyPath = Path.Combine(_workspace.Path, "issue-body.md");
        var body = (await File.ReadAllTextAsync(bodyPath))
            .Replace(
                "<!-- ci-failure-occurrences:start -->",
                "Operator notes: preserve CRLF replay.\n\n<!-- ci-failure-occurrences:start -->",
                StringComparison.Ordinal)
            .ReplaceLineEndings("\r\n");
        Assert.Contains("<!-- ci-failure-tests:start -->\r\n", body, StringComparison.Ordinal);
        await File.WriteAllTextAsync(
            Path.Combine(_workspace.Path, "issue.json"),
            new JsonArray(new JsonObject
            {
                ["number"] = 77,
                ["state"] = "open",
                ["body"] = body,
                ["labels"] = new JsonArray(new JsonObject { ["name"] = "ci-failure-cause" }),
            }).ToJsonString());
        File.Delete(bodyPath);
        var fakeGh = Path.Combine(Assert.Single(Directory.GetDirectories(_workspace.Path, "fake-bin-*")), "gh");

        var replay = await RunPublicationCommandAsync(fakeGh);

        Assert.True(replay.ExitCode == 0, replay.Output);
        var updated = await File.ReadAllTextAsync(bodyPath);
        foreach (var managed in new[] { "occurrences:start", "occurrences:end", "tests:start", "tests:end" })
        {
            Assert.Equal(1, updated.Split('\n').Count(line => line.TrimEnd('\r') == $"<!-- ci-failure-{managed} -->"));
        }
        Assert.Equal(1, updated.Split('\n').Count(line => line.TrimEnd('\r') == "## Observed tests"));
        Assert.EndsWith("<!-- ci-failure-occurrences:end -->", updated.TrimEnd(), StringComparison.Ordinal);
        Assert.Contains("Operator notes: preserve CRLF replay.", updated, StringComparison.Ordinal);
        var remote = await RunProcessAsync(
            "git",
            ["--no-pager", "--git-dir", "remote.git",
            "show", "refs/heads/memory/ci-failure-analysis:causes/debugger-timeout.json"]);
        Assert.Equal(0, remote.ExitCode);
        var history = JsonNode.Parse(remote.Output)!;
        Assert.Equal(123, Assert.Single(history["occurrences"]!.AsArray())!["run_id"]!.GetValue<int>());
    }

    private async Task<CommandResult> RunSuccessfulGroupedPublicationAsync(
        JsonObject? history = null,
        string? body = null,
        string state = "open",
        bool useTypedProposal = false)
    {
        await RunProcessAsync("git", ["--no-pager", "init", "--bare", "remote.git"]);
        await RunProcessAsync("git", ["--no-pager", "clone", "remote.git", "memory-repo"]);
        await RunProcessAsync("git", ["--no-pager", "-C", "memory-repo", "config", "user.name", "Test"]);
        await RunProcessAsync("git", ["--no-pager", "-C", "memory-repo", "config", "user.email", "test@example.invalid"]);
        if (history is not null)
        {
            var causes = Directory.CreateDirectory(Path.Combine(_workspace.Path, "memory-repo", "causes")).FullName;
            await File.WriteAllTextAsync(Path.Combine(causes, "debugger-timeout.json"), history.ToJsonString());
        }
        if (File.Exists(Path.Combine(_workspace.Path, "seed-unselected-malformed")))
        {
            var causes = Directory.CreateDirectory(Path.Combine(_workspace.Path, "memory-repo", "causes")).FullName;
            await File.WriteAllTextAsync(Path.Combine(causes, "unselected.json"), "not JSON");
        }
        if (body is not null)
        {
            await File.WriteAllTextAsync(
                Path.Combine(_workspace.Path, "issue.json"),
                new JsonArray(new JsonObject
                {
                    ["number"] = 77,
                    ["state"] = state,
                    ["body"] = body,
                    ["html_url"] = "https://github.com/microsoft/aspire/issues/77",
                    ["labels"] = new JsonArray(new JsonObject { ["name"] = "ci-failure-cause" }),
                }).ToJsonString());
        }
        var fakeGh = await CreateFakeGhAsync("""
            #!/usr/bin/env bash
            set -eu
            printf '%s\n' "$*" >> gh-calls.log
            if [ "$1" = api ]; then
              if [ -f invalidate-evidence-on-lookup ]; then
                printf 'not JSON' > ci-failure-analysis/evidence/run-context.json
                printf 'not JSON' > ci-failure-analysis/evidence/diagnostics/run.json
                printf 'not JSON' > ci-failure-analysis/evidence/diagnostics/failed-jobs.json
              fi
              if [[ "$2" = */pulls/42 ]]; then
                if [ -f close-after-lookup ] && [ -f pr-lookup ]; then
                  printf '%s' '{"state":"closed","locked":false}'
                else
                  printf '%s' '{"state":"open","locked":false}'
                fi
                touch pr-lookup
              elif [[ "$*" = *"issues/42/comments"* ]]; then
                if [ -f comments.json ]; then cat comments.json; else printf '[[]]'; fi
              elif [[ "$*" = *"issues/77/comments"* ]]; then
                if [ -f overflow-comments.json ]; then printf '['; cat overflow-comments.json; printf ']'; else printf '[[]]'; fi
              elif [[ "$*" = *"--method PATCH"* ]]; then
                endpoint="$4"
                while [ "$#" -gt 0 ]; do
                  if [ "$1" = --input ]; then cp "$2" published-comment-request.json; fi
                  shift
                done
                if [[ "$endpoint" = *"/issues/comments/"* ]] && [ -f overflow-comments.json ]; then
                  jq --argjson id "${endpoint##*/}" --slurpfile request published-comment-request.json \
                    'map(if .id == $id then .body = $request[0].body else . end)' overflow-comments.json > overflow-comments.updated.json
                  mv overflow-comments.updated.json overflow-comments.json
                fi
                printf '{}'
              elif [[ "$2" =~ /issues/[0-9]+$ ]]; then
                if [ -f issue-lookup-failure ]; then printf '%s\n' 'injected issue lookup failure' >&2; exit 23; fi
                if [ -f issue-details.json ]; then cat issue-details.json; else jq '.[0]' issue.json; fi
              elif [ -f issue.json ]; then printf '['; cat issue.json; printf ']'; else printf '[]'; fi
            elif [ "$1 $2" = "pr comment" ]; then
              number="$3"
              while [ "$#" -gt 0 ]; do
                if [ "$1" = --body-file ]; then cp "$2" pr-comment.md; fi
                shift
              done
              if [ "$number" = 77 ]; then
                if [ -f fail-overflow-comment-once ]; then rm fail-overflow-comment-once; exit 23; fi
                if [ ! -f overflow-comments.json ]; then printf '[]' > overflow-comments.json; fi
                id="$(jq 'length + 1000' overflow-comments.json)"
                jq --rawfile body pr-comment.md --argjson id "$id" \
                  '. + [{id:$id,body:$body,user:{login:"github-actions[bot]"}}]' overflow-comments.json > overflow-comments.updated.json
                mv overflow-comments.updated.json overflow-comments.json
                printf 'https://github.com/microsoft/aspire/issues/77#issuecomment-%s\n' "$id"
              elif [ -f comment-response ]; then cat comment-response;
              else printf 'https://github.com/microsoft/aspire/pull/42#issuecomment-123\n'; fi
            elif [ "$1 $2" = "label create" ] || [ "$1 $2" = "issue reopen" ]; then
              exit 0
            elif [ "$1 $2" = "issue create" ] || [ "$1 $2" = "issue edit" ]; then
              if [ -f fail-issue-create-once ]; then
                rm fail-issue-create-once
                printf '%s\n' 'injected issue create failure' >&2
                exit 23
              fi
              if [ -f fail-second-issue ]; then
                if [ -f first-issue-published ]; then
                  printf '%s\n' 'injected second issue failure' >&2
                  exit 23
                fi
                touch first-issue-published
              fi
              while [ "$#" -gt 0 ]; do
                if [ "$1" = --body-file ]; then cp "$2" issue-body.md; fi
                shift
              done
              if [ -f issue-body.md ] && grep -q '<!-- ci-failure-overflow:' issue-body.md; then
                jq -n --rawfile body issue-body.md \
                  '[{number:77,state:"open",locked:false,html_url:"https://github.com/microsoft/aspire/issues/77",body:$body,labels:[{name:"ci-failure-cause"}],user:{login:"github-actions[bot]"}}]' > issue.json
              fi
              printf 'https://github.com/microsoft/aspire/issues/77\n'
            else
              exit 1
            fi
            """);
        return useTypedProposal ? await RunTypedPublicationCommandAsync(fakeGh, "2026-10-21T01:00:00Z") : await RunPublicationCommandAsync(fakeGh);
    }

    private Task<CommandResult> RunPublicationCommandAsync(string fakeGh)
        => RunPublicationCommandAsync(fakeGh, "2026-10-21T01:00:00Z");

    private Task<CommandResult> RunPublicationCommandAsync(string fakeGh, string observedAt)
    {
        AnalyzeCiFailureFixtures.Write(_workspace.Path);
        return RunTypedPublicationCommandAsync(fakeGh, observedAt);
    }

    private async Task<CommandResult> RunTypedPublicationCommandAsync(string fakeGh, string observedAt)
    {
        await PrepareFreshPublicationCheckoutAsync();
        _publicationHistoryBefore = await RunProcessAsync(
            "git",
            ["--no-pager", "--git-dir", "remote.git",
            "show-ref", "--verify", "refs/heads/memory/ci-failure-analysis"]);
        return await RunProcessAsync(
            Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
            ["exec", "--runtimeconfig", Path.ChangeExtension(typeof(AnalyzeCiFailureWorkflowTests).Assembly.Location, ".runtimeconfig.json"),
                typeof(AnalyzeCiFailure.Program).Assembly.Location, "publish", "--working-dir", _workspace.Path,
                "--evidence-dir", EvidenceDirectory, "--analysis-dir", Path.Combine(_workspace.Path, "ci-analysis-output"),
                "--history-dir", Path.Combine(_workspace.Path, "memory-repo"), "--repo", "microsoft/aspire",
                "--observed-at", observedAt],
            new Dictionary<string, string>
            {
                ["PATH"] = $"{Path.GetDirectoryName(fakeGh)}{Path.PathSeparator}{Environment.GetEnvironmentVariable("PATH")}",
            });
    }

    private async Task PrepareFreshPublicationCheckoutAsync()
    {
        var checkout = Path.Combine(_workspace.Path, "memory-repo");
        var remote = Path.Combine(_workspace.Path, "remote.git");
        var init = await RunProcessAsync("git", ["--no-pager", "init"]);
        Assert.True(init.ExitCode == 0, init.Output);
        var origin = await RunProcessAsync("git", ["--no-pager", "remote", "get-url", "origin"]);
        var configure = await RunProcessAsync(
            "git",
            origin.ExitCode == 0
            ? ["--no-pager", "remote", "set-url", "origin", remote]
            : ["--no-pager", "remote", "add", "origin", remote]);
        Assert.True(configure.ExitCode == 0, configure.Output);
        if (!Directory.Exists(checkout))
        {
            return;
        }
        var files = new[] { "causes", "runs" }.SelectMany(name => Directory.Exists(Path.Combine(checkout, name))
            ? Directory.GetFiles(Path.Combine(checkout, name), "*.json") : []).ToArray();
        if (files.Length > 0)
        {
            var branch = await RunProcessAsync("git", ["--no-pager", "-C", checkout, "checkout", "-B", "memory/ci-failure-analysis"]);
            Assert.True(branch.ExitCode == 0, branch.Output);
            var stage = await RunProcessAsync(
                "git",
                ["--no-pager", "-C", checkout, "add", "--",
                .. files.Select(file => Path.GetRelativePath(checkout, file))]);
            Assert.True(stage.ExitCode == 0, stage.Output);
            var changes = await RunProcessAsync("git", ["--no-pager", "-C", checkout, "diff", "--cached", "--name-only"]);
            Assert.True(changes.ExitCode == 0, changes.Output);
            if (changes.Output.Length > 0)
            {
                var commit = await RunProcessAsync("git", ["--no-pager", "-C", checkout, "commit", "-m", "Seed publication fixture"]);
                Assert.True(commit.ExitCode == 0, commit.Output);
            }
            var push = await RunProcessAsync("git", ["--no-pager", "-C", checkout, "push", "origin", "HEAD:refs/heads/memory/ci-failure-analysis"]);
            Assert.True(push.ExitCode == 0, push.Output);
        }
        Directory.Delete(checkout, recursive: true);
    }

    private static JsonObject CreateTrustedOccurrence(string scope, string prNumbers, string job)
    {
        var id = AnalyzeCiFailure.NormalizedJsonNumber.Parse("1");
        var cause = new AnalyzeCiFailure.FailureCause("cause", "cause.json", AnalyzeCiFailure.CauseType.InfrastructureFailure, [id], default);
        var bundle = AnalyzeCiFailureFixtures.CreateEvidence(
            AnalyzeCiFailure.CiFailureJson.ReadEnum<AnalyzeCiFailure.RunScope>(scope, "Invalid test scope"));
        var analysis = new AnalyzeCiFailure.ValidatedAnalysis(
            new AnalyzeCiFailure.AnalysisReport(),
            new AnalyzeCiFailure.ValidatedRun(
                AnalyzeCiFailure.CiFailureJson.ReadEnum<AnalyzeCiFailure.RunScope>(scope, "Invalid test scope"),
                "https://github.com/microsoft/aspire/actions/runs/123",
                prNumbers),
            [new AnalyzeCiFailure.FailedJob(id, "1", job)],
            [cause],
            [],
            new AnalyzeCiFailure.RunEvidence(new(), new(), []))
        {
            Bundle = bundle,
            CapturedPublication = AnalyzeCiFailure.PublicationEvidence.FromBundle(bundle),
        };

        return AnalyzeCiFailure.AnalysisPublisher.CreateOccurrence(analysis, cause, JsonValue.Create(value: 123)!, "2026-08-31T12:00:00Z");
    }

    [Fact]
    [RequiresTools(["bash", "jq", "git"])]
    public async Task LaterIssueFailureKeepsEarlierLocalLinkAndAllDurableOccurrences()
    {
        await WriteGroupedFixtureAsync();
        var analysisDirectory = Path.Combine(_workspace.Path, "ci-analysis-output");
        var cause = JsonNode.Parse(await File.ReadAllTextAsync(
            Path.Combine(analysisDirectory, "causes", "debugger-timeout.json")))!;
        cause["id"] = "second-timeout";
        cause["title"] = "Second timeout";
        await File.WriteAllTextAsync(Path.Combine(analysisDirectory, "causes", "second-timeout.json"), cause.ToJsonString());
        var summaryPath = Path.Combine(analysisDirectory, "analysis-result.json");
        var summary = JsonNode.Parse(await File.ReadAllTextAsync(summaryPath))!;
        summary["causes"]!.AsArray().Add("second-timeout");
        await File.WriteAllTextAsync(summaryPath, summary.ToJsonString());
        await File.WriteAllTextAsync(Path.Combine(_workspace.Path, "fail-second-issue"), "");

        var result = await RunSuccessfulGroupedPublicationAsync();

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("POST /repos/microsoft/aspire/issues: HTTP 500.", result.Output, StringComparison.Ordinal);
        var recovery = RecoveryDirectory("publication");
        Assert.True(File.Exists(Path.Combine(recovery, "manifest.json")));
        var recoveryOutcome = AnalyzeCiFailure.TransientJson.Read<AnalyzeCiFailure.AnalysisOutcome>(
            await File.ReadAllTextAsync(Path.Combine(recovery, "collection-outcome.json")));
        Assert.Equal("failed", recoveryOutcome.Status);
        Assert.Equal("publication", Assert.Single(recoveryOutcome.Problems).Stage);
        foreach (var id in new[] { "debugger-timeout", "second-timeout" })
        {
            Assert.True(File.Exists(Path.Combine(recovery, "requests", $"{id}.issue-body.md")));
            var path = Path.Combine(_workspace.Path, "memory-repo", "causes", $"{id}.json");
            var local = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
            Assert.Equal(
                id == "debugger-timeout" ? "https://github.com/microsoft/aspire/issues/77" : null,
                local["issue_url"]?.GetValue<string>());
            var durable = await RunProcessAsync(
                "git",
                ["--no-pager", "--git-dir", "remote.git",
                "show", $"refs/heads/memory/ci-failure-analysis:causes/{id}.json"]);
            Assert.True(durable.ExitCode == 0, durable.Output);
            var stored = JsonNode.Parse(durable.Output)!;
            Assert.Null(stored["issue_url"]);
            var occurrence = Assert.Single(stored["occurrences"]!.AsArray())!;
            Assert.Equal(123, occurrence["run_id"]!.GetValue<int>());
            Assert.Equal(2, occurrence["tests"]!.AsArray().Count);
            Assert.False(File.Exists(Path.Combine(analysisDirectory, $"{id}.issue-body.md")));
        }
        Assert.False(File.Exists(Path.Combine(_workspace.Path, "pr-comment.md")));
    }

    private async Task WriteGroupedFixtureAsync()
    {
        await WriteValidationFixtureAsync(
            """{"run_id":123,"run_scope":"pull-request","verdict":"flaky-test","pr":{"number":42},"failed_jobs":[{"id":123,"classification":"flaky-test"}],"failed_tests":[{"name":"Tests.Method(value: 1/2)","job":"Tests","job_id":123,"error":"untrusted","classification":"flaky","reason":"Timeout"},{"name":"Tests.Other","job":"Tests","job_id":123,"error":"untrusted","classification":"flaky","reason":"Timeout"}],"causes":["debugger-timeout"]}""",
            """{"run_id":123,"run_scope":"pull-request","pr_numbers":"42"}""",
            """[{"id":123,"name":"Tests"}]""",
            "debugger-timeout.json",
            """{"id":"debugger-timeout","type":"flaky-test","title":"Debugger timeout","error_pattern":"Connection timed out","job_ids":[123],"tests":[{"name":"Tests.Method(value: 1/2)","job_id":123},{"name":"Tests.Other","job_id":123}]}""");
        await File.WriteAllTextAsync(
            Path.Combine(EvidenceDirectory, "test-failures.json"),
            """[{"test":"Tests.Method(value: 1/2)","job":"Tests","job_id":123,"result_file":"results.trx","result_format":"trx","error":"Trusted debugger timeout"},{"test":"Tests.Other","job":"Tests","job_id":123,"result_file":"results.trx","result_format":"trx","error":"Trusted debugger timeout"}]""");
        foreach (var name in new[] { "pr-metadata.json", "triggering-merge-pr.json", "last-successful-main-run.json", "candidate-merge-history-status.json" })
        {
            await File.WriteAllTextAsync(Path.Combine(EvidenceDirectory, name), "{}");
        }
        await File.WriteAllTextAsync(Path.Combine(EvidenceDirectory, "diagnostics/candidate-merges.json"), "[]");
    }

    [Fact]
    [RequiresTools(["bash", "jq"])]
    public async Task GroupedCauseValidatesExactObservations()
    {
        await WriteGroupedFixtureAsync();
        var result = await RunValidationScriptAsync("");
        Assert.True(result.ExitCode == 0, result.Output);
    }

    [Theory]
    [RequiresTools(["bash", "jq"])]
    [InlineData("missing")]
    [InlineData("wrong-job")]
    [InlineData("duplicate")]
    public async Task GroupedCauseRejectsInvalidObservations(string mutation)
    {
        await WriteGroupedFixtureAsync();
        var (evidence, proposal) = AnalyzeCiFailureFixtures.Read(_workspace.Path);
        var document = JsonNode.Parse(AnalyzeCiFailure.TransientJson.Write(proposal))!;
        var tests = document["causes"]![0]!["test_refs"]!.AsArray();
        switch (mutation)
        {
            case "missing":
                tests.RemoveAt(index: 1);
                break;
            case "wrong-job":
                tests[0] = "foreign-job-999-test";
                break;
            case "duplicate":
                tests.Add(tests[0]!.DeepClone());
                break;
        }
        var result = await RunStrictValidationAsync(AnalyzeCiFailure.TransientJson.Write(evidence), document.ToJsonString());

        Assert.Equal(1, result.ExitCode);
        var diagnostic = mutation switch
        {
            "missing" => "Every flaky test and job observation must be covered by a matching cause",
            "wrong-job" => "A cause references a test that is not in the collected evidence.",
            _ => "Each cause must reference unique collected job IDs and test references, with at least one job.",
        };
        Assert.Equal("::error::" + diagnostic + "\n", result.Output.ReplaceLineEndings("\n"));
    }

    [Fact]
    [RequiresTools(["bash", "jq"])]
    public async Task GroupedCauseAllowsDifferentMechanismsForSameMethod()
    {
        await WriteGroupedFixtureAsync();
        var directory = Path.Combine(_workspace.Path, "ci-analysis-output");
        var path = Path.Combine(directory, "causes", "debugger-timeout.json");
        var cause = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        var other = cause.DeepClone();
        other["id"] = "assertion-failure";
        other["title"] = "Intermittent assertion failure";
        other["error_pattern"] = "Expected value was different";
        other["tests"] = new JsonArray(((JsonArray)cause["tests"]!)[1]!.DeepClone());
        ((JsonArray)cause["tests"]!).RemoveAt(index: 1);
        await File.WriteAllTextAsync(path, cause.ToJsonString());
        await File.WriteAllTextAsync(Path.Combine(directory, "causes", "assertion-failure.json"), other.ToJsonString());
        var summaryPath = Path.Combine(directory, "analysis-result.json");
        var summary = JsonNode.Parse(await File.ReadAllTextAsync(summaryPath))!;
        ((JsonArray)summary["causes"]!).Add("assertion-failure");
        summary["failed_tests"]![1]!["name"] = "Tests.Method(value: 3/4)";
        other["tests"]![0]!["name"] = "Tests.Method(value: 3/4)";
        await File.WriteAllTextAsync(Path.Combine(directory, "causes", "assertion-failure.json"), other.ToJsonString());
        await File.WriteAllTextAsync(summaryPath, summary.ToJsonString());
        var evidencePath = Path.Combine(EvidenceDirectory, "test-failures.json");
        var evidence = JsonNode.Parse(await File.ReadAllTextAsync(evidencePath))!;
        evidence[1]!["test"] = "Tests.Method(value: 3/4)";
        evidence[1]!["error"] = "Expected value was different";
        await File.WriteAllTextAsync(evidencePath, evidence.ToJsonString());

        var result = await RunValidationScriptAsync("");

        Assert.True(result.ExitCode == 0, result.Output);
    }

    [Fact]
    [RequiresTools(["bash", "jq"])]
    public async Task GroupedCausePreservesAtLeastOneCauseCoverage()
    {
        await WriteGroupedFixtureAsync();
        var directory = Path.Combine(_workspace.Path, "ci-analysis-output");
        var other = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "causes", "debugger-timeout.json")))!;
        other["id"] = "other-mechanism";
        await File.WriteAllTextAsync(Path.Combine(directory, "causes", "other-mechanism.json"), other.ToJsonString());
        var path = Path.Combine(directory, "analysis-result.json");
        var summary = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        ((JsonArray)summary["causes"]!).Add("other-mechanism");
        await File.WriteAllTextAsync(path, summary.ToJsonString());

        var result = await RunValidationScriptAsync("");

        Assert.True(result.ExitCode == 0, result.Output);
    }

    [Fact]
    [RequiresTools(["bash", "jq", "git"])]
    public async Task GroupedCauseAllowsFlakyAndDeterministicFailuresInOneJob()
    {
        await WriteGroupedFixtureAsync();
        var path = Path.Combine(_workspace.Path, "ci-analysis-output", "analysis-result.json");
        var summary = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        summary["verdict"] = "mixed";
        summary["failed_jobs"]![0]!["classification"] = "code-issue";
        var deterministic = summary["failed_tests"]![0]!.DeepClone();
        deterministic["name"] = "Tests.Deterministic";
        deterministic["classification"] = "code-issue";
        ((JsonArray)summary["failed_tests"]!).Add(deterministic);
        await File.WriteAllTextAsync(path, summary.ToJsonString());
        var evidencePath = Path.Combine(EvidenceDirectory, "test-failures.json");
        var evidence = JsonNode.Parse(await File.ReadAllTextAsync(evidencePath))!;
        var failure = evidence[0]!.DeepClone();
        failure["test"] = "Tests.Deterministic";
        ((JsonArray)evidence).Add(failure);
        await File.WriteAllTextAsync(evidencePath, evidence.ToJsonString());

        var result = await RunValidationScriptAsync("");

        Assert.True(result.ExitCode == 0, result.Output);
        var published = await RunSuccessfulGroupedPublicationAsync();
        Assert.True(published.ExitCode == 0, published.Output);
        var history = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(_workspace.Path, "memory-repo", "causes", "debugger-timeout.json")))!;
        Assert.Equal(2, history["occurrences"]![0]!["tests"]!.AsArray().Count);
        var run = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(_workspace.Path, "memory-repo", "runs", "123.json")))!;
        Assert.Equal("mixed", run["verdict"]!.GetValue<string>());
        Assert.Equal(3, run["failed_tests"]!.AsArray().Count);
    }

    [Fact]
    [RequiresTools(["bash", "jq"])]
    public async Task GroupedCauseBindsDuplicateJobNamesByNumericId()
    {
        await WriteGroupedFixtureAsync();
        var directory = Path.Combine(_workspace.Path, "ci-analysis-output");
        var summaryPath = Path.Combine(directory, "analysis-result.json");
        var summary = JsonNode.Parse(await File.ReadAllTextAsync(summaryPath))!;
        ((JsonArray)summary["failed_jobs"]!).Add(new JsonObject { ["id"] = 456, ["classification"] = "flaky-test" });
        summary["failed_tests"]![1]!["job_id"] = 456;
        summary["failed_tests"]![1]!["name"] = "Tests.Method(value: 1/2)";
        await File.WriteAllTextAsync(summaryPath, summary.ToJsonString());
        var path = Path.Combine(directory, "causes", "debugger-timeout.json");
        var cause = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        ((JsonArray)cause["job_ids"]!).Add(456);
        cause["tests"]![1]!["job_id"] = 456;
        cause["tests"]![1]!["name"] = "Tests.Method(value: 1/2)";
        await File.WriteAllTextAsync(path, cause.ToJsonString());
        var data = EvidenceDirectory;
        await File.WriteAllTextAsync(Path.Combine(data, "diagnostics/failed-jobs.json"), """[{"id":123,"name":"Tests"},{"id":456,"name":"Tests"}]""");
        var evidencePath = Path.Combine(data, "test-failures.json");
        var evidence = JsonNode.Parse(await File.ReadAllTextAsync(evidencePath))!;
        evidence[1]!["job_id"] = 456;
        evidence[1]!["test"] = "Tests.Method(value: 1/2)";
        await File.WriteAllTextAsync(evidencePath, evidence.ToJsonString());

        var result = await RunValidationScriptAsync("");

        Assert.True(result.ExitCode == 0, result.Output);
    }

    [Fact]
    [RequiresTools(["bash", "jq"])]
    public async Task GroupedCauseTreatsStoredLegacyTestNameAsDescription()
    {
        await WriteGroupedFixtureAsync();
        var prior = Directory.CreateDirectory(Path.Combine(EvidenceDirectory, "history/causes")).FullName;
        await File.WriteAllTextAsync(
            Path.Combine(prior, "debugger-timeout.json"),
            """{"id":"debugger-timeout","type":"flaky-test","test_name":"Opaque/legacy/label"}""");

        var result = await RunValidationScriptAsync("");

        Assert.True(result.ExitCode == 0, result.Output);
    }

    [Fact]
    public void GroupedReplayKeepsFirstStoredRunWithoutAttempts()
    {
        var definition = JsonNode.Parse("""{"id":"timeout","type":"flaky-test","title":"New title"}""")!.AsObject();
        var existing = JsonNode.Parse(
            """{"id":"timeout","type":"flaky-test","title":"Stored title","occurrences":[{"run_id":123,"job":"Original job","observed_at":"2026-10-20T01:00:00Z","tests":[{"name":"Original/parameterized(value: 1)","job_id":1}]}]}""")!.AsObject();
        var occurrence = JsonNode.Parse(
            """{"run_id":123,"job":"Changed job","observed_at":"2026-10-21T01:00:00Z","tests":[{"name":"Different test","job_id":2}]}""")!.AsObject();

        var merged = AnalyzeCiFailure.AnalysisPublisher.MergeCause(definition, existing, occurrence);

        Assert.Equal(existing.ToJsonString(), merged.ToJsonString());
    }

    [Fact]
    [RequiresTools(["bash", "jq"])]
    public async Task GroupedOccurrenceSeparatesExactEvidenceFromRedactedDisplay()
    {
        await WriteGroupedFixtureAsync();
        const string exactName = "Tests.Method(token: \"fake-value-for-redaction\")";
        var directory = Path.Combine(_workspace.Path, "ci-analysis-output");
        var summaryPath = Path.Combine(directory, "analysis-result.json");
        var summary = JsonNode.Parse(await File.ReadAllTextAsync(summaryPath))!;
        summary["failed_tests"]![0]!["name"] = exactName;
        await File.WriteAllTextAsync(summaryPath, summary.ToJsonString());
        var causePath = Path.Combine(directory, "causes", "debugger-timeout.json");
        var cause = JsonNode.Parse(await File.ReadAllTextAsync(causePath))!;
        cause["tests"]![0]!["name"] = exactName;
        await File.WriteAllTextAsync(causePath, cause.ToJsonString());
        var evidencePath = Path.Combine(EvidenceDirectory, "test-failures.json");
        var evidence = JsonNode.Parse(await File.ReadAllTextAsync(evidencePath))!;
        evidence[0]!["test"] = exactName;
        await File.WriteAllTextAsync(evidencePath, evidence.ToJsonString());

        var collected = AnalyzeCiFailureFixtures.Validate(_workspace.Path);
        var occurrence = AnalyzeCiFailure.AnalysisPublisher.CreateOccurrence(collected, Assert.Single(collected.Causes), JsonValue.Create(value: 123)!, "2026-10-21T01:00:00Z");

        Assert.Equal(exactName, collected.Tests[0].Name);
        var publishedSummary = AnalyzeCiFailure.RunSummaryBuilder.Create(
            collected.Report,
            collected.Evidence,
            collected.CapturedPublication!,
            "2026-08-31T12:00:00Z");
        var comment = AnalyzeCiFailure.PrCommentRenderer.Render(collected.Report, collected.Evidence.Jobs, collected.Run.Url);
        var projection = new JsonObject
        {
            ["occurrence"] = occurrence.DeepClone(),
            ["run_summary"] = AnalyzeCiFailure.CiFailureJson.ToNode(publishedSummary),
            ["comment"] = comment,
        };
        await Verifier.Verify(
            projection.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }),
            "json").UseDirectory("Snapshots");
    }

    [Fact]
    [RequiresTools(["bash", "jq", "git"])]
    public async Task GroupedPublicationResumesFromDurableHistoryAndPreservesNotes()
    {
        await WriteGroupedFixtureAsync();
        await RunProcessAsync("git", ["--no-pager", "init", "--bare", "remote.git"]);
        await RunProcessAsync("git", ["--no-pager", "clone", "remote.git", "memory-repo"]);
        await RunProcessAsync("git", ["--no-pager", "-C", "memory-repo", "config", "user.name", "Test"]);
        await RunProcessAsync("git", ["--no-pager", "-C", "memory-repo", "config", "user.email", "test@example.invalid"]);
        var storedDirectory = Directory.CreateDirectory(Path.Combine(_workspace.Path, "memory-repo", "causes")).FullName;
        await File.WriteAllTextAsync(
            Path.Combine(storedDirectory, "debugger-timeout.json"),
            """{"id":"debugger-timeout","type":"flaky-test","title":"Debugger timeout","test_name":"Opaque/legacy/label","error_pattern":"Connection timed out","occurrences":[]}""");
        var fakeGh = await CreateFakeGhAsync("""
            #!/usr/bin/env bash
            set -eu
            echo "$*" >> gh-calls.log
            if [ "$1" = api ]; then
              if [[ "$2" = */issues/77 ]]; then jq '.[0]' issue.json;
              elif [ -f issue.json ]; then printf '['; cat issue.json; printf ']'; else printf '[]'; fi
            elif [ "$1 $2" = "issue create" ]; then
              while [ "$#" -gt 0 ]; do
                if [ "$1" = --body-file ]; then cp "$2" issue-body.md; fi
                shift
              done
              echo 'injected issue create failure' >&2
              exit 1
            elif [ "$1 $2" = "issue edit" ]; then
              while [ "$#" -gt 0 ]; do
                if [ "$1" = --body-file ]; then cp "$2" issue-body.md; fi
                shift
              done
            else
              exit 1
            fi
            """);
        var environment = new Dictionary<string, string>
        {
            // An obsolete environment value must never select the proposal for publication.
            ["ANALYSIS_DIR"] = Path.Combine(_workspace.Path, "obsolete-analysis-directory"),
            ["PATH"] = $"{Path.GetDirectoryName(fakeGh)}{Path.PathSeparator}{Environment.GetEnvironmentVariable("PATH")}",
        };
        string[] args = ["exec", "--runtimeconfig", Path.ChangeExtension(typeof(AnalyzeCiFailureWorkflowTests).Assembly.Location, ".runtimeconfig.json"),
            typeof(AnalyzeCiFailure.Program).Assembly.Location, "publish", "--working-dir", _workspace.Path,
            "--evidence-dir", EvidenceDirectory, "--analysis-dir", "ci-analysis-output",
            "--history-dir", Path.Combine(_workspace.Path, "memory-repo"), "--repo", "microsoft/aspire",
            "--observed-at", "2026-10-21T01:00:00Z"];
        var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
        AnalyzeCiFailureFixtures.Write(_workspace.Path);
        await PrepareFreshPublicationCheckoutAsync();
        var first = await RunProcessAsync(host, args, environment);
        Assert.Equal(1, first.ExitCode);
        Assert.Contains("POST /repos/microsoft/aspire/issues: HTTP 500.", first.Output, StringComparison.Ordinal);
        Assert.Contains(
            "issue create --repo microsoft/aspire --title [CI Failure] Debugger timeout --label ci-failure-cause,test-failure --body-file",
            await File.ReadAllTextAsync(Path.Combine(_workspace.Path, "gh-calls.log")),
            StringComparison.Ordinal);
        var storedPath = Path.Combine(storedDirectory, "debugger-timeout.json");
        var stored = JsonNode.Parse(await File.ReadAllTextAsync(storedPath))!;
        var durable = await RunProcessAsync(
            "git",
            ["--no-pager", "--git-dir", "remote.git", "show", "refs/heads/memory/ci-failure-analysis:causes/debugger-timeout.json"]);
        Assert.Equal(0, durable.ExitCode);
        Assert.True(JsonNode.DeepEquals(stored, JsonNode.Parse(durable.Output)));
        Assert.Equal("Opaque/legacy/label", stored["test_name"]!.GetValue<string>());
        Assert.Equal(2, stored["occurrences"]![0]!["tests"]!.AsArray().Count);
        Assert.Equal("results.trx", stored["occurrences"]![0]!["tests"]![0]!["result_file"]!.GetValue<string>());
        Assert.Equal("Trusted debugger timeout", stored["occurrences"]![0]!["tests"]![0]!["error"]!.GetValue<string>());
        Assert.Null(stored["occurrences"]![0]!["run_attempt"]);
        var body = await File.ReadAllTextAsync(Path.Combine(_workspace.Path, "issue-body.md"));
        await Verifier.Verify(body, "md").UseDirectory("Snapshots");
        await File.WriteAllTextAsync(
            Path.Combine(_workspace.Path, "issue.json"),
            new JsonArray(new JsonObject
            {
                ["number"] = 77,
                ["state"] = "open",
                ["body"] = body + "\nOperator notes: keep this.\n",
                ["html_url"] = "https://github.com/microsoft/aspire/issues/77",
                ["labels"] = new JsonArray(new JsonObject { ["name"] = "ci-failure-cause" }),
            }).ToJsonString());

        await PrepareFreshPublicationCheckoutAsync();
        var replay = await RunProcessAsync(host, args, environment);

        Assert.True(replay.ExitCode == 0, replay.Output);
        stored = JsonNode.Parse(await File.ReadAllTextAsync(storedPath))!;
        Assert.Single(stored["occurrences"]!.AsArray());
        Assert.Equal("https://github.com/microsoft/aspire/issues/77", stored["issue_url"]!.GetValue<string>());
        Assert.Contains("Operator notes: keep this.", await File.ReadAllTextAsync(Path.Combine(_workspace.Path, "issue-body.md")), StringComparison.Ordinal);
        durable = await RunProcessAsync(
            "git",
            ["--no-pager", "--git-dir", "remote.git", "show", "refs/heads/memory/ci-failure-analysis:causes/debugger-timeout.json"]);
        Assert.Equal("https://github.com/microsoft/aspire/issues/77", JsonNode.Parse(durable.Output)!["issue_url"]!.GetValue<string>());
    }
}
