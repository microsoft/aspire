// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Aspire.TestUtilities;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace Infrastructure.Tests;

[Trait("Category", "AgenticWorkflow")]
public sealed class AutoSecWorkflowTests(ITestOutputHelper testOutput)
{
    private const string HeadSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Now = "2026-10-01T00:00:00Z";

    [Theory]
    [RequiresTools(["node"])]
    [InlineData("files", 401)]
    [InlineData("files", 403)]
    [InlineData("files", 429)]
    [InlineData("files", 503)]
    [InlineData("base", 401)]
    [InlineData("base", 403)]
    [InlineData("base", 404)]
    [InlineData("base", 429)]
    [InlineData("base", 500)]
    [InlineData("head", 401)]
    [InlineData("head", 403)]
    [InlineData("head", 404)]
    [InlineData("head", 429)]
    [InlineData("head", 503)]
    public async Task DependabotCollectionFailsClosedOnApiErrors(string phase, int status)
    {
        var request = CreateCollectionScenario();
        var endpoint = phase == "files"
            ? "repos/microsoft/aspire/pulls/101/files?per_page=100"
            : $"repos/microsoft/aspire/contents/extension/yarn.lock?ref={phase}";
        request["failure"] = new JsonObject { ["endpoint"] = endpoint, ["status"] = status };

        var result = await RunHarnessAsync(request);

        Assert.True(result["failed"]!.GetValue<bool>());
        Assert.Null(result["output"]);
        Assert.Equal(endpoint, result["calls"]!.AsArray().Last()!.GetValue<string>());
    }

    [Theory]
    [RequiresTools(["node"])]
    [InlineData("modified")]
    [InlineData("added")]
    [InlineData("removed")]
    [InlineData("renamed")]
    public async Task DependabotCollectionUsesTrustedFileStatusesForMissingSides(string status)
    {
        var request = CreateCollectionScenario();
        var file = request["changedFiles"]![1]![0]!;
        file["status"] = status;
        var contents = request["contents"]!.AsObject();
        if (status == "added")
        {
            contents.Remove("repos/microsoft/aspire/contents/extension/yarn.lock?ref=base");
        }
        else if (status == "removed")
        {
            contents.Remove("repos/microsoft/aspire/contents/extension/yarn.lock?ref=head");
        }
        else if (status == "renamed")
        {
            file["previous_filename"] = "extension/old yarn.lock";
            contents["repos/microsoft/aspire/contents/extension/old%20yarn.lock?ref=base"] =
                contents["repos/microsoft/aspire/contents/extension/yarn.lock?ref=base"]!.DeepClone();
            contents.Remove("repos/microsoft/aspire/contents/extension/yarn.lock?ref=base");
        }

        var result = await RunHarnessAsync(request);

        Assert.False(result["failed"]!.GetValue<bool>());
        var pr = Assert.Single(result["output"]!.AsArray())!;
        Assert.Equal(["README.md", "extension/yarn.lock"], pr["files"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal(status == "removed" ? [] : [7], pr["covered_alerts"]!.AsArray().Select(n => n!.GetValue<int>()));
        var calls = new List<string> { "repos/microsoft/aspire/pulls/101/files?per_page=100" };
        if (status != "added")
        {
            calls.Add($"repos/microsoft/aspire/contents/extension/{(status == "renamed" ? "old%20yarn.lock" : "yarn.lock")}?ref=base");
        }
        if (status != "removed")
        {
            calls.Add("repos/microsoft/aspire/contents/extension/yarn.lock?ref=head");
        }
        Assert.Equal(calls, result["calls"]!.AsArray().Select(n => n!.GetValue<string>()));
    }

    [Theory]
    [RequiresTools(["node"])]
    [InlineData("added")]
    [InlineData("removed")]
    [InlineData("unknown")]
    [InlineData("renamed")]
    public async Task DependabotCollectionRejectsUnexpectedMissingFilesAndStatuses(string status)
    {
        var request = CreateCollectionScenario();
        request["changedFiles"]![1]![0]!["status"] = status;
        if (status is "added" or "removed")
        {
            request["failure"] = new JsonObject
            {
                ["endpoint"] = $"repos/microsoft/aspire/contents/extension/yarn.lock?ref={(status == "added" ? "head" : "base")}",
                ["status"] = 404,
            };
        }

        var result = await RunHarnessAsync(request);

        Assert.True(result["failed"]!.GetValue<bool>());
        Assert.Null(result["output"]);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task ApprovesDependabotPrThatPassesEveryGate()
    {
        var result = await RunHarnessAsync(CreateApprovalScenario());

        var decision = Assert.Single(result["value"]!.AsArray());
        Assert.Equal("approve", decision!["decision"]!.GetValue<string>());
        Assert.Empty(decision["reasons"]!.AsArray());
        Assert.Equal([7], decision["fixedAlerts"]!.AsArray().Select(n => n!.GetValue<int>()));
        var review = Assert.Single(result["reviews"]!.AsArray());
        Assert.Equal("APPROVE", review!["event"]!.GetValue<string>());
        Assert.Equal(101, review["pull_number"]!.GetValue<int>());
        Assert.Equal(HeadSha, review["commit_id"]!.GetValue<string>());
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task StagedModeEvaluatesGatesWithoutSubmittingReview()
    {
        var scenario = CreateApprovalScenario();
        scenario["staged"] = true;

        var result = await RunHarnessAsync(scenario);

        Assert.Equal("approve", result["value"]![0]!["decision"]!.GetValue<string>());
        Assert.Empty(result["reviews"]!.AsArray());
        Assert.Contains("(staged)", result["summary"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Theory]
    [RequiresTools(["node"])]
    [InlineData("evaluation")]
    [InlineData("live-pr")]
    [InlineData("live-checks")]
    [InlineData("live-statuses")]
    [InlineData("submission")]
    [InlineData("submission-after-accept")]
    [InlineData("parse-output")]
    [InlineData("read-output")]
    [InlineData("summary")]
    public async Task ApprovalFailuresPublishOnlyFixedReasonCodes(string phase)
    {
        var scenario = CreateApprovalScenario();
        scenario["approvalFailure"] = phase;

        var result = await RunHarnessAsync(scenario);

        var reason = phase is "submission" or "submission-after-accept" ? "approval-submission-failed" : "gate-evaluation-failed";
        Assert.Equal([$"Auto-sec approval failed: {reason}."], result["failures"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Empty(result["warnings"]!.AsArray());
        if (phase is "parse-output" or "read-output" or "summary")
        {
            Assert.Empty(result["value"]!.AsArray());
            Assert.Equal(phase == "summary" ? "auto-sec Dependabot approvals\nRequests: 1. Approved: 1. Skipped: 0.\n\n- #101: approve" : "", result["summary"]!.GetValue<string>());
        }
        else
        {
            var decision = Assert.Single(result["value"]!.AsArray());
            Assert.Equal("skip", decision!["decision"]!.GetValue<string>());
            Assert.Equal([reason], decision["reasons"]!.AsArray().Select(n => n!.GetValue<string>()));
            Assert.Empty(decision["fixedAlerts"]!.AsArray());
            Assert.Equal($"auto-sec Dependabot approvals\nRequests: 1. Approved: 0. Skipped: 1.\n\n- #101: skip ({reason})", result["summary"]!.GetValue<string>());
        }
        Assert.Equal(phase is "summary" or "submission-after-accept" ? 1 : 0, result["reviews"]!.AsArray().Count);
        Assert.Equal(phase is "parse-output" or "read-output" ? [] : [$"#101: {(phase == "summary" ? "approve " : $"skip {reason}")}"],
            result["info"]!.AsArray().Select(n => n!.GetValue<string>()));
    }

    [Theory]
    [RequiresTools(["node"])]
    [InlineData("dependencies")]
    [InlineData("devDependencies")]
    [InlineData("optionalDependencies")]
    [InlineData("peerDependencies")]
    [InlineData("overrides")]
    [InlineData("resolutions")]
    public async Task ApprovalRejectsUncheckedCompoundNpmSelectorAlongsideValidFix(string section)
    {
        var scenario = CreateApprovalScenario();
        var baseText = $"{{\n  \"{section}\": {{\n    \"foo\": \">=1 <2\"\n  }}\n}}\n";
        scenario["contents"]!["extension/package.json@base"] = baseText;
        scenario["contents"]!["extension/package.json@head"] = baseText;
        var unchanged = await RunHarnessAsync(scenario);
        Assert.Equal("approve", Assert.Single(unchanged["value"]!.AsArray())!["decision"]!.GetValue<string>());

        scenario["contents"]!["extension/package.json@head"] = baseText.Replace(">=1 <2", ">=1 <3", StringComparison.Ordinal);

        var result = await RunHarnessAsync(scenario);

        var decision = Assert.Single(result["value"]!.AsArray());
        Assert.Equal("skip", decision!["decision"]!.GetValue<string>());
        Assert.Contains("non-version-manifest-edit", decision["reasons"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Empty(result["reviews"]!.AsArray());
    }

    [Theory]
    [RequiresTools(["node"])]
    [InlineData("not-dependabot")]
    [InlineData("head-sha-mismatch")]
    [InlineData("wrong-base-branch")]
    [InlineData("actions-allow-list-required")]
    [InlineData("non-manifest-file-changed")]
    [InlineData("non-dependabot-commit")]
    [InlineData("non-dependabot-commit-unverified")]
    [InlineData("non-version-manifest-edit")]
    [InlineData("unbound-lockfile-artifact")]
    [InlineData("package-source-changed")]
    [InlineData("package-source-changed-other-org")]
    [InlineData("breaking-change")]
    [InlineData("breaking-change-grouped-transition")]
    [InlineData("breaking-change-unlisted-package")]
    [InlineData("breaking-change-consolidated-versions")]
    [InlineData("breaking-change-retained-version")]
    [InlineData("cooldown-not-satisfied")]
    [InlineData("cooldown-not-satisfied-unlisted-package")]
    [InlineData("too-many-version-changes")]
    [InlineData("malware-requires-review")]
    [InlineData("malware-requires-review-unlisted-package")]
    [InlineData("fixes-no-open-alert")]
    [InlineData("fixes-no-open-alert-other-directory")]
    [InlineData("fixes-no-open-alert-other-lockfile")]
    [InlineData("fixes-no-open-alert-vulnerable-copy-remains")]
    [InlineData("fixes-no-open-alert-later-vulnerable-range")]
    [InlineData("head-sha-mismatch-after-gates")]
    [InlineData("wrong-base-branch-after-gates")]
    [InlineData("not-open-after-gates")]
    [InlineData("checks-not-green-after-gates")]
    [InlineData("statuses-not-green-after-gates")]
    [InlineData("no-checks")]
    [InlineData("checks-not-green")]
    [InlineData("statuses-not-green")]
    [InlineData("statuses-not-green-on-later-page")]
    [InlineData("statuses-combined-state-not-green")]
    [InlineData("already-approved")]
    public async Task SkipsDependabotPrThatFailsGate(string scenarioName)
    {
        var expectedReason = scenarioName;
        var scenario = CreateApprovalScenario();
        var pr = scenario["pr"]!.AsObject();
        switch (scenarioName)
        {
            case "not-dependabot":
                pr["user_login"] = "someone";
                break;
            case "head-sha-mismatch":
                pr["head_sha"] = new string('b', 40);
                scenario["contents"]!["extension/yarn.lock@base"] = "resolved \"https://pkgs.dev.azure.com/x\"";
                break;
            case "wrong-base-branch":
                pr["base_ref"] = "release/13.3";
                break;
            case "actions-allow-list-required":
                pr["head_ref"] = "dependabot/github_actions/actions/checkout-4.2.0";
                break;
            case "non-manifest-file-changed":
                scenario["files"]!.AsArray().Add("NuGet.config");
                break;
            case "non-dependabot-commit":
                scenario["commits"] = new JsonArray(
                    new JsonObject { ["author_login"] = "dependabot[bot]", ["verified"] = true },
                    new JsonObject { ["author_login"] = "someone", ["verified"] = true });
                break;
            case "non-dependabot-commit-unverified":
                expectedReason = "non-dependabot-commit";
                scenario["commits"] = new JsonArray(new JsonObject { ["author_login"] = "dependabot[bot]", ["verified"] = false });
                break;
            case "non-version-manifest-edit":
                // A verified commit with Dependabot author metadata still cannot change a script.
                scenario["files"]!.AsArray().Add("extension/package.json");
                scenario["contents"]!["extension/package.json@base"] = "{\n  \"scripts\": { \"build\": \"tsc\" },\n  \"dependencies\": { \"lodash\": \"^4.17.20\" }\n}\n";
                scenario["contents"]!["extension/package.json@head"] = "{\n  \"scripts\": { \"build\": \"tsc && node x.js\" },\n  \"dependencies\": { \"lodash\": \"^4.17.21\" }\n}\n";
                break;
            case "package-source-changed":
                scenario["contents"]!["extension/yarn.lock@head"] = "resolved \"https://registry.example.com/lodash\"";
                break;
            case "package-source-changed-other-org":
                // Same host as the approved dnceng feeds, but another Azure DevOps organization.
                expectedReason = "package-source-changed";
                scenario["contents"]!["extension/yarn.lock@head"] = YarnLockEntry("lodash", "4.17.21", "https://pkgs.dev.azure.com/contoso/_packaging/feed/");
                break;
            case "breaking-change-grouped-transition":
                // The same target version reached from two starting versions: only the
                // second transition crosses a major version.
                expectedReason = "breaking-change";
                pr["head_ref"] = "dependabot/npm_and_yarn/npm_and_yarn-1a2b3c4d5e";
                pr["title"] = "Bump the npm_and_yarn group across 2 directories with 2 updates";
                pr["body"] = "Updates `lodash` from 4.17.20 to 4.17.21\nUpdates `lodash` from 3.10.1 to 4.17.21";
                break;
            case "breaking-change":
                pr["title"] = "Bump lodash from 4.17.20 to 5.0.0 in /extension";
                pr["body"] = "Bumps [lodash](https://github.com/lodash/lodash) from 4.17.20 to 5.0.0.";
                scenario["responses"]!["https://registry.npmjs.org/lodash"]!["body"]!["time"]!["5.0.0"] = "2026-09-01T00:00:00Z";
                break;
            case "cooldown-not-satisfied":
                scenario["responses"]!["https://registry.npmjs.org/lodash"]!["body"]!["time"]!["4.17.21"] = "2026-09-28T00:00:00Z";
                break;
            case "breaking-change-unlisted-package":
                // The lockfile also moves react across a major version that the PR body omits.
                expectedReason = "breaking-change";
                scenario["contents"]!["extension/yarn.lock@base"] = YarnLockEntry("lodash", "4.17.20") + YarnLockEntry("react", "17.0.2");
                scenario["contents"]!["extension/yarn.lock@head"] = YarnLockEntry("lodash", "4.17.21") + YarnLockEntry("react", "18.2.0");
                scenario["responses"]!["https://registry.npmjs.org/react"] = Response(new JsonObject { ["time"] = new JsonObject { ["18.2.0"] = "2026-09-01T00:00:00Z" } });
                break;
            case "breaking-change-consolidated-versions":
                // The lockfile collapses foo@1.0.0 and foo@2.0.0 into foo@2.1.0, moving the
                // 1.x consumers across a major version.
                expectedReason = "breaking-change";
                scenario["contents"]!["extension/yarn.lock@base"] = YarnLockEntry("lodash", "4.17.20") + YarnLockEntry("foo", "1.0.0") + YarnLockEntry("foo", "2.0.0");
                scenario["contents"]!["extension/yarn.lock@head"] = YarnLockEntry("lodash", "4.17.21") + YarnLockEntry("foo", "2.1.0");
                scenario["responses"]!["https://registry.npmjs.org/foo"] = Response(new JsonObject { ["time"] = new JsonObject { ["2.1.0"] = "2026-09-01T00:00:00Z" } });
                break;
            case "breaking-change-retained-version":
                // The lockfile drops foo@1.0.0 and keeps foo@2.1.0, so the 1.x consumers move
                // across a major version even though no new version is introduced.
                expectedReason = "breaking-change";
                scenario["contents"]!["extension/yarn.lock@base"] = YarnLockEntry("lodash", "4.17.20") + YarnLockEntry("foo", "1.0.0") + YarnLockEntry("foo", "2.1.0");
                scenario["contents"]!["extension/yarn.lock@head"] = YarnLockEntry("lodash", "4.17.21") + YarnLockEntry("foo", "2.1.0");
                scenario["responses"]!["https://registry.npmjs.org/foo"] = Response(new JsonObject { ["time"] = new JsonObject { ["2.1.0"] = "2026-09-01T00:00:00Z" } });
                break;
            case "malware-requires-review-unlisted-package":
                // The lockfile also changes minimist, which has an open malware alert, without
                // the PR body listing it.
                expectedReason = "malware-requires-review";
                scenario["contents"]!["extension/yarn.lock@base"] = YarnLockEntry("lodash", "4.17.20") + YarnLockEntry("minimist", "1.2.5");
                scenario["contents"]!["extension/yarn.lock@head"] = YarnLockEntry("lodash", "4.17.21") + YarnLockEntry("minimist", "1.2.8");
                scenario["responses"]!["https://registry.npmjs.org/minimist"] = Response(new JsonObject { ["time"] = new JsonObject { ["1.2.8"] = "2026-09-01T00:00:00Z" } });
                scenario["alerts"]!.AsArray().Add(new JsonObject
                {
                    ["number"] = 9,
                    ["ecosystem"] = "npm",
                    ["package"] = "minimist",
                    ["manifest_path"] = "extension/yarn.lock",
                    ["first_patched_version"] = null,
                });
                scenario["malwareNumbers"] = new JsonArray(9);
                break;
            case "cooldown-not-satisfied-unlisted-package":
                // The lockfile also bumps minimist to a release published three days ago.
                expectedReason = "cooldown-not-satisfied";
                scenario["contents"]!["extension/yarn.lock@base"] = YarnLockEntry("lodash", "4.17.20") + YarnLockEntry("minimist", "1.2.5");
                scenario["contents"]!["extension/yarn.lock@head"] = YarnLockEntry("lodash", "4.17.21") + YarnLockEntry("minimist", "1.2.8");
                scenario["responses"]!["https://registry.npmjs.org/minimist"] = Response(new JsonObject { ["time"] = new JsonObject { ["1.2.8"] = "2026-09-28T00:00:00Z" } });
                break;
            case "too-many-version-changes":
                var baseLock = new StringBuilder(YarnLockEntry("lodash", "4.17.20"));
                var headLock = new StringBuilder(YarnLockEntry("lodash", "4.17.21"));
                for (var i = 0; i < 50; i++)
                {
                    baseLock.Append(YarnLockEntry($"package-{i}", "1.0.0"));
                    headLock.Append(YarnLockEntry($"package-{i}", "1.0.1"));
                }
                scenario["contents"]!["extension/yarn.lock@base"] = baseLock.ToString();
                scenario["contents"]!["extension/yarn.lock@head"] = headLock.ToString();
                break;
            case "fixes-no-open-alert-vulnerable-copy-remains":
                // The top-level lodash is bumped but a nested copy stays on the vulnerable version.
                expectedReason = "fixes-no-open-alert";
                scenario["contents"]!["extension/yarn.lock@base"] = YarnLockEntry("lodash", "4.17.20");
                scenario["contents"]!["extension/yarn.lock@head"] = YarnLockEntry("lodash", "4.17.21") + YarnLockEntry("lodash", "4.17.20");
                break;
            case "fixes-no-open-alert":
                scenario["alerts"]![0]!["first_patched_version"] = "4.17.22";
                break;
            case "unbound-lockfile-artifact":
                // A verified commit with Dependabot author metadata keeps lodash@4.17.21 but
                // points it at another package's tarball on the same approved registry.
                scenario["contents"]!["extension/yarn.lock@head"] = YarnLockEntry("lodash", "4.17.21").Replace("/lodash/-/lodash-4.17.21.tgz", "/evil/-/evil-1.0.0.tgz", StringComparison.Ordinal);
                break;
            case "fixes-no-open-alert-later-vulnerable-range":
                // 4.17.21 is past the first patched version but inside a later range the
                // advisory also lists.
                expectedReason = "fixes-no-open-alert";
                scenario["alerts"]![0]!["advisory_ranges"] = new JsonArray("< 4.17.21", ">= 4.17.21, < 4.17.25");
                break;
            case "head-sha-mismatch-after-gates":
                // Dependabot pushes after the gates pass but before the review is submitted.
                expectedReason = "head-sha-mismatch";
                scenario["liveHeadSha"] = new string('c', 40);
                break;
            case "wrong-base-branch-after-gates":
                // The PR is retargeted away from main after the gates pass.
                expectedReason = "wrong-base-branch";
                scenario["liveBaseRef"] = "release/13.3";
                break;
            case "not-open-after-gates":
                // The PR is converted to draft after the gates pass.
                expectedReason = "not-open";
                scenario["liveDraft"] = true;
                break;
            case "checks-not-green-after-gates":
                // A check is re-run after the gates pass but before the review is submitted.
                expectedReason = "checks-not-green";
                scenario["liveCheckRuns"] = new JsonArray(new JsonObject { ["name"] = "tests", ["status"] = "in_progress", ["conclusion"] = null });
                break;
            case "statuses-not-green-after-gates":
                // A commit status turns red after the gates pass.
                expectedReason = "statuses-not-green";
                scenario["liveStatuses"] = new JsonArray(new JsonObject { ["context"] = "license/cla", ["state"] = "failure" });
                break;
            case "malware-requires-review":
                scenario["malwareNumbers"] = new JsonArray(7);
                break;
            case "fixes-no-open-alert-other-directory":
                // A grouped PR bumps lodash in extension/ and only touches another package in
                // playground/app/, so the playground lodash alert is not fixed.
                expectedReason = "fixes-no-open-alert";
                pr["head_ref"] = "dependabot/npm_and_yarn/npm_and_yarn-1a2b3c4d5e";
                pr["title"] = "Bump the npm_and_yarn group across 2 directories with 2 updates";
                pr["body"] = "Updates `lodash` from 4.17.20 to 4.17.21\nUpdates `minimist` from 1.2.5 to 1.2.8";
                scenario["files"]!.AsArray().Add("playground/app/yarn.lock");
                scenario["contents"]!["playground/app/yarn.lock@base"] = YarnLockEntry("lodash", "4.17.20") + YarnLockEntry("minimist", "1.2.5");
                scenario["contents"]!["playground/app/yarn.lock@head"] = YarnLockEntry("lodash", "4.17.20") + YarnLockEntry("minimist", "1.2.8");
                scenario["alerts"]![0]!["manifest_path"] = "playground/app/yarn.lock";
                scenario["responses"]!["https://registry.npmjs.org/minimist"] = Response(new JsonObject { ["time"] = new JsonObject { ["1.2.8"] = "2026-09-01T00:00:00Z" } });
                break;
            case "fixes-no-open-alert-other-lockfile":
                // The alert is on package-lock.json, but the PR changes only yarn.lock in the
                // same directory, so it does not prove the alerted manifest is fixed.
                expectedReason = "fixes-no-open-alert";
                scenario["alerts"]![0]!["manifest_path"] = "extension/package-lock.json";
                break;
            case "no-checks":
                scenario["checkRuns"] = new JsonArray();
                break;
            case "checks-not-green":
                scenario["checkRuns"]!.AsArray().Add(new JsonObject { ["name"] = "tests", ["status"] = "in_progress", ["conclusion"] = null });
                break;
            case "statuses-not-green":
                scenario["statuses"] = new JsonArray(new JsonObject { ["context"] = "license/cla", ["state"] = "pending" });
                break;
            case "statuses-not-green-on-later-page":
                // 30 green contexts fill the API's default page; the red one is on page two.
                expectedReason = "statuses-not-green";
                var statuses = new JsonArray();
                for (var i = 0; i < 30; i++)
                {
                    statuses.Add(new JsonObject { ["context"] = $"ok-{i}", ["state"] = "success" });
                }
                statuses.Add(new JsonObject { ["context"] = "license/cla", ["state"] = "failure" });
                scenario["statuses"] = statuses;
                break;
            case "statuses-combined-state-not-green":
                expectedReason = "statuses-not-green";
                scenario["statuses"] = new JsonArray(new JsonObject { ["context"] = "license/cla", ["state"] = "success" });
                scenario["combinedState"] = "failure";
                break;
            case "already-approved":
                scenario["reviews"] = new JsonArray(new JsonObject { ["user_login"] = "aspire-repo-bot[bot]", ["state"] = "APPROVED", ["commit_id"] = HeadSha });
                break;
        }

        var result = await RunHarnessAsync(scenario);

        var decision = Assert.Single(result["value"]!.AsArray());
        Assert.Equal("skip", decision!["decision"]!.GetValue<string>());
        // Gates run in a fixed order and keep evaluating, so a PR that fails an early gate
        // can also fail later ones (an actions PR has no package registry to check cooldown).
        Assert.Equal(expectedReason, decision["reasons"]![0]!.GetValue<string>());
        Assert.Empty(result["reviews"]!.AsArray());
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task IgnoresMalformedAndDuplicateApprovalRequests()
    {
        var result = await RunHarnessAsync(new JsonObject
        {
            ["mode"] = "call",
            ["fn"] = "readApprovalRequests",
            ["args"] = new JsonArray(new JsonObject
            {
                ["items"] = new JsonArray(
                    new JsonObject { ["type"] = "approve_dependabot_pr", ["pr_number"] = 5, ["head_sha"] = HeadSha },
                    new JsonObject { ["type"] = "approve_dependabot_pr", ["pr_number"] = 5, ["head_sha"] = HeadSha },
                    new JsonObject { ["type"] = "approve_dependabot_pr", ["pr_number"] = 6, ["head_sha"] = "main" },
                    new JsonObject { ["type"] = "approve_dependabot_pr", ["pr_number"] = -1, ["head_sha"] = HeadSha },
                    new JsonObject { ["type"] = "create_pull_request", ["pr_number"] = 7, ["head_sha"] = HeadSha }),
            }),
        });

        Assert.Equal(
            """[{"prNumber":5,"headSha":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}]""",
            result["value"]!.ToJsonString());
    }

    [Theory]
    [RequiresTools(["node"])]
    [InlineData("4.17.20", "4.17.21", false)]
    [InlineData("4.17.20", "4.18.0", false)]
    [InlineData("4.17.20", "5.0.0", true)]
    [InlineData("0.3.1", "0.3.2", false)]
    [InlineData("0.3.1", "0.4.0", true)]
    [InlineData("0.0.3", "0.0.4", false)]
    [InlineData("2.0.0", "1.9.9", true)]
    [InlineData("1.0.0", "not-a-version", true)]
    [InlineData("v1.2.3", "v1.2.4", false)]
    public async Task ClassifiesBreakingChanges(string from, string to, bool expected)
    {
        var result = await RunHarnessAsync(new JsonObject
        {
            ["mode"] = "call",
            ["fn"] = "isBreakingChange",
            ["args"] = new JsonArray(from, to),
        });

        Assert.Equal(expected, result["value"]!.GetValue<bool>());
    }

    [Theory]
    [RequiresTools(["node"])]
    [InlineData("1.2.4", "< 1.2.5", true)]
    [InlineData("1.2.5", "< 1.2.5", false)]
    [InlineData("1.3.2", ">= 1.3.0, < 1.3.4", true)]
    [InlineData("1.3.4", ">= 1.3.0, < 1.3.4", false)]
    [InlineData("2.0.0", "= 2.0.0", true)]
    [InlineData("2.0.1", "<= 2.0.0", false)]
    [InlineData("1.0.0", "~> 1.0", true)]
    public async Task EvaluatesAdvisoryVulnerableRanges(string version, string range, bool expected)
    {
        var result = await RunHarnessAsync(new JsonObject
        {
            ["mode"] = "call",
            ["fn"] = "inVulnerableRanges",
            ["args"] = new JsonArray(version, new JsonArray(range)),
        });

        Assert.Equal(expected, result["value"]!.GetValue<bool>());
    }

    [Theory]
    [RequiresTools(["node"])]
    [InlineData("package.json", "dependencies", "\"lodash\": \"^4.17.20\"", "\"lodash\": \"^4.17.21\"", true)]
    [InlineData("package.json", "devDependencies", "\"lodash\": \"1.2.9\"", "\"lodash\": \"1.2.10\"", true)]
    [InlineData("package.json", "overrides", "\"alias\": \"npm:lodash@4.17.20\"", "\"alias\": \"npm:lodash@4.17.21\"", true)]
    [InlineData("Directory.Packages.props", null, "<PackageVersion Include=\"X\" Version=\"9.0.4\" />", "<PackageVersion Include=\"X\" Version=\"9.0.5-rc.1\" />", true)]
    [InlineData("pyproject.toml", "project:dependencies", "  \"requests>=2.31.0\",", "  \"requests>=2.32.3\",", true)]
    [InlineData("pyproject.toml", "project.optional-dependencies:socks", "  \"requests>=2.31.0\",", "  \"requests>=2.32.3\",", true)]
    [InlineData("pyproject.toml", "dependency-groups:dev", "  \"requests>=2.31.0\",", "  \"requests>=2.32.3\",", true)]
    [InlineData("pyproject.toml", "tool.uv:dev-dependencies", "  \"requests>=2.31.0\",", "  \"requests>=2.32.3\",", true)]
    [InlineData("pyproject.toml", "tool.example:arguments", "  \"requests>=2.31.0\",", "  \"requests>=2.32.3\",", false)]
    [InlineData("pyproject.toml", "build-system:requires", "  \"setuptools>=64\",", "  \"setuptools>=65\",", false)]
    [InlineData("pyproject.toml", null, "  \"requests>=2.31.0\",", "  \"requests>=2.32.3\",", false)]
    [InlineData("pyproject.toml", "project:dependencies", "  \"requests[socks]>=2.31.0; python_version >= '3.9'\",", "  \"requests[socks]>=2.32.3; python_version >= '3.9'\",", true)]
    [InlineData("package.json", "scripts", "\"build\": \"tsc\"", "\"build\": \"tsc && node x.js\"", false)]
    [InlineData("package.json", "scripts", "\"build\": \"node script-1.js\"", "\"build\": \"node script-2.js\"", false)]
    [InlineData("package.json", "scripts", "\"build\": \"vite --mode=1\"", "\"build\": \"vite --mode=2\"", false)]
    [InlineData("package.json", "scripts", "\"build\": \"1.0.0\"", "\"build\": \"1.0.1\"", false)]
    [InlineData("package.json", "dependencies", "\"lodash\": \"^4.17.20\"", "\"lodash\": \"^4.17.20\",\n    \"postinstall\": \"node x.js\"", false)]
    [InlineData("package.json", "dependencies", "\"foo\": \">=1.0 <2.0\"", "\"foo\": \">=1.1 <3.0\"", false)]
    [InlineData("package.json", "dependencies", "\"foo\": \">=1 <2\"", "\"foo\": \">=1 <3\"", false)]
    [InlineData("package.json", "dependencies", "\"foo\": \">=1 <2\"", "\"foo\": \">=1.1 <2\"", false)]
    [InlineData("package.json", "overrides", "\"parent\": {\n      \".\": \">=1 <2\"\n    }", "\"parent\": {\n      \".\": \">=1 <3\"\n    }", false)]
    [InlineData("package.json", "overrides", "\"parent\": {\n      \".\": \"^1.0.0\"\n    }", "\"parent\": {\n      \".\": \"^1.0.1\"\n    }", true)]
    [InlineData("package.json", "resolutions", "\"**/foo\": \">=1 <2\"", "\"**/foo\": \">=1 <3\"", false)]
    [InlineData("package.json", "dependencies", "\"foo\": \">=1 <2\",\n    \"lodash\": \"^4.17.20\"", "\"foo\": \">=1 <2\",\n    \"lodash\": \"^4.17.21\"", true)]
    [InlineData("package.json", "dependencies", "\"lodash\": \"~4.17.20\"", "\"lodash\": \"~4.17.21\"", true)]
    [InlineData("pyproject.toml", null, "command = \"tool --level=1\"", "command = \"tool --level=2\"", false)]
    [InlineData("Directory.Packages.props", null, "<Exec Command=\"tool --level=1\" />", "<Exec Command=\"tool --level=2\" />", false)]
    [InlineData("yarn.lock", null, "  version \"1.0.0\"", "  version \"1.0.1\"", false)]
    [InlineData("package.json", "overrides", "\"x\": \"npm:1@1.0.0\"", "\"x\": \"npm:2@1.0.0\"", false)]
    [InlineData("package.json", "dependencies", "\"v1\": \"1.0.0\"", "\"v2\": \"1.0.0\"", false)]
    [InlineData("pyproject.toml", null, "  \"pkg1>=1.0.0\",", "  \"pkg2>=1.0.0\",", false)]
    [InlineData("pyproject.toml", null, "  \"requests>=2.31.0; python_version >= '3.9'\",", "  \"requests>=2.31.0; python_version >= '3.10'\",", false)]
    [InlineData("Directory.Packages.props", null, "<PackageVersion Include=\"X1\" Version=\"9.0.4\" />", "<PackageVersion Include=\"X2\" Version=\"9.0.4\" />", false)]
    public async Task DetectsVersionOnlyManifestEdits(string path, string? section, string baseLine, string headLine, bool expected)
    {
        // package.json lines are wrapped in a document so the JSON-path check sees them
        // inside the named top-level section. pyproject.toml sections are `table:key`, and the
        // line is wrapped in that multi-line array.
        string Wrap(string line) => section switch
        {
            null => line,
            _ when path == "pyproject.toml" => $"[{section.Split(':')[0]}]\n{section.Split(':')[1]} = [\n{line}\n]\n",
            _ => $"{{\n  \"{section}\": {{\n    {line}\n  }}\n}}",
        };

        var result = await RunHarnessAsync(new JsonObject
        {
            ["mode"] = "call",
            ["fn"] = "isVersionOnlyEdit",
            ["args"] = new JsonArray(path, Wrap(baseLine), Wrap(headLine)),
        });

        Assert.Equal(expected, result["value"]!.GetValue<bool>());
    }

    private const string PackageJsonBase = "{\n  \"name\": \"x\",\n  \"scripts\": {\n    \"build\": \"vite --mode=1\",\n    \"preinstall\": \"1.0.0\"\n  },\n  \"dependencies\": {\n    \"lodash\": \"^4.17.20\"\n  }\n}\n";
    private const string PackagesPropsBase = "<Project>\n  <ItemGroup>\n    <PackageVersion Include=\"X\" Version=\"9.0.4\" />\n  </ItemGroup>\n</Project>\n";
    private const string PyprojectBase = "[tool.x]\ncommand = \"tool --level=1\"\nx = 1\n";
    private const string PackageLockBase = "{\n  \"packages\": {\n    \"node_modules/lodash\": {\n      \"version\": \"4.17.20\",\n      \"resolved\": \"https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-public-npm/npm/registry/lodash/-/lodash-4.17.20.tgz\"\n    }\n  }\n}\n";
    private const string YarnLockBase = "lodash@^4.17.20:\n  version \"4.17.20\"\n  resolved \"https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-public-npm/npm/registry/lodash/-/lodash-4.17.20.tgz#abc\"\n";
    private const string NpmRegistry = "https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-public-npm/npm/registry/";
    private const string LockVersion20 = "      \"version\": \"4.17.20\",\n      \"resolved\": \"" + NpmRegistry + "lodash/-/lodash-4.17.20.tgz\"";
    private const string LockVersion21 = "      \"version\": \"4.17.21\",\n      \"resolved\": \"" + NpmRegistry + "lodash/-/lodash-4.17.21.tgz\"";
    private const string YarnVersion20 = "  version \"4.17.20\"\n  resolved \"" + NpmRegistry + "lodash/-/lodash-4.17.20.tgz#abc\"";
    private const string YarnVersion21 = "  version \"4.17.21\"\n  resolved \"" + NpmRegistry + "lodash/-/lodash-4.17.21.tgz#abc\"";

    [Theory]
    [RequiresTools(["node"])]
    [InlineData("extension/package.json", PackageJsonBase, "    \"lodash\": \"^4.17.20\"", "    \"lodash\": \"^4.17.21\"", "workspace", "")]
    [InlineData("extension/package.json", PackageJsonBase, "    \"lodash\": \"^4.17.20\"", "    \"lodash\": \"^4.17.21\"", "branch", "")]
    [InlineData("Directory.Packages.props", PackagesPropsBase, "    <PackageVersion Include=\"X\" Version=\"9.0.4\" />", "    <PackageVersion Include=\"X\" Version=\"9.0.5\" />", "workspace", "")]
    [InlineData("extension/package-lock.json", PackageLockBase, LockVersion20, LockVersion21, "workspace", "")]
    [InlineData("app/yarn.lock", YarnLockBase, YarnVersion20, YarnVersion21, "workspace", "")]
    // The version moves but the entry keeps the vulnerable tarball.
    [InlineData("extension/package-lock.json", PackageLockBase, "      \"version\": \"4.17.20\",", "      \"version\": \"4.17.21\",", "workspace", "unbound-lockfile-artifact")]
    [InlineData("app/yarn.lock", YarnLockBase, "  version \"4.17.20\"", "  version \"4.17.21\"", "workspace", "unbound-lockfile-artifact")]
    [InlineData("extension/package-lock.json", PackageLockBase, LockVersion20, "      \"version\": \"4.17.21\",\n      \"resolved\": \"" + NpmRegistry + "evil/-/evil-1.0.0.tgz\"", "workspace", "unbound-lockfile-artifact")]
    [InlineData("app/yarn.lock", YarnLockBase, YarnVersion20, "  version \"4.17.21\"\n  resolved \"" + NpmRegistry + "lodash/-/lodash-4.17.21.tgz#abc\" # alert 42", "workspace", "lockfile-comment")]
    [InlineData("app/yarn.lock", YarnLockBase, YarnVersion20, "  # fixes 42\n" + YarnVersion21, "workspace", "lockfile-comment")]
    [InlineData("extension/package-lock.json", PackageLockBase, LockVersion20, "      \"version\": \"4.17.21\",\n      \"note\": \"GHSA-xxxx\",\n      \"resolved\": \"" + NpmRegistry + "lodash/-/lodash-4.17.21.tgz\"", "workspace", "forbidden-public-text")]
    [InlineData("extension/package-lock.json", PackageLockBase, LockVersion20, "      \"version\": \"4.17.21\",\n      \"note\": \"alert 42: prototype pollution in lodash\",\n      \"resolved\": \"" + NpmRegistry + "lodash/-/lodash-4.17.21.tgz\"", "workspace", "unsupported-lockfile-text")]
    [InlineData("extension/package.json", PackageJsonBase, "    \"lodash\": \"^4.17.20\"", "    \"lodash\": \"^4.17.21\"", "stale", "non-version-manifest-edit")]
    [InlineData("extension/package.json", PackageJsonBase, "    \"preinstall\": \"1.0.0\"", "    \"preinstall\": \"1.0.1\"", "workspace", "non-version-manifest-edit")]
    [InlineData("extension/package.json", PackageJsonBase, "    \"build\": \"vite --mode=1\",", "    \"build\": \"vite --mode=2\",", "workspace", "non-version-manifest-edit")]
    [InlineData("extension/package.json", PackageJsonBase, "  \"scripts\": {", "  \"scripts\": {\n    \"postinstall\": \"node x.js\",", "workspace", "non-version-manifest-edit")]
    [InlineData("extension/package.json", "{\n  \"dependencies\": {\n    \"foo\": \">=1 <2\"\n  }\n}\n", "    \"foo\": \">=1 <2\"", "    \"foo\": \">=1 <3\"", "workspace", "non-version-manifest-edit")]
    [InlineData("pyproject.toml", PyprojectBase, "command = \"tool --level=1\"", "command = \"tool --level=2\"", "workspace", "non-version-manifest-edit")]
    [InlineData("app/yarn.lock", YarnLockBase, YarnVersion20, YarnVersion21, "stale", "unreconstructable-file-diff")]
    [InlineData("extension/package-lock.json", PackageLockBase, LockVersion20, "      \"version\": \"4.17.21\",\n      \"resolved\": \"https://evil.example/lodash/-/lodash-4.17.21.tgz\"", "workspace", "new-package-source")]
    [InlineData("extension/package-lock.json", PackageLockBase, LockVersion20, "      \"version\": \"4.17.21\",\n      \"resolved\": \"https://evil.example/lodash/-/lodash-4.17.21.tgz\"", "workspace-with-stale-branch", "new-package-source")]
    // Version policy re-checked from the rebuilt files: lodash 4.17.22 is inside the
    // cooldown, 5.0.0 crosses a major version, and NuGet X 9.0.6 is not on an approved feed.
    [InlineData("extension/package.json", PackageJsonBase, "    \"lodash\": \"^4.17.20\"", "    \"lodash\": \"^4.17.22\"", "workspace", "cooldown-not-satisfied")]
    [InlineData("extension/package.json", PackageJsonBase, "    \"lodash\": \"^4.17.20\"", "    \"lodash\": \"^5.0.0\"", "workspace", "breaking-change")]
    // A version the registry cannot describe fails closed.
    [InlineData("extension/package.json", PackageJsonBase, "    \"lodash\": \"^4.17.20\"", "    \"lodash\": \"^4.17.23\"", "workspace", "cooldown-not-satisfied")]
    [InlineData("Directory.Packages.props", PackagesPropsBase, "    <PackageVersion Include=\"X\" Version=\"9.0.4\" />", "    <PackageVersion Include=\"X\" Version=\"9.0.6\" />", "workspace", "nuget-not-on-approved-feed")]
    public async Task PatchContentGateChecksEveryChangedLine(string path, string baseText, string oldLine, string newLine, string baseLocation, string expectedReason)
    {
        var patch = ReplacementPatch(path, baseText, oldLine, newLine);

        const string flat = "https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-public/nuget/v3/flat2/";
        var workspaceFiles = new JsonObject
        {
            ["NuGet.config"] = "<configuration>\n  <packageSources>\n    <add key=\"public\" value=\"https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-public/nuget/v3/index.json\" />\n  </packageSources>\n</configuration>\n",
        };
        var request = new JsonObject
        {
            ["mode"] = "patch-gate",
            ["patchFiles"] = new JsonObject { ["aw-auto-sec-security-updates.patch"] = patch },
            ["workspaceFiles"] = workspaceFiles,
            ["now"] = "2026-09-01T00:00:00Z",
            ["responses"] = new JsonObject
            {
                ["https://registry.npmjs.org/lodash"] = Response(new JsonObject
                {
                    ["time"] = new JsonObject { ["4.17.21"] = "2026-08-01T00:00:00Z", ["4.17.22"] = "2026-08-30T00:00:00Z", ["5.0.0"] = "2026-08-01T00:00:00Z" },
                }),
                ["https://api.nuget.org/v3/registration5-gz-semver2/x/9.0.5.json"] = Response(new JsonObject { ["published"] = "2026-08-01T00:00:00Z" }),
                ["https://api.nuget.org/v3/registration5-gz-semver2/x/9.0.6.json"] = Response(new JsonObject { ["published"] = "2026-08-01T00:00:00Z" }),
                ["https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-public/nuget/v3/index.json"] = ServiceIndex(flat),
                [$"{flat}x/index.json"] = Response(new JsonObject { ["versions"] = new JsonArray("9.0.4", "9.0.5") }),
            },
        };
        switch (baseLocation)
        {
            case "workspace":
                workspaceFiles[path] = baseText;
                break;
            case "branch":
                request["branchFiles"] = new JsonObject { [path] = baseText };
                break;
            case "workspace-with-stale-branch":
                // The auto-sec branch copy already references the new source, but the patch
                // is based on the workspace copy, so the branch copy must not authorize it.
                workspaceFiles[path] = baseText;
                request["branchFiles"] = new JsonObject { [path] = baseText + "{\"resolved\": \"https://evil.example/lodash/-/lodash-4.17.21.tgz\"}\n" };
                break;
            case "stale":
                // The checked-out file no longer matches the blob the patch was made from.
                workspaceFiles[path] = baseText + "\n";
                break;
        }

        var result = await RunHarnessAsync(request);
        var violations = result["value"]!["violations"]!.AsArray().Select(v => $"{v!["path"]} {v["reason"]}").ToArray();
        if (expectedReason.Length == 0)
        {
            Assert.Empty(violations);
            Assert.Empty(result["failures"]!.AsArray());
        }
        else
        {
            Assert.Equal([$"{path} {expectedReason}"], violations);
            Assert.Single(result["failures"]!.AsArray());
        }
    }

        private static string ReplacementPatch(string path, string baseText, string oldLine, string newLine)
        {
            // Replacement hunk with a context line on each side, applied at the real position
            // in baseText and labeled with the exact base's git blob ID.
            var baseLines = baseText.Split('\n');
            var removed = oldLine.Split('\n');
            var at = Array.IndexOf(baseLines, removed[0]);
            Assert.True(at > 0 && at + removed.Length < baseLines.Length);
            Assert.Equal(removed, baseLines[at..(at + removed.Length)]);
            var added = newLine.Split('\n');
            var hunk = string.Join('\n', [$" {baseLines[at - 1]}", .. removed.Select(line => $"-{line}"), .. added.Select(line => $"+{line}"), $" {baseLines[at + removed.Length]}"]);
            return $"From 0000000000000000000000000000000000000000 Mon Sep 17 00:00:00 2001\nSubject: [PATCH] update\n\n---\ndiff --git a/{path} b/{path}\nindex {GitBlobId(baseText)[..7]}..2222222 100644\n--- a/{path}\n+++ b/{path}\n@@ -{at},{removed.Length + 2} +{at},{added.Length + 2} @@\n{hunk}\n-- \n2.43.0\n";
        }

        [Theory]
        [RequiresTools(["node"])]
        [InlineData("cooldown-not-satisfied", true)]
        [InlineData("cooldown-not-satisfied", false)]
        [InlineData("breaking-change", true)]
        [InlineData("breaking-change", false)]
        [InlineData("nuget-not-on-approved-feed", true)]
        [InlineData("nuget-not-on-approved-feed", false)]
        public async Task PatchContentGateValidatesOverlappingArtifactsIndependently(string reason, bool invalidFirst)
        {
            var nuget = reason == "nuget-not-on-approved-feed";
            var path = nuget ? "Directory.Packages.props" : "extension/package.json";
            var baseText = nuget ? PackagesPropsBase : PackageJsonBase;
            var oldLine = nuget ? "    <PackageVersion Include=\"X\" Version=\"9.0.4\" />" : "    \"lodash\": \"^4.17.20\"";
            var safeLine = nuget ? oldLine.Replace("9.0.4", "9.0.5") : oldLine.Replace("4.17.20", "4.17.21");
            var invalidLine = nuget ? oldLine.Replace("9.0.4", "9.0.6")
                : oldLine.Replace("4.17.20", reason == "breaking-change" ? "5.0.0" : "4.17.22");
            const string flat = "https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-public/nuget/v3/flat2/";
            var result = await RunHarnessAsync(new JsonObject
            {
                ["mode"] = "patch-gate",
                ["patchFiles"] = new JsonObject
                {
                    ["aw-a.patch"] = ReplacementPatch(path, baseText, oldLine, invalidFirst ? invalidLine : safeLine),
                    ["aw-z.patch"] = ReplacementPatch(path, baseText, oldLine, invalidFirst ? safeLine : invalidLine),
                },
                ["workspaceFiles"] = new JsonObject
                {
                    [path] = baseText,
                    ["NuGet.config"] = "<configuration><packageSources><add key=\"public\" value=\"https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-public/nuget/v3/index.json\" /></packageSources></configuration>",
                },
                ["now"] = "2026-09-01T00:00:00Z",
                ["responses"] = new JsonObject
                {
                    ["https://registry.npmjs.org/lodash"] = Response(new JsonObject
                    {
                        ["time"] = new JsonObject { ["4.17.21"] = "2026-08-01T00:00:00Z", ["4.17.22"] = "2026-08-30T00:00:00Z", ["5.0.0"] = "2026-08-01T00:00:00Z" },
                    }),
                    ["https://api.nuget.org/v3/registration5-gz-semver2/x/9.0.5.json"] = Response(new JsonObject { ["published"] = "2026-08-01T00:00:00Z" }),
                    ["https://api.nuget.org/v3/registration5-gz-semver2/x/9.0.6.json"] = Response(new JsonObject { ["published"] = "2026-08-01T00:00:00Z" }),
                    ["https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-public/nuget/v3/index.json"] = ServiceIndex(flat),
                    [$"{flat}x/index.json"] = Response(new JsonObject { ["versions"] = new JsonArray("9.0.4", "9.0.5") }),
                },
            });

            Assert.Equal([$"{path} {reason}"], result["value"]!["violations"]!.AsArray().Select(v => $"{v!["path"]} {v["reason"]}"));
            Assert.Single(result["failures"]!.AsArray());
        }

    [Fact]
    [RequiresTools(["node"])]
    public async Task PatchContentGateRejectsUnsupportedTransportsAndFileDiffs()
    {
        var newFile = "diff --git a/extension/package.json b/extension/package.json\nnew file mode 100644\n--- /dev/null\n+++ b/extension/package.json\n@@ -0,0 +1 @@\n+{}\n";
        var result = await RunHarnessAsync(new JsonObject
        {
            ["mode"] = "patch-gate",
            ["patchFiles"] = new JsonObject
            {
                ["aw-auto-sec-security-updates.bundle"] = "bundle",
                ["aw-auto-sec-security-updates.patch"] = newFile,
            },
        });

        Assert.Equal(
            ["aw-auto-sec-security-updates.bundle bundle-transport", "extension/package.json unsupported-file-diff"],
            result["value"]!["violations"]!.AsArray().Select(v => $"{v!["path"]} {v["reason"]}"));
    }

    [Theory]
    [RequiresTools(["node"])]
    // A new transitive lockfile entry repeats a delivery host the file already uses.
    [InlineData("      \"resolved\": \"https://registry.example.com/x/-/x-1.0.0.tgz\"", "{\n  \"resolved\": \"https://registry.example.com/y/-/y-1.0.0.tgz\"\n}\n", "")]
    [InlineData("      \"resolved\": \"https://registry.example.com/x/-/x-1.0.0.tgz\"", null, "new-package-source")]
    // Funding links are not delivery sources, so they neither need nor grant authorization.
    [InlineData("      \"funding\": { \"url\": \"https://opencollective.com/x\" },", null, "")]
    [InlineData("      \"resolved\": \"https://github.com/o/x/archive/v1.tgz\"", "{\n  \"funding\": {\n    \"url\": \"https://github.com/sponsors/y\"\n  }\n}\n", "new-package-source")]
    [InlineData("      \"resolved\": \"https://github.com/o/x/archive/v1.tgz\"", "{\n  \"funding\": \"https://github.com/sponsors/y\"\n}\n", "new-package-source")]
    // A metadata object opened in one block must not hide a source added in a later block.
    [InlineData("      \"funding\": {\n \n      \"resolved\": \"https://evil.example/x.tgz\"", null, "new-package-source")]
    public async Task PatchContentGateAllowsOnlyDeliverySourcesAlreadyInTheCheckedOutFile(string addedLines, string? baseText, string expectedReason)
    {
        var lines = addedLines.Split('\n');
        var body = string.Join('\n', lines.Select(line => line.Trim().Length == 0 ? line : $"+{line}"));
        var contextCount = lines.Count(line => line.Trim().Length == 0);
        var patch = $"diff --git a/extension/package-lock.json b/extension/package-lock.json\nindex {(baseText is null ? "1111111" : GitBlobId(baseText)[..7])}..2222222 100644\n--- a/extension/package-lock.json\n+++ b/extension/package-lock.json\n@@ -1,{contextCount + 1} +1,{lines.Length + 1} @@\n   \"packages\": {{\n{body}\n";
        var request = new JsonObject
        {
            ["mode"] = "patch-gate",
            ["patchFiles"] = new JsonObject { ["aw-auto-sec-security-updates.patch"] = patch },
        };
        if (baseText is not null)
        {
            request["workspaceFiles"] = new JsonObject { ["extension/package-lock.json"] = baseText };
        }

        var result = await RunHarnessAsync(request);

        // These fragments isolate delivery-source authorization; full reconstruction and
        // the independently tested publication grammar reject them for other reasons.
        var violations = result["value"]!["violations"]!.AsArray()
            .Select(v => $"{v!["path"]} {v["reason"]}")
            .Where(v => v != "extension/package-lock.json unreconstructable-file-diff" && v != "extension/package-lock.json unsupported-lockfile-text")
            .ToArray();
        Assert.Equal(expectedReason.Length == 0 ? [] : [$"extension/package-lock.json {expectedReason}"], violations);
    }

    private const string PublicTextBase = "{\n  \"packages\": {\n    \"node_modules/lodash\": {\n      \"version\": \"4.17.20\",\n      \"dev\": true\n    },\n\n\n    \"node_modules/@types/node\": {\n      \"version\": \"22.0.0\",\n      \"dev\": true\n    }\n  }\n}\n";

    // Preserve mbox's "-- " signature separator without trailing source whitespace.
    private static string PublicTextPatch => """
        From 1111111111111111111111111111111111111111 Mon Sep 17 00:00:00 2001
        From: "github-actions[bot]" <github-actions[bot]@users.noreply.github.com>
        Date: Sat, 3 Oct 2026 09:00:00 +0000
        Subject: [PATCH] Update dependencies

        lodash 4.17.20 -> 4.17.21
        @types/node 22.0.0 -> 22.1.0
        ---
         extension/package-lock.json | 4 ++--
         1 file changed, 2 insertions(+), 2 deletions(-)

        diff --git a/extension/package-lock.json b/extension/package-lock.json
        index 1111111..2222222 100644
        --- a/extension/package-lock.json
        +++ b/extension/package-lock.json
        @@ -3,3 +3,3 @@
             "node_modules/lodash": {
        -      "version": "4.17.20",
        +      "version": "4.17.21",
               "dev": true
        @@ -9,3 +9,3 @@
             "node_modules/@types/node": {
        -      "version": "22.0.0",
        +      "version": "22.1.0",
               "dev": true
        --
        2.43.0

        """.ReplaceLineEndings("\n")
        .Replace("\n--\n", "\n-- \n", StringComparison.Ordinal)
        .Replace("index 1111111..", $"index {GitBlobId(PublicTextBase)[..7]}..", StringComparison.Ordinal);

    private const string PublicTextBody = """
        This is an automated pull request created by the auto-sec workflow.

        It updates the following dependencies to newer, non-breaking versions that have
        been published for at least 7 days and resolve from the existing package sources:

        | Package | Manifest | From | To |
        | --- | --- | --- | --- |
        | lodash | extension/package-lock.json | 4.17.20 | 4.17.21 |
        | `@types/node` | extension/package-lock.json | 22.0.0 | 22.1.0 |

        This table records the initial update batch. Subsequent automated updates are
        listed in the commit history; review the full branch diff for the current changes.

        No package sources or feeds were changed. Please review the lockfile diffs and
        CI results before merging.
        """;

    [Fact]
    [RequiresTools(["node"])]
    public async Task PublicTextGateRequiresInitialBatchQualification()
    {
        var output = PublicOutputFixture("create_pull_request");
        output["body"] = PublicTextBody.ReplaceLineEndings("\n").Replace(
            "This table records the initial update batch. Subsequent automated updates are\n"
            + "listed in the commit history; review the full branch diff for the current changes.\n\n",
            "", StringComparison.Ordinal);
        var result = await RunHarnessAsync(new JsonObject
        {
            ["mode"] = "public-text-gate",
            ["useCheckedOutBases"] = true,
            ["workspaceFiles"] = new JsonObject { ["extension/package-lock.json"] = PublicTextBase },
            ["agentItems"] = new JsonArray(output),
            ["patchFiles"] = new JsonObject { ["aw-auto-sec-security-updates.patch"] = PublicTextPatch },
        });

        Assert.Equal(["create_pull_request.body non-template-text"],
            result["value"]!["violations"]!.AsArray().Select(v => $"{v!["source"]} {v["reason"]}"));
    }

    [Theory]
    [RequiresTools(["node"])]
    [InlineData("package-lock.json", true)]
    [InlineData("extension/package-lock.json", true)]
    [InlineData("caf\u00e9/package-lock.json", true)]
    [InlineData("./extension/package-lock.json", false)]
    [InlineData("note-42/../extension/package-lock.json", false)]
    [InlineData("../extension/package-lock.json", false)]
    [InlineData("/extension/package-lock.json", false)]
    [InlineData("extension//package-lock.json", false)]
    [InlineData("extension\\package-lock.json", false)]
    [InlineData(".github/../extension/package-lock.json", false)]
    public async Task ManifestAdmissionRequiresCanonicalRepositoryPaths(string path, bool expected)
    {
        var result = await RunHarnessAsync(new JsonObject
        {
            ["mode"] = "call",
            ["fn"] = "isAllowedManifest",
            ["args"] = new JsonArray(path),
        });

        Assert.Equal(expected, result["value"]!.GetValue<bool>());
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task PublicTextGateRejectsNormalizedWorkspacePathAliases()
    {
        const string alias = "note-42/../extension/package-lock.json";
        var output = PublicOutputFixture("create_pull_request");
        output["body"] = PublicTextBody.Replace("extension/package-lock.json", alias, StringComparison.Ordinal);
        var result = await RunHarnessAsync(new JsonObject
        {
            ["mode"] = "public-text-gate",
            ["useCheckedOutBases"] = true,
            ["workspaceFiles"] = new JsonObject { ["extension/package-lock.json"] = PublicTextBase },
            ["agentItems"] = new JsonArray(output),
            ["patchFiles"] = new JsonObject
            {
                ["aw-auto-sec-security-updates.patch"] = PublicTextPatch.Replace("extension/package-lock.json", alias, StringComparison.Ordinal),
            },
        });

        Assert.Equal(
            ["create_pull_request.body non-template-text", "aw-auto-sec-security-updates.patch commit-row-not-in-patch"],
            result["value"]!["violations"]!.AsArray().Select(v => $"{v!["source"]} {v["reason"]}"));
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task AgentOutputScrubRejectsPathAliasesBeforePublication()
    {
        const string alias = "note-42/../extension/package-lock.json";
        var output = PublicOutputFixture("create_pull_request");
        output["body"] = PublicTextBody.Replace("extension/package-lock.json", alias, StringComparison.Ordinal);
        var result = await RunHarnessAsync(new JsonObject
        {
            ["mode"] = "agent-scrub",
            ["publicationConditions"] = CompiledPublicationConditions(),
            ["outputLines"] = new JsonArray(output.ToJsonString()),
            ["patchFiles"] = new JsonObject
            {
                ["aw-auto-sec-security-updates.patch"] = PublicTextPatch.Replace("extension/package-lock.json", alias, StringComparison.Ordinal),
            },
            ["baseFiles"] = new JsonObject { [alias] = PublicTextBase },
        });

        Assert.Equal(
            ["create_pull_request.body non-template-text", "aw-auto-sec-security-updates.patch commit-row-not-in-patch", "aw-auto-sec-security-updates.patch unsupported-file-diff", "aw-auto-sec-security-updates.patch disallowed-patch-file"],
            result["value"]!["violations"]!.AsArray().Select(v => $"{v!["source"]} {v["reason"]}"));
        Assert.Empty(result["outputs"]!.AsArray());
        Assert.Empty(result["remaining"]!.AsArray());
        Assert.Null(result["stepOutputs"]!["publication_ready"]);
        Assert.Empty(result["publications"]!.AsArray());
    }

    [Theory]
    [RequiresTools(["node"])]
    [InlineData("valid-create", "")]
    [InlineData("valid-create-prefixed-title", "")]
    [InlineData("valid-push", "")]
    [InlineData("title-free-text", "create_pull_request.title non-template-text")]
    [InlineData("body-extra-paragraph", "create_pull_request.body non-template-text")]
    [InlineData("body-free-text-row", "create_pull_request.body non-template-text")]
    [InlineData("body-forbidden-token-row", "create_pull_request.body non-template-text")]
    [InlineData("body-without-rows", "create_pull_request.body non-template-text")]
    [InlineData("body-row-not-in-patch", "create_pull_request.body row-not-in-patch")]
    [InlineData("push-message-free-text", "push_to_pull_request_branch.message non-template-text")]
    [InlineData("commit-subject-free-text", "aw-auto-sec-security-updates.patch non-template-commit-message")]
    [InlineData("commit-body-free-text", "aw-auto-sec-security-updates.patch non-template-commit-message")]
    [InlineData("commit-author", "aw-auto-sec-security-updates.patch unexpected-commit-author")]
    [InlineData("commit-extra-header", "aw-auto-sec-security-updates.patch unexpected-commit-header")]
    [InlineData("commit-headers-missing", "aw-auto-sec-security-updates.patch missing-commit-headers")]
    public async Task PublicTextGateAcceptsOnlyTemplateText(string scenario, string expected)
    {
        var patch = PublicTextPatch.Replace("\r\n", "\n");
        var body = PublicTextBody.Replace("\r\n", "\n");
        var title = "Automated dependency updates";
        var pushMessage = "Update dependencies\n\nlodash 4.17.20 -> 4.17.21\n@types/node 22.0.0 -> 22.1.0";
        var push = false;
        switch (scenario)
        {
            case "valid-create-prefixed-title":
                title = "[auto-sec] Automated dependency updates";
                break;
            case "valid-push":
                push = true;
                break;
            case "title-free-text":
                title = "Automated dependency updates for alert 42";
                break;
            case "body-extra-paragraph":
                body += "\n\nThis resolves alert 42.";
                break;
            case "body-free-text-row":
                body = body.Replace("| lodash |", "| lodash prototype pollution |");
                break;
            case "body-forbidden-token-row":
                body = body.Replace("| lodash |", "| GHSA-xxxx |");
                break;
            case "body-without-rows":
                body = string.Join('\n', body.Split('\n').Where(line => !line.StartsWith("| lodash", StringComparison.Ordinal) && !line.StartsWith("| `@types", StringComparison.Ordinal)));
                break;
            case "body-row-not-in-patch":
                body = body.Replace("| 4.17.20 | 4.17.21 |", "| 4.17.20 | 4.17.99 |");
                break;
            case "push-message-free-text":
                push = true;
                pushMessage = "Update dependencies\n\nlodash: fixes a malware alert";
                break;
            case "commit-subject-free-text":
                patch = patch.Replace("Subject: [PATCH] Update dependencies", "Subject: [PATCH] Fix alert 42");
                break;
            case "commit-body-free-text":
                patch = patch.Replace("lodash 4.17.20 -> 4.17.21\n", "lodash 4.17.20 -> 4.17.21\nResolves alert 42\n");
                break;
            case "commit-author":
                patch = patch.Replace("From: \"github-actions[bot]\"", "From: \"Alert 42\"");
                break;
            case "commit-extra-header":
                patch = patch.Replace("Date: ", "X-Note: alert 42\nDate: ");
                break;
            case "commit-headers-missing":
                patch = patch[patch.IndexOf("diff --git", StringComparison.Ordinal)..];
                break;
        }

        JsonObject item = push
            ? new JsonObject { ["type"] = "push_to_pull_request_branch", ["pull_request_number"] = 202, ["message"] = pushMessage }
            : new JsonObject { ["type"] = "create_pull_request", ["branch"] = "auto-sec/security-updates", ["title"] = title, ["body"] = body };
        var result = await RunHarnessAsync(new JsonObject
        {
            ["mode"] = "public-text-gate",
            ["agentItems"] = new JsonArray(item),
            ["patchFiles"] = new JsonObject { ["aw-auto-sec-security-updates.patch"] = patch },
        });

        var violations = result["value"]!["violations"]!.AsArray().Select(v => $"{v!["source"]} {v["reason"]}").ToArray();
        if (expected.Length == 0)
        {
            Assert.Empty(violations);
            Assert.Empty(result["failures"]!.AsArray());
        }
        else
        {
            Assert.Equal([expected], violations);
            // The failure names the field and reason only, never the rejected text.
            var failure = Assert.Single(result["failures"]!.AsArray())!.GetValue<string>();
            Assert.DoesNotContain("alert 42", failure, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [RequiresTools(["node"])]
    [InlineData("alerts=12 dependabot-pr=3 auto-sec-pr=6 blocked=3 (nuget-not-mirrored=1, breaking-upgrade-required=2) code-findings-out-of-scope=344", true)]
    [InlineData("alerts=0 dependabot-pr=0 auto-sec-pr=0 blocked=0 code-findings-out-of-scope=0", true)]
    [InlineData("alerts=1 dependabot-pr=0 auto-sec-pr=0 blocked=1 (no-safe-version=1) code-findings-out-of-scope=0", true)]
    [InlineData("alerts=3 dependabot-pr=0 auto-sec-pr=0 blocked=3 (update-failed=1) code-findings-out-of-scope=0", false)]
    [InlineData("alerts=2 dependabot-pr=0 auto-sec-pr=0 blocked=2 code-findings-out-of-scope=0", false)]
    [InlineData("alerts=0 dependabot-pr=0 auto-sec-pr=0 blocked=0 (update-failed=0) code-findings-out-of-scope=0", false)]
    [InlineData("alerts=1 dependabot-pr=0 auto-sec-pr=0 blocked=1 (update-failed=2) code-findings-out-of-scope=0", false)]
    [InlineData("alerts=1 dependabot-pr=0 auto-sec-pr=0 blocked=1 (update-failed=0, no-safe-version=1) code-findings-out-of-scope=0", false)]
    [InlineData("alerts=1 dependabot-pr=0 auto-sec-pr=0 blocked=1 (lodash=1) code-findings-out-of-scope=0", false)]
    [InlineData("alerts=2 dependabot-pr=0 auto-sec-pr=0 blocked=2 (update-failed=1, update-failed=1) code-findings-out-of-scope=0", false)]
    [InlineData("alerts=1 dependabot-pr=0 auto-sec-pr=1 blocked=0 code-findings-out-of-scope=0 lodash 4.17.21", false)]
    [InlineData("Reviewed alert 42 for lodash", false)]
    public async Task NoopReportAcceptsOnlyCounts(string message, bool expected)
    {
        var result = await RunHarnessAsync(new JsonObject
        {
            ["mode"] = "call",
            ["fn"] = "checkPublicNoopMessage",
            ["args"] = new JsonArray(message),
        });

        Assert.Equal(expected, result["value"]!.GetValue<bool>());
    }

    [Theory]
    [RequiresTools(["node"])]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("normalized-duplicate")]
    public async Task PublicTextGateRequiresEveryUpdateExactlyOnce(string scenario)
    {
        var body = PublicTextBody.Replace("\r\n", "\n");
        const string row = "| lodash | extension/package-lock.json | 4.17.20 | 4.17.21 |";
        body = scenario == "missing"
            ? string.Join('\n', body.Split('\n').Where(line => !line.StartsWith("| `@types", StringComparison.Ordinal)))
            : body.Replace(row, $"{row}\n{(scenario == "normalized-duplicate" ? row.Replace("lodash", "LODASH", StringComparison.Ordinal) : row)}", StringComparison.Ordinal);
        var output = PublicOutputFixture("create_pull_request");
        output["body"] = body;
        var result = await RunHarnessAsync(new JsonObject
        {
            ["mode"] = "agent-scrub",
            ["publicationConditions"] = CompiledPublicationConditions(),
            ["outputLines"] = new JsonArray(output.ToJsonString()),
            ["patchFiles"] = new JsonObject { ["aw-auto-sec-security-updates.patch"] = PublicTextPatch.Replace("\r\n", "\n") },
        });

        Assert.Equal(["create_pull_request.body incomplete-or-duplicate-summary"],
            result["value"]!["violations"]!.AsArray().Select(v => $"{v!["source"]} {v["reason"]}"));
        Assert.Empty(result["outputs"]!.AsArray());
        Assert.Empty(result["publications"]!.AsArray());
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task PublicTextGateAcceptsCompleteReorderedNormalizedRows()
    {
        const string lodash = "| lodash | extension/package-lock.json | 4.17.20 | 4.17.21 |";
        const string node = "| `@types/node` | extension/package-lock.json | 22.0.0 | 22.1.0 |";
        var output = PublicOutputFixture("create_pull_request");
        output["body"] = PublicTextBody.Replace($"{lodash}\r\n{node}", $"{node}\r\n{lodash}", StringComparison.Ordinal)
            .Replace($"{lodash}\n{node}", $"{node}\n{lodash}", StringComparison.Ordinal)
            .Replace("| lodash |", "| LODASH |", StringComparison.Ordinal);
        var result = await RunHarnessAsync(new JsonObject
        {
            ["mode"] = "agent-scrub",
            ["outputLines"] = new JsonArray(output.ToJsonString()),
            ["patchFiles"] = new JsonObject { ["aw-auto-sec-security-updates.patch"] = PublicTextPatch.Replace("\r\n", "\n") },
        });

        Assert.Empty(result["failures"]!.AsArray());
        Assert.Equal("true", result["stepOutputs"]!["publication_ready"]!.GetValue<string>());
    }

    [Theory]
    [RequiresTools(["node", "git"])]
    [InlineData("commit", "empty", false)]
    [InlineData("commit", "missing", false)]
    [InlineData("commit", "duplicate", false)]
    [InlineData("commit", "normalized-duplicate", false)]
    [InlineData("commit", "complete", true)]
    [InlineData("commit", "reordered-normalized", true)]
    [InlineData("push", "empty", false)]
    [InlineData("push", "missing", false)]
    [InlineData("push", "duplicate", false)]
    [InlineData("push", "normalized-duplicate", false)]
    [InlineData("push", "complete", true)]
    [InlineData("push", "reordered-normalized", true)]
    public async Task AgentOutputScrubRequiresCompleteCommitAndPushTuples(string field, string scenario, bool accepted)
    {
        const string complete = "Update dependencies\n\nlodash 4.17.20 -> 4.17.21\n@types/node 22.0.0 -> 22.1.0";
        var message = scenario switch
        {
            "empty" => "Update dependencies",
            "missing" => "Update dependencies\n\nlodash 4.17.20 -> 4.17.21",
            "duplicate" => complete + "\nlodash 4.17.20 -> 4.17.21",
            "normalized-duplicate" => complete + "\nLODASH v4.17.20 -> v4.17.21",
            "reordered-normalized" => "Update dependencies\n\n@types/node 22.0.0 -> 22.1.0\nLODASH v4.17.20 -> v4.17.21",
            _ => complete,
        };
        var output = PublicOutputFixture("push_to_pull_request_branch");
        output["message"] = field == "push" ? message : complete;
        var result = await RunHarnessAsync(new JsonObject
        {
            ["mode"] = "agent-scrub",
            ["useGitSnapshot"] = true,
            ["baseFiles"] = new JsonObject { ["extension/package-lock.json"] = PublicTextBase },
            ["generatedPatch"] = new JsonObject
            {
                ["headFiles"] = new JsonObject
                {
                    ["extension/package-lock.json"] = PublicTextBase.Replace("4.17.20", "4.17.21", StringComparison.Ordinal).Replace("22.0.0", "22.1.0", StringComparison.Ordinal),
                },
                ["message"] = field == "commit" ? message : complete,
            },
            ["outputLines"] = new JsonArray(output.ToJsonString()),
            ["publicationConditions"] = CompiledPublicationConditions(),
        });

        if (accepted)
        {
            Assert.Empty(result["value"]!["violations"]!.AsArray());
            Assert.Single(result["outputs"]!.AsArray());
            Assert.Equal("true", result["stepOutputs"]!["publication_ready"]!.GetValue<string>());
        }
        else
        {
            var source = field == "commit" ? "aw-auto-sec-security-updates.patch" : "push_to_pull_request_branch.message";
            var reason = scenario == "empty"
                ? field == "commit" ? "non-template-commit-message" : "non-template-text"
                : "incomplete-or-duplicate-summary";
            Assert.Equal([$"{source} {reason}"],
                result["value"]!["violations"]!.AsArray().Select(v => $"{v!["source"]} {v["reason"]}"));
            Assert.Empty(result["outputs"]!.AsArray());
            Assert.Empty(result["remaining"]!.AsArray());
            Assert.Null(result["stepOutputs"]!["publication_ready"]);
            Assert.Empty(result["publications"]!.AsArray());
        }
    }

    [Theory]
    [RequiresTools(["node", "git"])]
    [InlineData("complete", true)]
    [InlineData("borrowed", false)]
    [InlineData("extra-borrowed", false)]
    [InlineData("empty", false)]
    [InlineData("intermediate", false)]
    public async Task AgentOutputScrubBindsRowsToEachCommitInAPatchSeries(string scenario, bool accepted)
    {
        const string node = "Update dependencies\n\n@types/node 22.0.0 -> 22.1.0";
        const string lodash = "Update dependencies\n\nlodash 4.17.20 -> 4.17.21";
        var firstHead = PublicTextBase.Replace("22.0.0", "22.1.0", StringComparison.Ordinal);
        if (scenario == "intermediate")
        {
            firstHead = PublicTextBase.Replace("22.0.0", "22.0.1", StringComparison.Ordinal);
        }
        var firstMessage = scenario switch
        {
            "borrowed" => lodash,
            "extra-borrowed" => node + "\nlodash 4.17.20 -> 4.17.21",
            "empty" => "Update dependencies",
            "intermediate" => "Update dependencies\n\n@types/node 22.0.0 -> 22.0.1",
            _ => node,
        };
        var output = PublicOutputFixture("push_to_pull_request_branch");
        var result = await RunHarnessAsync(new JsonObject
        {
            ["mode"] = "agent-scrub",
            ["useGitSnapshot"] = true,
            ["baseFiles"] = new JsonObject { ["extension/package-lock.json"] = PublicTextBase },
            ["generatedPatch"] = new JsonObject
            {
                ["commits"] = new JsonArray(
                    new JsonObject { ["headFiles"] = new JsonObject { ["extension/package-lock.json"] = firstHead }, ["message"] = firstMessage },
                    new JsonObject
                    {
                        ["headFiles"] = new JsonObject { ["extension/package-lock.json"] = firstHead.Replace("4.17.20", "4.17.21", StringComparison.Ordinal).Replace("22.0.1", "22.1.0", StringComparison.Ordinal) },
                        ["message"] = scenario == "intermediate" ? lodash + "\n@types/node 22.0.1 -> 22.1.0" : lodash,
                    }),
            },
            ["outputLines"] = new JsonArray(output.ToJsonString()),
            ["publicationConditions"] = CompiledPublicationConditions(),
        });

        if (accepted)
        {
            Assert.Empty(result["value"]!["violations"]!.AsArray());
            Assert.Equal("true", result["stepOutputs"]!["publication_ready"]!.GetValue<string>());
        }
        else
        {
            var reason = scenario == "empty" ? "non-template-commit-message" : "commit-row-not-in-patch";
            var expected = scenario == "intermediate"
                ? new[] { $"aw-auto-sec-security-updates.patch {reason}", $"aw-auto-sec-security-updates.patch {reason}" }
                : [$"aw-auto-sec-security-updates.patch {reason}"];
            Assert.Equal(expected,
                result["value"]!["violations"]!.AsArray().Select(v => $"{v!["source"]} {v["reason"]}"));
            Assert.Empty(result["outputs"]!.AsArray());
            Assert.Null(result["stepOutputs"]!["publication_ready"]);
            Assert.Empty(result["publications"]!.AsArray());
        }
    }

    [Theory]
    [RequiresTools(["node", "git"])]
    [InlineData("commit", "same-tuple", true)]
    [InlineData("push", "same-tuple", true)]
    [InlineData("commit", "same-tuple", false)]
    [InlineData("push", "same-tuple", false)]
    [InlineData("commit", "different-priors", true)]
    [InlineData("push", "different-priors", true)]
    [InlineData("commit", "different-priors", false)]
    [InlineData("push", "different-priors", false)]
    [InlineData("commit", "installed-copies", true)]
    [InlineData("push", "installed-copies", true)]
    [InlineData("commit", "installed-copies", false)]
    [InlineData("push", "installed-copies", false)]
    [InlineData("commit", "introduced", false)]
    [InlineData("push", "introduced", false)]
    public async Task AgentOutputScrubDeduplicatesManifestCopiesButCoversEveryPriorVersion(string field, string layout, bool accepted)
    {
        var files = layout is "installed-copies" or "introduced"
            ? new JsonObject
            {
                ["extension/package-lock.json"] = """{"packages":{"node_modules/x":{"version":"1.0.0"},"node_modules/y/node_modules/x":{"version":"1.0.1"}}}""" + "\n",
            }
            : new JsonObject
            {
                ["one/package.json"] = """{"dependencies":{"x":"1.0.0"}}""" + "\n",
                ["two/package.json"] = layout == "same-tuple"
                    ? """{"dependencies":{"x":"1.0.0"}}""" + "\n"
                    : """{"dependencies":{"x":"1.0.1"}}""" + "\n",
            };
        var heads = new JsonObject();
        foreach (var path in files.Select(pair => pair.Key).ToArray())
        {
            var text = JsonNode.Parse(files[path]!.GetValue<string>())!.ToJsonString(new JsonSerializerOptions { WriteIndented = true }).ReplaceLineEndings("\n") + "\n";
            files[path] = text;
            heads[path] = text.Replace("1.0.0", "1.0.2", StringComparison.Ordinal).Replace("1.0.1", "1.0.2", StringComparison.Ordinal);
        }
        if (layout == "introduced")
        {
            var head = JsonNode.Parse(heads["extension/package-lock.json"]!.GetValue<string>())!;
            head["packages"]!["node_modules/y"] = new JsonObject { ["version"] = "1.0.2" };
            heads["extension/package-lock.json"] = head.ToJsonString(new JsonSerializerOptions { WriteIndented = true }).ReplaceLineEndings("\n") + "\n";
        }
        const string first = "Update dependencies\n\nx 1.0.0 -> 1.0.2";
        var complete = layout == "same-tuple" ? first : first + "\nx 1.0.1 -> 1.0.2";
        var message = accepted || layout == "introduced" ? complete : layout == "same-tuple" ? first + "\nx 1.0.0 -> 1.0.2" : first;
        var output = PublicOutputFixture("push_to_pull_request_branch");
        output["message"] = field == "push" ? message : complete;
        var result = await RunHarnessAsync(new JsonObject
        {
            ["mode"] = "agent-scrub",
            ["useGitSnapshot"] = true,
            ["baseFiles"] = files,
            ["generatedPatch"] = new JsonObject { ["headFiles"] = heads, ["message"] = field == "commit" ? message : complete },
            ["outputLines"] = layout == "introduced" && field == "commit" ? new JsonArray() : new JsonArray(output.ToJsonString()),
            ["publicationConditions"] = CompiledPublicationConditions(),
        });

        if (accepted)
        {
            Assert.Empty(result["value"]!["violations"]!.AsArray());
            Assert.Equal("true", result["stepOutputs"]!["publication_ready"]!.GetValue<string>());
        }
        else
        {
            var source = field == "commit" ? "aw-auto-sec-security-updates.patch" : "push_to_pull_request_branch.message";
            var expected = layout == "introduced" && field == "push"
                ? new[] { $"{source} incomplete-or-duplicate-summary", "aw-auto-sec-security-updates.patch incomplete-or-duplicate-summary" }
                : [$"{source} incomplete-or-duplicate-summary"];
            Assert.Equal(expected,
                result["value"]!["violations"]!.AsArray().Select(v => $"{v!["source"]} {v["reason"]}"));
            Assert.Empty(result["outputs"]!.AsArray());
            Assert.Empty(result["publications"]!.AsArray());
        }
    }

    [Theory]
    [RequiresTools(["node"])]
    [InlineData("aw-prompts/prompt.txt")]
    [InlineData("agent_execution.json")]
    [InlineData("pre-agent-audit.txt")]
    [InlineData("github_rate_limits.jsonl")]
    [InlineData("agent_usage.json")]
    [InlineData("aw_info.json")]
    [InlineData("sandbox/firewall/logs/private.jsonl")]
    [InlineData("new-framework-input/private.txt")]
    [InlineData("safeoutputs.jsonl")]
    [InlineData("agent_output.json")]
    public async Task AgentOutputScrubWithholdsEveryUnvalidatedWorkFile(string filename)
    {
        var result = await RunHarnessAsync(new JsonObject
        {
            ["mode"] = "agent-scrub",
            ["publicationConditions"] = CompiledPublicationConditions(),
            ["outputLines"] = new JsonArray(PublicOutputFixture("create_pull_request").ToJsonString()),
            ["patchFiles"] = new JsonObject { ["aw-auto-sec-security-updates.patch"] = PublicTextPatch.Replace("\r\n", "\n") },
            ["workFiles"] = new JsonObject { [filename] = "private advisory detail" },
        });

        Assert.Empty(result["failures"]!.AsArray());
        Assert.Equal(["aw-auto-sec-security-updates.patch"], result["remaining"]!.AsArray().Select(v => v!.GetValue<string>()));
        Assert.Equal("true", result["stepOutputs"]!["publication_ready"]!.GetValue<string>());
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task AgentOutputScrubRecreatesNestedOutputsAfterRemovingUnvalidatedSiblings()
    {
        var output = PublicOutputFixture("create_pull_request").ToJsonString();
        var result = await RunHarnessAsync(new JsonObject
        {
            ["mode"] = "agent-scrub",
            ["outputRelativePath"] = "safeoutputs/raw/outputs.jsonl",
            ["outputLines"] = new JsonArray(output),
            ["patchFiles"] = new JsonObject { ["aw-auto-sec-security-updates.patch"] = PublicTextPatch.Replace("\r\n", "\n") },
            ["workFiles"] = new JsonObject { ["safeoutputs/private.txt"] = "private advisory detail" },
        });

        Assert.Empty(result["failures"]!.AsArray());
        Assert.Equal([output], result["outputs"]!.AsArray().Select(v => JsonNode.Parse(v!.GetValue<string>())!.ToJsonString()));
        Assert.Equal(["aw-auto-sec-security-updates.patch"], result["remaining"]!.AsArray().Select(v => v!.GetValue<string>()));
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task AgentOutputScrubRemovesTranscriptAndKeepsTemplateOutputs()
    {
        const string headSha = "0123456789abcdef0123456789abcdef01234567";
        var result = await RunHarnessAsync(new JsonObject
        {
            ["mode"] = "agent-scrub",
            ["publicationConditions"] = CompiledPublicationConditions(),
            ["outputLines"] = new JsonArray(
                new JsonObject { ["type"] = "create_pull_request", ["branch"] = "auto-sec/security-updates", ["title"] = "Automated dependency updates", ["body"] = PublicTextBody.Replace("\r\n", "\n") }.ToJsonString(),
                new JsonObject { ["type"] = "approve_dependabot_pr", ["pr_number"] = 101, ["head_sha"] = headSha, ["note"] = "fixes alert 42" }.ToJsonString(),
                new JsonObject { ["type"] = "missing_tool", ["tool"] = "alerts", ["reason"] = "alert 42 needs a tool" }.ToJsonString(),
                new JsonObject { ["type"] = "noop", ["message"] = "alerts=2 dependabot-pr=1 auto-sec-pr=1 blocked=0 code-findings-out-of-scope=5" }.ToJsonString()),
            ["patchFiles"] = new JsonObject { ["aw-auto-sec-security-updates.patch"] = PublicTextPatch.Replace("\r\n", "\n") },
            ["workFiles"] = new JsonObject
            {
                ["sandbox/agent/logs/session.jsonl"] = "alert 42",
                ["mcp-logs/safeoutputs.log"] = "alert 42",
                ["proxy-logs/proxy.log"] = "alert 42",
                ["agent-stdio.log"] = "alert 42",
                ["agent-step-summary.md"] = "alert 42",
                ["redacted-urls.log"] = "alert 42",
                ["otel.jsonl"] = "alert 42",
                ["agent_execution.json"] = "{}",
            },
        });

        Assert.Empty(result["failures"]!.AsArray());
        Assert.Equal("true", result["stepOutputs"]!["publication_ready"]!.GetValue<string>());
        Assert.Equal(CompiledPublicationConditions().Count, result["publications"]!.AsArray().Count);
        Assert.Equal(["aw-auto-sec-security-updates.patch"], result["remaining"]!.AsArray().Select(name => name!.GetValue<string>()));
        var outputs = result["outputs"]!.AsArray().Select(line => JsonNode.Parse(line!.GetValue<string>())!).ToArray();
        Assert.Collection(
            outputs,
            item => Assert.Equal("create_pull_request", item["type"]!.GetValue<string>()),
            item => Assert.Equal(new JsonObject { ["type"] = "approve_dependabot_pr", ["pr_number"] = 101, ["head_sha"] = headSha }.ToJsonString(), item.ToJsonString()),
            item => Assert.Equal("noop", item["type"]!.GetValue<string>()));
    }

    [Theory]
    [RequiresTools(["node"])]
    [InlineData("create_pull_request", "push_to_pull_request_branch")]
    [InlineData("create_pull_request", "create_pull_request")]
    [InlineData("push_to_pull_request_branch", "push_to_pull_request_branch")]
    public async Task AgentOutputScrubRejectsConflictingCodeWritingRequests(string first, string second)
    {
        var result = await RunHarnessAsync(new JsonObject
        {
            ["mode"] = "agent-scrub",
            ["publicationConditions"] = CompiledPublicationConditions(),
            ["outputLines"] = new JsonArray(PublicOutputFixture(first).ToJsonString(), PublicOutputFixture(second).ToJsonString()),
            ["patchFiles"] = new JsonObject { ["aw-auto-sec-security-updates.patch"] = PublicTextPatch.Replace("\r\n", "\n") },
        });

        Assert.Equal(["code-writing-outputs conflicting-requests"], result["value"]!["violations"]!.AsArray().Select(v => $"{v!["source"]} {v["reason"]}"));
        Assert.Empty(result["outputs"]!.AsArray());
        Assert.Empty(result["remaining"]!.AsArray());
        Assert.Null(result["stepOutputs"]!["publication_ready"]);
        Assert.Empty(result["publications"]!.AsArray());
    }

    [Theory]
    [RequiresTools(["node"])]
    [InlineData("package-lock.json", "\"note\": \"alert 42: prototype pollution in lodash\",")]
    [InlineData("package-lock.json", "\"note\": { \"text\": \"alert 42\" },")]
    [InlineData("package-lock.json", "\"note\": [\"alert 42\"],")]
    [InlineData("package-lock.json", "\"\\u006eote\": \"alert 42\",")]
    [InlineData("package-lock.json", "\"alert 42 prototype pollution\": true,")]
    [InlineData("package-lock.json", "\"funding\": { \"url\": \"https://example.com/alert-42\" },")]
    [InlineData("package-lock.json", "\"integrity\": \"alert 42\",")]
    [InlineData("package-lock.json", "\"resolved\": \"https://example.com/pkg.tgz?note=alert42\",")]
    [InlineData("npm-shrinkwrap.json", "\"note\": \"alert 42: prototype pollution in lodash\",")]
    [InlineData("npm-shrinkwrap.json", "\"note\": { \"text\": \"alert 42\" },")]
    public async Task AgentOutputScrubRejectsUnrecognizedJsonLockfileText(string basename, string addedLine)
    {
        var patch = PublicTextPatch.Replace("\r\n", "\n")
            .Replace("extension/package-lock.json", $"extension/{basename}", StringComparison.Ordinal)
            .Replace("@@ -3,3 +3,3 @@", "@@ -3,3 +3,4 @@", StringComparison.Ordinal)
            .Replace("+      \"version\": \"4.17.21\",\n", $"+      \"version\": \"4.17.21\",\n+      {addedLine}\n", StringComparison.Ordinal);
        var output = PublicOutputFixture("create_pull_request");
        output["body"] = PublicTextBody.Replace("\r\n", "\n").Replace("extension/package-lock.json", $"extension/{basename}", StringComparison.Ordinal);
        var result = await RunHarnessAsync(new JsonObject
        {
            ["mode"] = "agent-scrub",
            ["publicationConditions"] = CompiledPublicationConditions(),
            ["outputLines"] = new JsonArray(output.ToJsonString()),
            ["patchFiles"] = new JsonObject { ["aw-auto-sec-security-updates.patch"] = patch },
        });

        string[] reasons = addedLine.StartsWith("\"resolved\"", StringComparison.Ordinal)
            ? ["unbound-lockfile-artifact", "new-package-source", "unsupported-lockfile-text"]
            : ["unsupported-lockfile-text"];
        var expected = reasons.Select(reason => $"aw-auto-sec-security-updates.patch {reason}").ToArray();
        Assert.Equal(expected, result["value"]!["violations"]!.AsArray().Select(v => $"{v!["source"]} {v["reason"]}"));
        Assert.Equal([$"auto-sec agent output scrub failed: {string.Join("; ", expected)}"], result["failures"]!.AsArray().Select(v => v!.GetValue<string>()));
        Assert.Empty(result["outputs"]!.AsArray());
        Assert.Empty(result["remaining"]!.AsArray());
        Assert.Null(result["stepOutputs"]!["publication_ready"]);
        Assert.Empty(result["publications"]!.AsArray());
    }

    [Theory]
    [RequiresTools(["node", "git"])]
    [InlineData("package-lock.json", "root-field", false)]
    [InlineData("npm-shrinkwrap.json", "root-field", false)]
    [InlineData("package-lock.json", "encoded-root-field", false)]
    [InlineData("package-lock.json", "package-field", false)]
    [InlineData("npm-shrinkwrap.json", "package-field", false)]
    [InlineData("package-lock.json", "root-flag", false)]
    [InlineData("package-lock.json", "package-lockfile-version", false)]
    [InlineData("package-lock.json", "misplaced-package", false)]
    [InlineData("package-lock.json", "misplaced-dependency-map", false)]
    [InlineData("package-lock.json", "misplaced-requires", false)]
    [InlineData("package-lock.json", "engine-field", false)]
    [InlineData("package-lock.json", "duplicate-version", false)]
    [InlineData("package-lock.json", "metadata-array", false)]
    [InlineData("package-lock.json", "unchanged-metadata-array", true)]
    [InlineData("package-lock.json", "dependencies", true)]
    [InlineData("package-lock.json", "devDependencies", true)]
    [InlineData("package-lock.json", "optionalDependencies", true)]
    [InlineData("npm-shrinkwrap.json", "peerDependencies", true)]
    [InlineData("package-lock.json", "root-metadata", true)]
    public async Task AgentOutputScrubValidatesJsonDependencyLocations(string basename, string scenario, bool accepted)
    {
        var path = $"extension/{basename}";
        var baseText = PublicTextBase;
        if (scenario is "metadata-array" or "unchanged-metadata-array")
        {
            var baseline = JsonNode.Parse(baseText)!;
            baseline["metadata"] = new JsonArray(new JsonObject { ["version"] = "1.0.0" });
            baseText = baseline.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        }
        var head = JsonNode.Parse(baseText.Replace("4.17.20", "4.17.21", StringComparison.Ordinal).Replace("22.0.0", "22.1.0", StringComparison.Ordinal))!;
        var package = head["packages"]!["node_modules/lodash"]!;
        switch (scenario)
        {
            case "root-field":
            case "encoded-root-field":
                head["alert-42"] = "1.2.3";
                break;
            case "package-field":
                package["alert-42"] = "1.2.3";
                break;
            case "root-flag":
                head["dev"] = true;
                break;
            case "package-lockfile-version":
                package["lockfileVersion"] = 3;
                break;
            case "misplaced-package":
                head["node_modules/alert-42"] = new JsonObject { ["version"] = "1.2.3" };
                break;
            case "misplaced-dependency-map":
                head["dependencies"] = new JsonObject { ["alert-42"] = "1.2.3" };
                break;
            case "misplaced-requires":
                head["requires"] = new JsonObject { ["alert-42"] = "1.2.3" };
                break;
            case "engine-field":
                package["engines"] = new JsonObject { ["alert-42"] = "1.2.3" };
                break;
            case "duplicate-version":
            case "unchanged-metadata-array":
                break;
            case "metadata-array":
                head["metadata"]![0]!["version"] = "42.0.0";
                break;
            case "root-metadata":
                head["lockfileVersion"] = 3;
                head["requires"] = true;
                break;
            default:
                package[scenario] = new JsonObject { ["@types/node"] = "^22.1.0" };
                break;
        }
        var text = head.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        if (scenario == "encoded-root-field")
        {
            text = text.Replace("\"alert-42\"", "\"\\u0061lert-42\"", StringComparison.Ordinal);
        }
        else if (scenario == "duplicate-version")
        {
            text = text.Replace("\"version\": \"4.17.21\"", "\"version\": \"42.0.0\",\n      \"version\": \"4.17.21\"", StringComparison.Ordinal);
        }
        var output = PublicOutputFixture("create_pull_request");
        output["body"] = output["body"]!.GetValue<string>().Replace("extension/package-lock.json", path, StringComparison.Ordinal);
        var result = await RunHarnessAsync(new JsonObject
        {
            ["mode"] = "agent-scrub",
            ["useGitSnapshot"] = true,
            ["baseFiles"] = new JsonObject { [path] = baseText },
            ["generatedPatch"] = new JsonObject
            {
                ["headFiles"] = new JsonObject { [path] = text },
                ["message"] = "Update dependencies\n\nlodash 4.17.20 -> 4.17.21\n@types/node 22.0.0 -> 22.1.0\n",
            },
            ["outputLines"] = new JsonArray(output.ToJsonString()),
            ["publicationConditions"] = CompiledPublicationConditions(),
        });

        if (accepted)
        {
            Assert.Empty(result["value"]!["violations"]!.AsArray());
            Assert.Empty(result["failures"]!.AsArray());
            Assert.Single(result["outputs"]!.AsArray());
            Assert.Equal("true", result["stepOutputs"]!["publication_ready"]!.GetValue<string>());
            Assert.Equal(CompiledPublicationConditions().Count, result["publications"]!.AsArray().Count);
        }
        else
        {
            Assert.Equal(["aw-auto-sec-security-updates.patch unsupported-lockfile-text"],
                result["value"]!["violations"]!.AsArray().Select(v => $"{v!["source"]} {v["reason"]}"));
            Assert.Equal(["auto-sec agent output scrub failed: aw-auto-sec-security-updates.patch unsupported-lockfile-text"],
                result["failures"]!.AsArray().Select(n => n!.GetValue<string>()));
            Assert.Empty(result["outputs"]!.AsArray());
            Assert.Empty(result["remaining"]!.AsArray());
            Assert.Null(result["stepOutputs"]!["publication_ready"]);
            Assert.Empty(result["publications"]!.AsArray());
        }
    }

    [Theory]
    [RequiresTools(["node", "git"])]
    [InlineData("package-lock.json", 1)]
    [InlineData("package-lock.json", 2)]
    [InlineData("package-lock.json", 3)]
    [InlineData("npm-shrinkwrap.json", 1)]
    [InlineData("npm-shrinkwrap.json", 2)]
    [InlineData("npm-shrinkwrap.json", 3)]
    public async Task AgentOutputScrubPreservesRecognizedNpmLockfileVersions(string basename, int version)
    {
        var path = $"extension/{basename}";
        var baseline = new JsonObject { ["lockfileVersion"] = version, ["requires"] = true };
        if (version <= 2)
        {
            baseline["dependencies"] = new JsonObject
            {
                ["lodash"] = new JsonObject
                {
                    ["version"] = "4.17.20",
                    ["requires"] = new JsonObject { ["@types/node"] = "^22.0.0" },
                    ["dependencies"] = new JsonObject { ["@types/node"] = new JsonObject { ["version"] = "22.0.0" } },
                },
                ["@types/node"] = new JsonObject { ["version"] = "22.0.0" },
            };
        }
        if (version >= 2)
        {
            baseline["packages"] = JsonNode.Parse(PublicTextBase)!["packages"]!.DeepClone();
        }
        var baseText = baseline.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        var output = PublicOutputFixture("create_pull_request");
        output["body"] = output["body"]!.GetValue<string>().Replace("extension/package-lock.json", path, StringComparison.Ordinal);
        var result = await RunHarnessAsync(new JsonObject
        {
            ["mode"] = "agent-scrub",
            ["useGitSnapshot"] = true,
            ["baseFiles"] = new JsonObject { [path] = baseText },
            ["generatedPatch"] = new JsonObject
            {
                ["headFiles"] = new JsonObject { [path] = baseText.Replace("4.17.20", "4.17.21", StringComparison.Ordinal).Replace("22.0.0", "22.1.0", StringComparison.Ordinal) },
                ["message"] = "Update dependencies\n\nlodash 4.17.20 -> 4.17.21\n@types/node 22.0.0 -> 22.1.0\n",
            },
            ["outputLines"] = new JsonArray(output.ToJsonString()),
            ["publicationConditions"] = CompiledPublicationConditions(),
        });

        Assert.Empty(result["value"]!["violations"]!.AsArray());
        Assert.Empty(result["failures"]!.AsArray());
        Assert.Single(result["outputs"]!.AsArray());
        Assert.Equal("true", result["stepOutputs"]!["publication_ready"]!.GetValue<string>());
        Assert.Equal(CompiledPublicationConditions().Count, result["publications"]!.AsArray().Count);
    }

    [Theory]
    [RequiresTools(["node", "git"])]
    [InlineData("package-lock.json", "none", false, 3)]
    [InlineData("npm-shrinkwrap.json", "none", false, 3)]
    [InlineData("package-lock.json", "other-package", false, 3)]
    [InlineData("npm-shrinkwrap.json", "other-package", false, 3)]
    [InlineData("package-lock.json", "other-occurrence", false, 3)]
    [InlineData("package-lock.json", "same-entry", true, 3)]
    [InlineData("npm-shrinkwrap.json", "same-entry", true, 3)]
    [InlineData("package-lock.json", "none", false, 1)]
    [InlineData("npm-shrinkwrap.json", "other-package", false, 1)]
    [InlineData("package-lock.json", "other-occurrence", false, 1)]
    [InlineData("npm-shrinkwrap.json", "same-entry", true, 1)]
    public async Task AgentOutputScrubBindsIntegrityEditsToOwningVersionTransition(string basename, string transition, bool accepted, int format)
    {
        var path = $"extension/{basename}";
        var baseline = JsonNode.Parse(PublicTextBase)!;
        if (format == 1)
        {
            baseline["dependencies"] = new JsonObject
            {
                ["lodash"] = baseline["packages"]!["node_modules/lodash"]!.DeepClone(),
                ["@types/node"] = baseline["packages"]!["node_modules/@types/node"]!.DeepClone(),
            };
            baseline.AsObject().Remove("packages");
        }
        var group = format == 1 ? "dependencies" : "packages";
        var owner = format == 1 ? "lodash" : "node_modules/lodash";
        var other = format == 1 ? "@types/node" : "node_modules/@types/node";
        baseline[group]![owner]!["integrity"] = "sha512-YWJj";
        if (transition == "other-occurrence")
        {
            if (format == 1)
            {
                baseline["dependencies"]!["parent"] = new JsonObject
                {
                    ["version"] = "1.0.0",
                    ["dependencies"] = new JsonObject { ["lodash"] = new JsonObject { ["version"] = "4.17.20" } },
                };
            }
            else
            {
                baseline["packages"]!["node_modules/parent/node_modules/lodash"] = new JsonObject { ["version"] = "4.17.20" };
            }
        }
        var head = baseline.DeepClone();
        head[group]![owner]!["integrity"] = "sha512-ZGVm";
        var message = "Update dependencies";
        switch (transition)
        {
            case "same-entry":
                head[group]![owner]!["version"] = "4.17.21";
                message += "\n\nlodash 4.17.20 -> 4.17.21\n";
                break;
            case "other-package":
                head[group]![other]!["version"] = "22.1.0";
                message += "\n\n@types/node 22.0.0 -> 22.1.0\n";
                break;
            case "other-occurrence":
                if (format == 1)
                {
                    head["dependencies"]!["parent"]!["dependencies"]!["lodash"]!["version"] = "4.17.21";
                }
                else
                {
                    head["packages"]!["node_modules/parent/node_modules/lodash"]!["version"] = "4.17.21";
                }
                message += "\n\nlodash 4.17.20 -> 4.17.21\n";
                break;
        }
        var output = PublicOutputFixture("push_to_pull_request_branch");
        output["message"] = message;
        var options = new JsonSerializerOptions { WriteIndented = true };
        var result = await RunHarnessAsync(new JsonObject
        {
            ["mode"] = "agent-scrub",
            ["useGitSnapshot"] = true,
            ["baseFiles"] = new JsonObject { [path] = baseline.ToJsonString(options) },
            ["generatedPatch"] = new JsonObject
            {
                ["headFiles"] = new JsonObject { [path] = head.ToJsonString(options) },
                ["message"] = message,
            },
            ["outputLines"] = new JsonArray(output.ToJsonString()),
            ["publicationConditions"] = CompiledPublicationConditions(),
        });

        if (accepted)
        {
            Assert.Empty(result["value"]!["violations"]!.AsArray());
            Assert.Empty(result["failures"]!.AsArray());
            Assert.Single(result["outputs"]!.AsArray());
            Assert.Equal("true", result["stepOutputs"]!["publication_ready"]!.GetValue<string>());
            Assert.Equal(CompiledPublicationConditions().Count, result["publications"]!.AsArray().Count);
        }
        else
        {
            string[] expected = transition == "none"
                ? ["push_to_pull_request_branch.message non-template-text", "aw-auto-sec-security-updates.patch non-template-commit-message", "aw-auto-sec-security-updates.patch no-version-transitions"]
                : ["aw-auto-sec-security-updates.patch unbound-lockfile-metadata"];
            Assert.Equal(expected,
                result["value"]!["violations"]!.AsArray().Select(v => $"{v!["source"]} {v["reason"]}"));
            Assert.Empty(result["outputs"]!.AsArray());
            Assert.Empty(result["remaining"]!.AsArray());
            Assert.Null(result["stepOutputs"]!["publication_ready"]);
            Assert.Empty(result["publications"]!.AsArray());
        }
    }

    [Theory]
    [RequiresTools(["node", "git"])]
    [InlineData("yarn.lock", "none", false)]
    [InlineData("yarn.lock", "other-package", false)]
    [InlineData("yarn.lock", "other-occurrence", false)]
    [InlineData("yarn.lock", "ambiguous-owner", false)]
    [InlineData("yarn.lock", "same-entry", true)]
    [InlineData("pnpm-lock.yaml", "none", false)]
    [InlineData("pnpm-lock.yaml", "other-package", false)]
    [InlineData("pnpm-lock.yaml", "other-occurrence", false)]
    [InlineData("pnpm-lock.yaml", "same-entry", true)]
    [InlineData("uv.lock", "none", false)]
    [InlineData("uv.lock", "other-package", false)]
    [InlineData("uv.lock", "other-occurrence", false)]
    [InlineData("uv.lock", "existing-target", false)]
    [InlineData("uv.lock", "same-entry", true)]
    public async Task AgentOutputScrubBindsTextLockfileMetadataToOwningTransition(string basename, string transition, bool accepted)
    {
        var path = basename == "uv.lock" ? basename : $"extension/{basename}";
        var name = basename == "uv.lock" ? "jinja2" : "lodash";
        var from = basename == "uv.lock" ? "3.1.5" : "4.17.20";
        var to = basename == "uv.lock" ? "3.1.6" : "4.17.21";
        var previous = basename == "uv.lock" ? "3.1.4" : "4.17.19";
        var other = basename == "uv.lock" ? "markupsafe" : "react";
        string Entry(string package, string version, string hash, bool copy) => basename switch
        {
            "yarn.lock" => $"\"{package}@{(copy ? "~" : "^")}{from}\":\n  version \"{version}\"\n  integrity sha512-{hash}\n",
            "pnpm-lock.yaml" => $"  {package}@{version}:\n    resolution: {{integrity: sha512-{hash}}}\n",
            _ => $"[[package]]\nname = \"{package}\"\nversion = \"{version}\"\nsource = {{ registry = \"https://pypi.org/simple\" }}\nsdist = {{ url = \"https://files.pythonhosted.org/packages/{package}-{version}.tar.gz\", hash = \"sha256:{hash}\" }}\n",
        };
        var prefix = basename == "pnpm-lock.yaml" ? "packages:\n" : "";
        var baseText = prefix;
        var headText = prefix;
        if (transition is "other-occurrence" or "existing-target" or "ambiguous-owner")
        {
            baseText += Entry(name, previous, "abcd", transition != "ambiguous-owner");
            headText += Entry(name, transition is "existing-target" or "ambiguous-owner" ? from : to, "abcd", transition != "ambiguous-owner");
        }
        baseText += Entry(name, from, "abcd", false);
        headText += Entry(name, transition == "same-entry" ? to : from, "cdef", false);
        if (transition == "other-package")
        {
            baseText += Entry(other, "2.1.3", "abcd", false);
            headText += Entry(other, "2.1.4", "abcd", false);
        }
        var message = transition switch
        {
            "none" => "Update dependencies",
            "other-package" => $"Update dependencies\n\n{other} 2.1.3 -> 2.1.4\n",
            "other-occurrence" => $"Update dependencies\n\n{name} {previous} -> {to}\n",
            "existing-target" or "ambiguous-owner" => $"Update dependencies\n\n{name} {previous} -> {from}\n",
            _ => $"Update dependencies\n\n{name} {from} -> {to}\n",
        };
        var output = PublicOutputFixture("push_to_pull_request_branch");
        output["message"] = message;
        var result = await RunHarnessAsync(new JsonObject
        {
            ["mode"] = "agent-scrub",
            ["useGitSnapshot"] = true,
            ["baseFiles"] = new JsonObject { [path] = baseText },
            ["generatedPatch"] = new JsonObject { ["headFiles"] = new JsonObject { [path] = headText }, ["message"] = message },
            ["outputLines"] = new JsonArray(output.ToJsonString()),
            ["publicationConditions"] = CompiledPublicationConditions(),
        });

        if (accepted)
        {
            Assert.Empty(result["value"]!["violations"]!.AsArray());
            Assert.Empty(result["failures"]!.AsArray());
            Assert.Single(result["outputs"]!.AsArray());
            Assert.Equal("true", result["stepOutputs"]!["publication_ready"]!.GetValue<string>());
            Assert.Equal(CompiledPublicationConditions().Count, result["publications"]!.AsArray().Count);
        }
        else
        {
            string[] expected = transition == "none"
                ? ["push_to_pull_request_branch.message non-template-text", "aw-auto-sec-security-updates.patch non-template-commit-message", "aw-auto-sec-security-updates.patch no-version-transitions"]
                : ["aw-auto-sec-security-updates.patch unbound-lockfile-metadata"];
            Assert.Equal(expected,
                result["value"]!["violations"]!.AsArray().Select(v => $"{v!["source"]} {v["reason"]}"));
            Assert.Empty(result["outputs"]!.AsArray());
            Assert.Empty(result["remaining"]!.AsArray());
            Assert.Null(result["stepOutputs"]!["publication_ready"]);
            Assert.Empty(result["publications"]!.AsArray());
        }
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task PatchGateDoesNotBorrowTransitionFromAnotherArtifact()
    {
        var baseline = JsonNode.Parse(PublicTextBase)!;
        baseline["packages"]!["node_modules/lodash"]!["integrity"] = "sha512-YWJj";
        var baseText = baseline.ToJsonString(new JsonSerializerOptions { WriteIndented = true }).Replace("\r\n", "\n", StringComparison.Ordinal);
        var integrityLine = baseText.Split('\n')
            .Single(line => line.Contains("sha512-YWJj", StringComparison.Ordinal));
        var result = await RunHarnessAsync(new JsonObject
        {
            ["mode"] = "patch-gate",
            ["workspaceFiles"] = new JsonObject { ["extension/package.json"] = PackageJsonBase, ["extension/package-lock.json"] = baseText },
            ["patchFiles"] = new JsonObject
            {
                ["aw-upgrade.patch"] = ReplacementPatch("extension/package.json", PackageJsonBase, "    \"lodash\": \"^4.17.20\"", "    \"lodash\": \"^4.17.21\""),
                ["aw-metadata.patch"] = ReplacementPatch("extension/package-lock.json", baseText, integrityLine, integrityLine.Replace("sha512-YWJj", "sha512-ZGVm", StringComparison.Ordinal)),
            },
        });

        Assert.Equal(["(patch) no-version-transitions"],
            result["value"]!["violations"]!.AsArray().Select(v => $"{v!["path"]} {v["reason"]}"));
        Assert.Single(result["failures"]!.AsArray());
    }

    [Theory]
    [RequiresTools(["node", "git"])]
    [InlineData("consumer", true)]
    [InlineData("unbound-consumer", false)]
    [InlineData("consumer-hash", false)]
    [InlineData("package-identity", false)]
    [InlineData("root-identity", false)]
    [InlineData("new-existing-target", false)]
    public async Task AgentOutputScrubBindsNpmConsumersAndPreservesPackageIdentity(string scenario, bool accepted)
    {
        const string path = "extension/package-lock.json";
        var baseline = JsonNode.Parse(PublicTextBase)!;
        baseline["name"] = "public-app";
        baseline["packages"]![""] = new JsonObject { ["dependencies"] = new JsonObject { ["lodash"] = "^4.17.20" } };
        baseline["packages"]!["node_modules/lodash"]!["name"] = "lodash";
        baseline["packages"]!["node_modules/lodash"]!["integrity"] = "sha512-YWJj";
        if (scenario == "new-existing-target")
        {
            baseline["packages"]!["node_modules/existing/node_modules/lodash"] = new JsonObject
            {
                ["name"] = "lodash", ["version"] = "4.17.21", ["integrity"] = "sha512-YWJj",
            };
        }
        var head = baseline.DeepClone();
        head["packages"]!["node_modules/lodash"]!["version"] = "4.17.21";
        head["packages"]![""]!["dependencies"]!["lodash"] = scenario == "unbound-consumer" ? "^4.17.22" : "^4.17.21";
        switch (scenario)
        {
            case "consumer-hash":
                head["packages"]![""]!["integrity"] = "sha512-ZGVm";
                break;
            case "package-identity":
                head["packages"]!["node_modules/lodash"]!["name"] = "other";
                break;
            case "root-identity":
                head["name"] = "other";
                break;
            case "new-existing-target":
                head["packages"]!["node_modules/new/node_modules/lodash"] = new JsonObject
                {
                    ["name"] = "lodash", ["version"] = "4.17.21", ["integrity"] = "sha512-ZGVm",
                };
                break;
        }
        const string message = "Update dependencies\n\nlodash 4.17.20 -> 4.17.21\n";
        var output = PublicOutputFixture("push_to_pull_request_branch");
        output["message"] = message;
        var options = new JsonSerializerOptions { WriteIndented = true };
        var result = await RunHarnessAsync(new JsonObject
        {
            ["mode"] = "agent-scrub",
            ["useGitSnapshot"] = true,
            ["baseFiles"] = new JsonObject { [path] = baseline.ToJsonString(options) },
            ["generatedPatch"] = new JsonObject { ["headFiles"] = new JsonObject { [path] = head.ToJsonString(options) }, ["message"] = message },
            ["outputLines"] = new JsonArray(output.ToJsonString()),
            ["publicationConditions"] = CompiledPublicationConditions(),
        });

        if (accepted)
        {
            Assert.Empty(result["value"]!["violations"]!.AsArray());
            Assert.Empty(result["failures"]!.AsArray());
            Assert.Equal("true", result["stepOutputs"]!["publication_ready"]!.GetValue<string>());
        }
        else
        {
            Assert.Equal(["aw-auto-sec-security-updates.patch unbound-lockfile-metadata"],
                result["value"]!["violations"]!.AsArray().Select(v => $"{v!["source"]} {v["reason"]}"));
            Assert.Empty(result["outputs"]!.AsArray());
            Assert.Empty(result["remaining"]!.AsArray());
            Assert.Null(result["stepOutputs"]!["publication_ready"]);
            Assert.Empty(result["publications"]!.AsArray());
        }
    }

    [Theory]
    [RequiresTools(["node", "git"])]
    [InlineData("importer", true)]
    [InlineData("snapshot", true)]
    [InlineData("snapshot-hash", false)]
    [InlineData("misplaced-hash", false)]
    public async Task AgentOutputScrubSeparatesPnpmConsumerReferencesFromArtifactMetadata(string scenario, bool accepted)
    {
        const string path = "extension/pnpm-lock.yaml";
        const string packages = "packages:\n  lodash@4.17.20:\n    resolution: {integrity: sha512-YWJj}\n";
        var consumer = scenario == "importer"
            ? "importers:\n  .:\n    dependencies:\n      lodash:\n        specifier: ^4.17.20\n        version: 4.17.20\n"
            : "snapshots:\n  parent@1.0.0:\n    resolution: {integrity: sha512-YWJj}\n    dependencies:\n      lodash: 4.17.20\n";
        var baseText = packages + consumer;
        var headText = baseText.Replace("4.17.20", "4.17.21", StringComparison.Ordinal);
        if (scenario == "snapshot-hash")
        {
            headText = packages.Replace("4.17.20", "4.17.21", StringComparison.Ordinal)
                + consumer.Replace("4.17.20", "4.17.21", StringComparison.Ordinal).Replace("sha512-YWJj", "sha512-ZGVm", StringComparison.Ordinal);
        }
        else if (scenario == "misplaced-hash")
        {
            headText += "      integrity: sha512-ZGVm\n";
        }
        const string message = "Update dependencies\n\nlodash 4.17.20 -> 4.17.21\n";
        var output = PublicOutputFixture("push_to_pull_request_branch");
        output["message"] = message;
        var result = await RunHarnessAsync(new JsonObject
        {
            ["mode"] = "agent-scrub",
            ["useGitSnapshot"] = true,
            ["baseFiles"] = new JsonObject { [path] = baseText },
            ["generatedPatch"] = new JsonObject { ["headFiles"] = new JsonObject { [path] = headText }, ["message"] = message },
            ["outputLines"] = new JsonArray(output.ToJsonString()),
            ["publicationConditions"] = CompiledPublicationConditions(),
        });

        if (accepted)
        {
            Assert.Empty(result["value"]!["violations"]!.AsArray());
            Assert.Empty(result["failures"]!.AsArray());
            Assert.Equal("true", result["stepOutputs"]!["publication_ready"]!.GetValue<string>());
        }
        else
        {
            Assert.Equal(["aw-auto-sec-security-updates.patch unbound-lockfile-metadata"],
                result["value"]!["violations"]!.AsArray().Select(v => $"{v!["source"]} {v["reason"]}"));
            Assert.Empty(result["outputs"]!.AsArray());
            Assert.Empty(result["remaining"]!.AsArray());
            Assert.Null(result["stepOutputs"]!["publication_ready"]);
            Assert.Empty(result["publications"]!.AsArray());
        }
    }

    [Theory]
    [RequiresTools(["node"])]
    [InlineData("https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-public-npm/npm/registry/other/-/other-4.17.21.tgz", "unbound-lockfile-artifact")]
    [InlineData("https://new.example/lodash/-/lodash-4.17.21.tgz", "new-package-source")]
    public async Task AgentOutputScrubEnforcesReconstructedContentPolicyBeforeUpload(string url, string reason)
    {
        var patch = PublicTextPatch.Replace("\r\n", "\n")
            .Replace("@@ -3,3 +3,3 @@", "@@ -3,3 +3,4 @@", StringComparison.Ordinal)
            .Replace("+      \"version\": \"4.17.21\",\n", $"+      \"version\": \"4.17.21\",\n+      \"resolved\": \"{url}\",\n", StringComparison.Ordinal);
        var result = await RunHarnessAsync(new JsonObject
        {
            ["mode"] = "agent-scrub",
            ["publicationConditions"] = CompiledPublicationConditions(),
            ["outputLines"] = new JsonArray(PublicOutputFixture("create_pull_request").ToJsonString()),
            ["patchFiles"] = new JsonObject { ["aw-auto-sec-security-updates.patch"] = patch },
        });

        Assert.Equal([$"aw-auto-sec-security-updates.patch {reason}"], result["value"]!["violations"]!.AsArray().Select(v => $"{v!["source"]} {v["reason"]}"));
        Assert.Empty(result["outputs"]!.AsArray());
        Assert.Empty(result["remaining"]!.AsArray());
        Assert.Null(result["stepOutputs"]!["publication_ready"]);
        Assert.Empty(result["publications"]!.AsArray());
    }

    [Theory]
    [RequiresTools(["node"])]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AgentOutputScrubRequiresCompoundNpmSelectorsToRemainUnchanged(bool changeSelector)
    {
        const string lines = "    \"lodash\": \"^4.17.20\",\n    \"foo\": \">=1 <2\"";
        const string baseText = "{\n  \"dependencies\": {\n" + lines + "\n  }\n}\n";
        var newLines = lines.Replace("4.17.20", "4.17.21", StringComparison.Ordinal);
        if (changeSelector)
        {
            newLines = newLines.Replace(">=1 <2", ">=1 <3", StringComparison.Ordinal);
        }
        var output = PublicOutputFixture("create_pull_request");
        output["body"] = output["body"]!.GetValue<string>().Replace(
            "| lodash | extension/package-lock.json | 4.17.20 | 4.17.21 |",
            "| lodash | extension/package-lock.json | 4.17.20 | 4.17.21 |\n| lodash | extension/package.json | 4.17.20 | 4.17.21 |",
            StringComparison.Ordinal);
        var dependencyPatch = ReplacementPatch("extension/package.json", baseText, lines, newLines);
        var dependencyDiff = dependencyPatch[dependencyPatch.IndexOf("diff --git", StringComparison.Ordinal)..dependencyPatch.IndexOf("\n-- \n", StringComparison.Ordinal)];
        var patch = PublicTextPatch.Replace("\r\n", "\n").Replace("\n-- \n", $"\n{dependencyDiff}\n-- \n", StringComparison.Ordinal);
        var result = await RunHarnessAsync(new JsonObject
        {
            ["mode"] = "agent-scrub",
            ["publicationConditions"] = CompiledPublicationConditions(),
            ["outputLines"] = new JsonArray(output.ToJsonString()),
            ["baseFiles"] = new JsonObject { ["extension/package-lock.json"] = PublicTextBase, ["extension/package.json"] = baseText },
            ["patchFiles"] = new JsonObject { ["aw-auto-sec-security-updates.patch"] = patch },
        });

        if (changeSelector)
        {
            Assert.Equal(["create_pull_request.body row-not-in-patch", "aw-auto-sec-security-updates.patch non-version-manifest-edit"],
                result["value"]!["violations"]!.AsArray().Select(v => $"{v!["source"]} {v["reason"]}"));
            Assert.Empty(result["outputs"]!.AsArray());
            Assert.Empty(result["remaining"]!.AsArray());
            Assert.Null(result["stepOutputs"]!["publication_ready"]);
            Assert.Empty(result["publications"]!.AsArray());
        }
        else
        {
            Assert.Empty(result["value"]!["violations"]!.AsArray());
            Assert.Single(result["outputs"]!.AsArray());
            Assert.Equal("true", result["stepOutputs"]!["publication_ready"]!.GetValue<string>());
            Assert.NotEmpty(result["publications"]!.AsArray());
        }
    }

    [Theory]
    [RequiresTools(["node"])]
    [InlineData("date")]
    [InlineData("preamble")]
    [InlineData("diffstat")]
    [InlineData("between-hunks")]
    [InlineData("hunk-heading")]
    [InlineData("newline-marker")]
    [InlineData("index-suffix")]
    [InlineData("footer")]
    [InlineData("incomplete-hunk")]
    public async Task AgentOutputScrubRejectsOpaquePatchCarrierText(string scenario)
    {
        const string privateText = "alert 42: prototype pollution in private-package";
        var patch = PublicTextPatch.Replace("\r\n", "\n");
        patch = scenario switch
        {
            "date" => patch.Replace("Date: Sat, 3 Oct 2026 09:00:00 +0000", $"Date: {privateText}", StringComparison.Ordinal),
            "preamble" => privateText + "\n" + patch,
            "diffstat" => patch.Replace("\n---\n", $"\n---\n{privateText}\n", StringComparison.Ordinal),
            "between-hunks" => patch.Replace("@@ -9,3 +9,3 @@", $"{privateText}\n@@ -9,3 +9,3 @@", StringComparison.Ordinal),
            "hunk-heading" => patch.Replace("@@ -3,3 +3,3 @@", $"@@ -3,3 +3,3 @@ {privateText}", StringComparison.Ordinal),
            "newline-marker" => patch.Replace("+      \"version\": \"4.17.21\",\n", $"+      \"version\": \"4.17.21\",\n\\ {privateText}\n", StringComparison.Ordinal),
            "index-suffix" => patch.Replace(" 100644\n", $" 100644 {privateText}\n", StringComparison.Ordinal),
            "footer" => patch.Replace("2.43.0", privateText, StringComparison.Ordinal),
            "incomplete-hunk" => patch.Replace("@@ -3,3 +3,3 @@", "@@ -3,3 +3,4 @@", StringComparison.Ordinal),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };
        var result = await RunHarnessAsync(new JsonObject
        {
            ["mode"] = "agent-scrub",
            ["publicationConditions"] = CompiledPublicationConditions(),
            ["outputLines"] = new JsonArray(PublicOutputFixture("create_pull_request").ToJsonString()),
            ["patchFiles"] = new JsonObject { ["aw-auto-sec-security-updates.patch"] = patch },
        });

        string[] expected = scenario switch
        {
            "date" => ["aw-auto-sec-security-updates.patch unexpected-commit-date"],
            "preamble" or "diffstat" => ["aw-auto-sec-security-updates.patch unsupported-patch-text"],
            "hunk-heading" => ["create_pull_request.body row-not-in-patch", "aw-auto-sec-security-updates.patch commit-row-not-in-patch", "aw-auto-sec-security-updates.patch unsupported-file-diff"],
            _ => ["create_pull_request.body row-not-in-patch", "aw-auto-sec-security-updates.patch commit-row-not-in-patch", "aw-auto-sec-security-updates.patch unsupported-file-diff", "aw-auto-sec-security-updates.patch disallowed-patch-file"],
        };
        Assert.Equal(expected, result["value"]!["violations"]!.AsArray().Select(v => $"{v!["source"]} {v["reason"]}"));
        Assert.Empty(result["outputs"]!.AsArray());
        Assert.Empty(result["remaining"]!.AsArray());
        Assert.Null(result["stepOutputs"]!["publication_ready"]);
        Assert.Empty(result["publications"]!.AsArray());
        Assert.Equal([$"auto-sec agent output scrub failed: {string.Join("; ", expected)}"], result["failures"]!.AsArray().Select(v => v!.GetValue<string>()));
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task AgentOutputScrubAllowsHunkHeadingFromTrustedBase()
    {
        var patch = PublicTextPatch.Replace("\r\n", "\n").Replace("@@ -3,3 +3,3 @@", "@@ -3,3 +3,3 @@     \"node_modules/lodash\": {", StringComparison.Ordinal);
        var result = await RunHarnessAsync(new JsonObject
        {
            ["mode"] = "agent-scrub",
            ["publicationConditions"] = CompiledPublicationConditions(),
            ["outputLines"] = new JsonArray(PublicOutputFixture("create_pull_request").ToJsonString()),
            ["patchFiles"] = new JsonObject { ["aw-auto-sec-security-updates.patch"] = patch },
        });

        Assert.Empty(result["value"]!["violations"]!.AsArray());
        Assert.Empty(result["failures"]!.AsArray());
        Assert.Equal("true", result["stepOutputs"]!["publication_ready"]!.GetValue<string>());
        Assert.Equal(CompiledPublicationConditions().Count, result["publications"]!.AsArray().Count);
    }

    [Theory]
    [RequiresTools(["node"])]
    [InlineData("aw-alert-42-prototype-pollution.patch", true)]
    [InlineData("aw-auto-sec-security-updates.bundle", false)]
    public async Task AgentOutputScrubRejectsUnreviewedArtifactNamesAndBundles(string name, bool patch)
    {
        var result = await RunHarnessAsync(new JsonObject
        {
            ["mode"] = "agent-scrub",
            ["publicationConditions"] = CompiledPublicationConditions(),
            ["outputLines"] = new JsonArray(PublicOutputFixture("create_pull_request").ToJsonString()),
            ["patchFiles"] = new JsonObject { ["aw-auto-sec-security-updates.patch"] = PublicTextPatch.Replace("\r\n", "\n") },
            ["workFiles"] = new JsonObject { [name] = patch ? PublicTextPatch.Replace("\r\n", "\n") : "alert 42: prototype pollution" },
        });

        Assert.Equal(["patch-artifacts unsupported-artifact"], result["value"]!["violations"]!.AsArray().Select(v => $"{v!["source"]} {v["reason"]}"));
        Assert.Equal(["auto-sec agent output scrub failed: patch-artifacts unsupported-artifact"], result["failures"]!.AsArray().Select(v => v!.GetValue<string>()));
        Assert.Empty(result["outputs"]!.AsArray());
        Assert.Empty(result["remaining"]!.AsArray());
        Assert.Null(result["stepOutputs"]!["publication_ready"]);
        Assert.Empty(result["publications"]!.AsArray());
    }

    [Theory]
    [RequiresTools(["node"])]
    [InlineData("workflow", "header")]
    [InlineData("workflow", "metadata")]
    [InlineData("workflow", "both")]
    [InlineData("branch", "header")]
    [InlineData("branch", "metadata")]
    [InlineData("branch", "both")]
    [InlineData("unknown", "header")]
    [InlineData("unknown", "metadata")]
    [InlineData("unknown", "both")]
    public async Task AgentOutputScrubBindsTransportBasesToTrustedRefs(string source, string carrier)
    {
        const string branchSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var sha = source switch
        {
            "workflow" => "0123456789abcdef0123456789abcdef01234567",
            "branch" => branchSha,
            "unknown" => "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            _ => throw new ArgumentOutOfRangeException(nameof(source)),
        };
        var output = PublicOutputFixture("create_pull_request");
        var patch = PublicTextPatch.Replace("\r\n", "\n");
        if (carrier is "metadata" or "both")
        {
            output["base_commit"] = sha;
        }
        if (carrier is "header" or "both")
        {
            patch = patch.Replace("From: ", $"X-GH-AW-Base-Commit: {sha}\nFrom: ", StringComparison.Ordinal);
        }
        var result = await RunHarnessAsync(new JsonObject
        {
            ["mode"] = "agent-scrub",
            ["branchSha"] = branchSha,
            ["publicationConditions"] = CompiledPublicationConditions(),
            ["outputLines"] = new JsonArray(output.ToJsonString()),
            ["patchFiles"] = new JsonObject { ["aw-auto-sec-security-updates.patch"] = patch },
        });

        if (source == "unknown")
        {
            Assert.Equal(["patch-transport untrusted-base-commit"], result["value"]!["violations"]!.AsArray().Select(v => $"{v!["source"]} {v["reason"]}"));
            Assert.Equal(["auto-sec agent output scrub failed: patch-transport untrusted-base-commit"], result["failures"]!.AsArray().Select(v => v!.GetValue<string>()));
            Assert.Empty(result["outputs"]!.AsArray());
            Assert.Empty(result["remaining"]!.AsArray());
            Assert.Null(result["stepOutputs"]!["publication_ready"]);
            Assert.Empty(result["publications"]!.AsArray());
        }
        else
        {
            Assert.Empty(result["value"]!["violations"]!.AsArray());
            Assert.Empty(result["failures"]!.AsArray());
            Assert.Equal("true", result["stepOutputs"]!["publication_ready"]!.GetValue<string>());
            Assert.Equal(CompiledPublicationConditions().Count, result["publications"]!.AsArray().Count);
        }
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task AgentOutputScrubRejectsFreeTextInFrameworkBaseHeader()
    {
        var patch = PublicTextPatch.Replace("\r\n", "\n").Replace("From: ", "X-GH-AW-Base-Commit: alert 42: prototype pollution\nFrom: ", StringComparison.Ordinal);
        var result = await RunHarnessAsync(new JsonObject
        {
            ["mode"] = "agent-scrub",
            ["publicationConditions"] = CompiledPublicationConditions(),
            ["outputLines"] = new JsonArray(PublicOutputFixture("create_pull_request").ToJsonString()),
            ["patchFiles"] = new JsonObject { ["aw-auto-sec-security-updates.patch"] = patch },
        });

        Assert.Equal(["aw-auto-sec-security-updates.patch invalid-base-commit"], result["value"]!["violations"]!.AsArray().Select(v => $"{v!["source"]} {v["reason"]}"));
        Assert.Equal(["auto-sec agent output scrub failed: aw-auto-sec-security-updates.patch invalid-base-commit"], result["failures"]!.AsArray().Select(v => v!.GetValue<string>()));
        Assert.Empty(result["outputs"]!.AsArray());
        Assert.Empty(result["remaining"]!.AsArray());
        Assert.Null(result["stepOutputs"]!["publication_ready"]);
        Assert.Empty(result["publications"]!.AsArray());
    }

    [Theory]
    [RequiresTools(["node"])]
    [InlineData("\"node_modules/@scope/name\": {", true)]
    [InlineData("\"node_modules/a/node_modules/b\": {", true)]
    [InlineData("\"dependencies\": {", true)]
    [InlineData("\"note\": {", false)]
    [InlineData("\"note\": [", false)]
    [InlineData("\"note\": true,", false)]
    [InlineData("\"note\": 42,", false)]
    [InlineData("\"note\": \"alert 42\",", false)]
    [InlineData("\"version\": \"4.17.21\",", true)]
    [InlineData("\"lodash\": \"^4.17.21 || >=5.0.0\",", true)]
    [InlineData("\"alias\": \"npm:lodash@^4.17.21\",", true)]
    [InlineData("\"node\": \"*\",", true)]
    [InlineData("\"dev\": true,", true)]
    [InlineData("\"lockfileVersion\": 3,", true)]
    [InlineData("\"integrity\": \"sha512-YWJjZA==\",", true)]
    [InlineData("\"integrity\": \"alert 42\",", false)]
    [InlineData("\"resolved\": \"https://registry.npmjs.org/lodash/-/lodash-4.17.21.tgz\",", true)]
    [InlineData("\"resolved\": \"https://registry.npmjs.org/lodash/-/lodash-4.17.21.tgz#abc123\",", true)]
    [InlineData("\"resolved\": \"https://registry.npmjs.org/lodash/-/lodash-4.17.21.tgz?note=alert42\",", false)]
    [InlineData("\"resolved\": \"https://alert42@example.com/a.tgz\",", false)]
    [InlineData("\"version\": \"4.17.21\", \"note\": \"alert 42\"", false)]
    [InlineData("\"version\": { \"note\": \"alert 42\" },", false)]
    [InlineData("\"version\": \"alert 42\",", false)]
    [InlineData("\"version\": \"1alert42\",", false)]
    [InlineData("\"license\": \"MIT\",", false)]
    public async Task JsonLockfilePublicationAcceptsOnlyTypedDependencyLines(string line, bool expected)
    {
        var result = await RunHarnessAsync(new JsonObject
        {
            ["mode"] = "call",
            ["fn"] = "isPublicJsonLockfileLine",
            ["args"] = new JsonArray(line),
        });

        Assert.Equal(expected, result["value"]!.GetValue<bool>());
    }

    [Theory]
    [RequiresTools(["node"])]
    [InlineData("yarn.lock", "  version \"4.17.21\"", true)]
    [InlineData("yarn.lock", "\"lodash@npm:^4\":", true)]
    [InlineData("yarn.lock", "  resolution: \"lodash@npm:4.17.21\"", true)]
    [InlineData("yarn.lock", "  resolved \"https://r.example/lodash/-/lodash-4.17.21.tgz#abc123\"", true)]
    [InlineData("yarn.lock", "  note \"alert 42: prototype pollution\"", false)]
    [InlineData("yarn.lock", "  note: 'alert 42'", false)]
    [InlineData("yarn.lock", "  version: \"alert 42\"", false)]
    [InlineData("pnpm-lock.yaml", "  '@scope/name@1.0.0':", true)]
    [InlineData("pnpm-lock.yaml", "    resolution:", true)]
    [InlineData("pnpm-lock.yaml", "      tarball: https://r.example/lodash/-/lodash-4.17.21.tgz", true)]
    [InlineData("pnpm-lock.yaml", "    resolution: {integrity: sha512-YWJjZA==, tarball: https://r.example/a/-/a-1.0.0.tgz}", true)]
    [InlineData("pnpm-lock.yaml", "    version: 1.0.0(foo@2.0.0(bar@3.0.0))", true)]
    [InlineData("pnpm-lock.yaml", "    note: alert 42 prototype pollution", false)]
    [InlineData("pnpm-lock.yaml", "    note: {version: 'alert 42'}", false)]
    [InlineData("pnpm-lock.yaml", "    resolution: {integrity: sha512-YWJjZA==, note: 'alert 42'}", false)]
    [InlineData("uv.lock", "[[package]]", true)]
    [InlineData("uv.lock", "name = \"jinja2\"", true)]
    [InlineData("uv.lock", "version = \"3.1.6\"", true)]
    [InlineData("uv.lock", "source = { editable = \"../pkg\" }", true)]
    [InlineData("uv.lock", "source = { registry = \"https://pypi.org/simple\" }", true)]
    [InlineData("uv.lock", "{ name = \"jinja2\", version = \"3.1.6\" },", true)]
    [InlineData("uv.lock", "{ url = \"https://f.example/jinja2-3.1.6.whl\", hash = \"sha256:abcd\", size = 123, upload-time = \"2026-01-01T01:02:03Z\" },", true)]
    [InlineData("uv.lock", "note = \"alert 42: prototype pollution\"", false)]
    [InlineData("uv.lock", "source = { registry = \"https://pypi.org/simple\", note = \"alert 42\" }", false)]
    [InlineData("uv.lock", "version = '''alert 42'''", false)]
    [InlineData("uv.lock", "note = [", false)]
    public async Task YamlAndTomlLockfilesAcceptOnlyTypedPublicationLines(string path, string line, bool expected)
    {
        var result = await RunHarnessAsync(new JsonObject
        {
            ["mode"] = "call",
            ["fn"] = "isPublicLockfileLine",
            ["args"] = new JsonArray(path, line),
        });

        Assert.Equal(expected, result["value"]!.GetValue<bool>());
    }

    [Theory]
    [RequiresTools(["node"])]
    [InlineData("yarn.lock", "  note \"alert 42 prototype pollution\"")]
    [InlineData("pnpm-lock.yaml", "    note: 'alert 42 prototype pollution'")]
    [InlineData("uv.lock", "note = \"alert 42 prototype pollution\"")]
    public async Task AgentOutputScrubBlocksUnknownYamlAndTomlMetadataBeforeUpload(string path, string line)
    {
        var baseText = path == "uv.lock" ? "version = 1\n" : "lockfileVersion: 9.0\n";
        var patch = PublicTextPatch.Replace("\r\n", "\n").Replace("-- \n",
            $"diff --git a/{path} b/{path}\nindex {GitBlobId(baseText)[..7]}..2222222 100644\n--- a/{path}\n+++ b/{path}\n@@ -1 +1,2 @@\n {baseText.TrimEnd()}\n+{line}\n-- \n", StringComparison.Ordinal);
        var result = await RunHarnessAsync(new JsonObject
        {
            ["mode"] = "agent-scrub",
            ["outputLines"] = new JsonArray(PublicOutputFixture("create_pull_request").ToJsonString()),
            ["patchFiles"] = new JsonObject { ["aw-auto-sec-security-updates.patch"] = patch },
            ["publicationConditions"] = CompiledPublicationConditions(),
            ["baseFiles"] = new JsonObject { ["extension/package-lock.json"] = PublicTextBase, [path] = baseText },
        });

        Assert.Equal(["aw-auto-sec-security-updates.patch unsupported-lockfile-text"], result["value"]!["violations"]!.AsArray().Select(v => $"{v!["source"]} {v["reason"]}"));
        Assert.Empty(result["outputs"]!.AsArray());
        Assert.Empty(result["remaining"]!.AsArray());
        Assert.Null(result["stepOutputs"]!["publication_ready"]);
        Assert.Empty(result["publications"]!.AsArray());
    }

    [Theory]
    [RequiresTools(["node"])]
    [InlineData("wrong-manifest")]
    [InlineData("unrelated-package")]
    [InlineData("version-substring")]
    [InlineData("wrong-base-version")]
    public async Task PublicTextRowsRequireOneReconstructedUpdateTuple(string scenario)
    {
        var body = PublicTextBody.Replace("\r\n", "\n");
        body = scenario switch
        {
            "wrong-manifest" => body.Replace("extension/package-lock.json", "other/package-lock.json", StringComparison.Ordinal),
            "unrelated-package" => body.Replace("| lodash |", "| @types/node |", StringComparison.Ordinal),
            "version-substring" => body.Replace("| 4.17.21 |", "| 4.17.2 |", StringComparison.Ordinal),
            "wrong-base-version" => body.Replace("| 4.17.20 |", "| 4.17.19 |", StringComparison.Ordinal),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };
        var output = PublicOutputFixture("create_pull_request");
        output["body"] = body;
        var result = await RunHarnessAsync(new JsonObject
        {
            ["mode"] = "agent-scrub",
            ["outputLines"] = new JsonArray(output.ToJsonString()),
            ["patchFiles"] = new JsonObject { ["aw-auto-sec-security-updates.patch"] = PublicTextPatch.Replace("\r\n", "\n") },
            ["publicationConditions"] = CompiledPublicationConditions(),
        });

        Assert.Equal(["create_pull_request.body row-not-in-patch"], result["value"]!["violations"]!.AsArray().Select(v => $"{v!["source"]} {v["reason"]}"));
        Assert.Empty(result["outputs"]!.AsArray());
        Assert.Empty(result["remaining"]!.AsArray());
        Assert.Null(result["stepOutputs"]!["publication_ready"]);
        Assert.Empty(result["publications"]!.AsArray());
    }

    [Theory]
    [RequiresTools(["node", "git"])]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AgentOutputScrubReadsWorkflowSnapshotAndFailsClosedWhenUnavailable(bool missing)
    {
        var result = await RunHarnessAsync(new JsonObject
        {
            ["mode"] = "agent-scrub",
            ["useGitSnapshot"] = true,
            ["missingGitSnapshot"] = missing,
            ["outputLines"] = new JsonArray(PublicOutputFixture("create_pull_request").ToJsonString()),
            ["patchFiles"] = new JsonObject { ["aw-auto-sec-security-updates.patch"] = PublicTextPatch.Replace("\r\n", "\n") },
            ["publicationConditions"] = CompiledPublicationConditions(),
        });

        Assert.Equal(missing ? ["filesystem io-error"] : [], result["value"]!["violations"]!.AsArray().Select(v => $"{v!["source"]} {v["reason"]}"));
        Assert.Equal(missing ? ["auto-sec agent output scrub failed: filesystem io-error"] : [], result["failures"]!.AsArray().Select(v => v!.GetValue<string>()));
        Assert.Equal(missing ? null : "true", result["stepOutputs"]!["publication_ready"]?.GetValue<string>());
        Assert.Equal(missing ? 0 : CompiledPublicationConditions().Count, result["publications"]!.AsArray().Count);
    }

    [Theory]
    [RequiresTools(["node", "git"])]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public async Task AgentOutputScrubAcceptsActualGitFormatPatch(bool fullIndex, bool embedBaseCommit, bool repoScoped)
    {
        var result = await RunHarnessAsync(new JsonObject
        {
            ["mode"] = "agent-scrub",
            ["useGitSnapshot"] = true,
            ["generatedPatch"] = new JsonObject
            {
                ["headFiles"] = new JsonObject { ["extension/package-lock.json"] = PublicTextBase.Replace("4.17.20", "4.17.21", StringComparison.Ordinal).Replace("22.0.0", "22.1.0", StringComparison.Ordinal) },
                ["message"] = "Update dependencies\n\nlodash 4.17.20 -> 4.17.21\n@types/node 22.0.0 -> 22.1.0\n",
                ["fullIndex"] = fullIndex,
                ["embedBaseCommit"] = embedBaseCommit,
                ["repoScoped"] = repoScoped,
            },
            ["outputLines"] = new JsonArray(PublicOutputFixture("create_pull_request").ToJsonString()),
            ["publicationConditions"] = CompiledPublicationConditions(),
        });

        Assert.Empty(result["value"]!["violations"]!.AsArray());
        Assert.Empty(result["failures"]!.AsArray());
        Assert.Single(result["outputs"]!.AsArray());
        Assert.Equal([repoScoped ? "aw-microsoft-aspire-auto-sec-security-updates.patch" : "aw-auto-sec-security-updates.patch"], result["remaining"]!.AsArray().Select(v => v!.GetValue<string>()));
        Assert.Equal("true", result["stepOutputs"]!["publication_ready"]!.GetValue<string>());
        Assert.Equal(CompiledPublicationConditions().Count, result["publications"]!.AsArray().Count);
    }

    [Fact]
    [RequiresTools(["node", "git"])]
    public async Task AgentOutputScrubReadsBotBranchBaseWhenWorkflowSnapshotIsUnavailable()
    {
        const string branchSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var patch = PublicTextPatch.Replace("\r\n", "\n").Replace("From: ", $"X-GH-AW-Base-Commit: {branchSha}\nFrom: ", StringComparison.Ordinal);
        var result = await RunHarnessAsync(new JsonObject
        {
            ["mode"] = "agent-scrub",
            ["useGitSnapshot"] = true,
            ["missingGitSnapshot"] = true,
            ["branchFiles"] = new JsonObject { ["extension/package-lock.json"] = PublicTextBase },
            ["branchSha"] = branchSha,
            ["outputLines"] = new JsonArray(PublicOutputFixture("create_pull_request").ToJsonString()),
            ["patchFiles"] = new JsonObject { ["aw-auto-sec-security-updates.patch"] = patch },
            ["publicationConditions"] = CompiledPublicationConditions(),
        });

        Assert.Empty(result["value"]!["violations"]!.AsArray());
        Assert.Empty(result["failures"]!.AsArray());
        Assert.Equal("true", result["stepOutputs"]!["publication_ready"]!.GetValue<string>());
        Assert.Equal(CompiledPublicationConditions().Count, result["publications"]!.AsArray().Count);
    }

    [Fact]
    [RequiresTools(["node", "git"])]
    public async Task AgentWrittenGitBlobsCannotAuthorizePublicSummaryRows()
    {
        var forgedBase = PublicTextBase.Replace("4.17.20", "4.17.19", StringComparison.Ordinal);
        var patch = PublicTextPatch.Replace("\r\n", "\n")
            .Replace("4.17.20", "4.17.19", StringComparison.Ordinal)
            .Replace(GitBlobId(PublicTextBase)[..7], GitBlobId(forgedBase)[..7], StringComparison.Ordinal);
        var output = PublicOutputFixture("create_pull_request");
        output["body"] = PublicTextBody.Replace("\r\n", "\n").Replace("4.17.20", "4.17.19", StringComparison.Ordinal);
        var result = await RunHarnessAsync(new JsonObject
        {
            ["mode"] = "agent-scrub",
            ["useGitSnapshot"] = true,
            ["extraBlobs"] = new JsonArray(forgedBase),
            ["outputLines"] = new JsonArray(output.ToJsonString()),
            ["patchFiles"] = new JsonObject { ["aw-auto-sec-security-updates.patch"] = patch },
            ["publicationConditions"] = CompiledPublicationConditions(),
        });

        Assert.Equal(["create_pull_request.body row-not-in-patch", "aw-auto-sec-security-updates.patch commit-row-not-in-patch", "aw-auto-sec-security-updates.patch unreconstructable-file-diff"],
            result["value"]!["violations"]!.AsArray().Select(v => $"{v!["source"]} {v["reason"]}"));
        Assert.Empty(result["outputs"]!.AsArray());
        Assert.Empty(result["remaining"]!.AsArray());
        Assert.Null(result["stepOutputs"]!["publication_ready"]);
        Assert.Empty(result["publications"]!.AsArray());
    }

    [Theory]
    [RequiresTools(["node"])]
    [InlineData("workspace")]
    [InlineData("auto-sec-branch")]
    public async Task PublicTextGateReconstructsFromTheAuthorizedBaseCandidates(string source)
    {
        var request = new JsonObject
        {
            ["mode"] = "public-text-gate",
            ["useCheckedOutBases"] = true,
            ["agentItems"] = new JsonArray(PublicOutputFixture("create_pull_request")),
            ["patchFiles"] = new JsonObject { ["aw-auto-sec-security-updates.patch"] = PublicTextPatch.Replace("\r\n", "\n") },
        };
        request[source == "workspace" ? "workspaceFiles" : "branchFiles"] = new JsonObject { ["extension/package-lock.json"] = PublicTextBase };
        var result = await RunHarnessAsync(request);

        Assert.Empty(result["value"]!["violations"]!.AsArray());
        Assert.Empty(result["failures"]!.AsArray());
    }

    [Theory]
    [RequiresTools(["node"])]
    [InlineData("create_pull_request", false)]
    [InlineData("create_pull_request", true)]
    [InlineData("push_to_pull_request_branch", false)]
    [InlineData("push_to_pull_request_branch", true)]
    [InlineData("noop", false)]
    [InlineData("noop", true)]
    [InlineData("approve_dependabot_pr", false)]
    [InlineData("approve_dependabot_pr", true)]
    public async Task AgentOutputScrubProjectsEveryRetainedType(string type, bool nested)
    {
        var expected = PublicOutputFixture(type);
        var input = expected.DeepClone().AsObject();
        input["private advisory property GHSA-xxxx"] = nested
            ? new JsonObject { ["private advisory"] = new JsonArray("alert 42") }
            : JsonValue.Create("alert 42");
        var result = await RunHarnessAsync(new JsonObject
        {
            ["mode"] = "agent-scrub",
            ["publicationConditions"] = CompiledPublicationConditions(),
            ["outputLines"] = new JsonArray(input.ToJsonString()),
            ["patchFiles"] = new JsonObject { ["aw-auto-sec-security-updates.patch"] = PublicTextPatch.Replace("\r\n", "\n") },
        });

        Assert.Empty(result["failures"]!.AsArray());
        Assert.Empty(result["value"]!["violations"]!.AsArray());
        var output = JsonNode.Parse(Assert.Single(result["outputs"]!.AsArray())!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(expected, output));
        Assert.Equal("true", result["stepOutputs"]!["publication_ready"]!.GetValue<string>());
        Assert.Equal(CompiledPublicationConditions().Count, result["publications"]!.AsArray().Count);
    }

    [Theory]
    [RequiresTools(["node"])]
    [InlineData("create_pull_request")]
    [InlineData("push_to_pull_request_branch")]
    public async Task AgentOutputScrubPreservesValidatedFrameworkTransportFields(string type)
    {
        var expected = PublicOutputFixture(type);
        expected["branch"] = "auto-sec/security-updates";
        expected["repo"] = "microsoft/aspire";
        expected["head_repo"] = "microsoft/aspire";
        expected["base"] = "main";
        expected["base_branch"] = "main";
        expected["base_commit"] = "0123456789abcdef0123456789abcdef01234567";
        expected["diff_size"] = 123;
        var input = expected.DeepClone().AsObject();
        input["repo_cwd"] = "/private/alert-42";
        var result = await RunHarnessAsync(new JsonObject
        {
            ["mode"] = "agent-scrub",
            ["outputLines"] = new JsonArray(input.ToJsonString()),
            ["patchFiles"] = new JsonObject { ["aw-auto-sec-security-updates.patch"] = PublicTextPatch.Replace("\r\n", "\n") },
        });

        Assert.Empty(result["failures"]!.AsArray());
        var output = JsonNode.Parse(Assert.Single(result["outputs"]!.AsArray())!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(expected, output));
        Assert.Equal("true", result["stepOutputs"]!["publication_ready"]!.GetValue<string>());
    }

    [Theory]
    [RequiresTools(["node"])]
    [InlineData("create_pull_request", "branch", "\"alert 42\"")]
    [InlineData("create_pull_request", "branch", "{\"advisory\":\"alert 42\"}")]
    [InlineData("create_pull_request", "title", "[\"Automated dependency updates\"]")]
    [InlineData("create_pull_request", "body", "null")]
    [InlineData("push_to_pull_request_branch", "pull_request_number", "\"alert 42\"")]
    [InlineData("push_to_pull_request_branch", "pull_request_number", "[202]")]
    [InlineData("push_to_pull_request_branch", "pull_request_number", "0")]
    [InlineData("push_to_pull_request_branch", "pull_request_number", "202.5")]
    [InlineData("push_to_pull_request_branch", "message", "[\"Update dependencies\"]")]
    [InlineData("noop", "message", "[\"alerts=1 dependabot-pr=0 auto-sec-pr=1 blocked=0 code-findings-out-of-scope=0\"]")]
    [InlineData("approve_dependabot_pr", "pr_number", "[101]")]
    [InlineData("approve_dependabot_pr", "head_sha", "[\"0123456789abcdef0123456789abcdef01234567\"]")]
    [InlineData("create_pull_request", "repo", "\"private advisory\"")]
    [InlineData("create_pull_request", "head_repo", "{\"advisory\":\"alert 42\"}")]
    [InlineData("create_pull_request", "base", "\"alert-42\"")]
    [InlineData("create_pull_request", "base_branch", "\"alert-42\"")]
    [InlineData("push_to_pull_request_branch", "base_commit", "\"alert 42\"")]
    [InlineData("push_to_pull_request_branch", "diff_size", "{\"advisory\":\"alert 42\"}")]
    public async Task AgentOutputScrubRejectsNonPrimitiveOrInvalidRetainedFields(string type, string field, string value)
    {
        var input = PublicOutputFixture(type);
        input[field] = JsonNode.Parse(value);
        var result = await RunHarnessAsync(new JsonObject
        {
            ["mode"] = "agent-scrub",
            ["publicationConditions"] = CompiledPublicationConditions(),
            ["outputLines"] = new JsonArray(input.ToJsonString()),
            ["patchFiles"] = new JsonObject { ["aw-auto-sec-security-updates.patch"] = PublicTextPatch.Replace("\r\n", "\n") },
        });

        Assert.Equal([$"{type} invalid-inputs"], result["value"]!["violations"]!.AsArray().Select(v => $"{v!["source"]} {v["reason"]}"));
        Assert.Equal([$"auto-sec agent output scrub failed: {type} invalid-inputs"], result["failures"]!.AsArray().Select(v => v!.GetValue<string>()));
        Assert.Empty(result["outputs"]!.AsArray());
        Assert.Empty(result["remaining"]!.AsArray());
        Assert.Null(result["stepOutputs"]!["publication_ready"]);
        Assert.Empty(result["publications"]!.AsArray());
    }

    private static JsonObject PublicOutputFixture(string type) => type switch
    {
        "create_pull_request" => new JsonObject { ["type"] = type, ["branch"] = "auto-sec/security-updates", ["title"] = "Automated dependency updates", ["body"] = PublicTextBody.Replace("\r\n", "\n") },
        "push_to_pull_request_branch" => new JsonObject { ["type"] = type, ["pull_request_number"] = 202, ["message"] = "Update dependencies\n\nlodash 4.17.20 -> 4.17.21\n@types/node 22.0.0 -> 22.1.0" },
        "approve_dependabot_pr" => new JsonObject { ["type"] = type, ["pr_number"] = 101, ["head_sha"] = "0123456789abcdef0123456789abcdef01234567" },
        "noop" => new JsonObject { ["type"] = type, ["message"] = "alerts=1 dependabot-pr=0 auto-sec-pr=1 blocked=0 code-findings-out-of-scope=0" },
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    [Theory]
    [RequiresTools(["node"])]
    [InlineData("noop-free-text", "noop.message non-template-text")]
    [InlineData("malformed-line", "line 2 malformed-output")]
    [InlineData("approval-invalid-sha", "approve_dependabot_pr invalid-inputs")]
    [InlineData("body-free-text", "create_pull_request.body non-template-text")]
    // Patches ship in the public `agent` artifact, so their content is checked here too.
    [InlineData("patch-alert-file", "aw-auto-sec-security-updates.patch disallowed-patch-file")]
    [InlineData("patch-advisory-context", "aw-auto-sec-security-updates.patch forbidden-public-text")]
    [InlineData("patch-lockfile-comment", "aw-auto-sec-security-updates.patch lockfile-comment")]
    public async Task AgentOutputScrubEmptiesOutputsWhenAnyTextIsOffTemplate(string scenario, string expected)
    {
        var body = PublicTextBody.Replace("\r\n", "\n");
        var patch = PublicTextPatch.Replace("\r\n", "\n");
        var second = new JsonObject { ["type"] = "noop", ["message"] = "alerts=1 dependabot-pr=0 auto-sec-pr=1 blocked=0 code-findings-out-of-scope=0" }.ToJsonString();
        switch (scenario)
        {
            case "noop-free-text":
                second = new JsonObject { ["type"] = "noop", ["message"] = "Fixed alert 42 in lodash" }.ToJsonString();
                break;
            case "malformed-line":
                second = "{ alert 42";
                break;
            case "approval-invalid-sha":
                second = new JsonObject { ["type"] = "approve_dependabot_pr", ["pr_number"] = 101, ["head_sha"] = "alert 42" }.ToJsonString();
                break;
            case "body-free-text":
                body += "\n\nThis resolves alert 42.";
                break;
            case "patch-alert-file":
                patch = patch.Replace("-- \n", "diff --git a/.auto-sec/alerts.json b/.auto-sec/alerts.json\nindex 1111111..2222222 100644\n--- a/.auto-sec/alerts.json\n+++ b/.auto-sec/alerts.json\n@@ -1 +1 @@\n-[]\n+[{\"number\":42}]\n-- \n");
                break;
            case "patch-advisory-context":
                patch = patch.Replace("     \"node_modules/lodash\": {", "     \"node_modules/lodash\": { \"note\": \"GHSA-xxxx\",");
                break;
            case "patch-lockfile-comment":
                patch = patch.Replace("-- \n", "diff --git a/app/yarn.lock b/app/yarn.lock\nindex 1111111..2222222 100644\n--- a/app/yarn.lock\n+++ b/app/yarn.lock\n@@ -1 +1,2 @@\n lodash@^4.17.20:\n+# alert 42\n-- \n");
                break;
        }

        var result = await RunHarnessAsync(new JsonObject
        {
            ["mode"] = "agent-scrub",
            ["outputLines"] = new JsonArray(
                new JsonObject { ["type"] = "create_pull_request", ["branch"] = "auto-sec/security-updates", ["title"] = "Automated dependency updates", ["body"] = body }.ToJsonString(),
                second),
            ["patchFiles"] = new JsonObject { ["aw-auto-sec-security-updates.patch"] = patch },
        });

        string[] expectedViolations = scenario switch
        {
            "patch-advisory-context" => ["create_pull_request.body row-not-in-patch", "aw-auto-sec-security-updates.patch commit-row-not-in-patch", "aw-auto-sec-security-updates.patch unreconstructable-file-diff", expected],
            "patch-alert-file" or "patch-lockfile-comment" => ["aw-auto-sec-security-updates.patch unreconstructable-file-diff", expected],
            _ => [expected],
        };
        Assert.Equal(expectedViolations, result["value"]!["violations"]!.AsArray().Select(v => $"{v!["source"]} {v["reason"]}"));
        Assert.Empty(result["outputs"]!.AsArray());
        Assert.Empty(result["remaining"]!.AsArray());
        Assert.Null(result["stepOutputs"]!["publication_ready"]);
        // The failure names the field and reason only, never the rejected text.
        var failure = Assert.Single(result["failures"]!.AsArray())!.GetValue<string>();
        Assert.DoesNotContain("alert 42", failure, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [RequiresTools(["node"])]
    [InlineData("rmSync", "sandbox", false)]
    [InlineData("rmSync", "agent_execution.json", false)]
    [InlineData("readFileSync", "outputs.jsonl", false)]
    [InlineData("readFileSync", "aw-auto-sec-security-updates.patch", false)]
    [InlineData("lstatSync", "aw-auto-sec-security-updates.patch", false)]
    [InlineData("readdirSync", "", false)]
    [InlineData("writeFileSync", "outputs.jsonl", false)]
    [InlineData("writeFileSync", "outputs.jsonl", true)]
    [InlineData("rmSync", "aw-auto-sec-security-updates.patch", true)]
    public async Task AgentOutputScrubIoFailuresNeverAuthorizePublication(string operation, string path, bool invalidOutput)
    {
        var result = await RunHarnessAsync(new JsonObject
        {
            ["mode"] = "agent-scrub",
            ["ioFailure"] = new JsonObject { ["operation"] = operation, ["path"] = path },
            ["publicationConditions"] = CompiledPublicationConditions(),
            ["outputLines"] = new JsonArray(new JsonObject
            {
                ["type"] = "noop",
                ["message"] = invalidOutput ? "synthetic private output" : "alerts=1 dependabot-pr=0 auto-sec-pr=0 blocked=1 (update-failed=1) code-findings-out-of-scope=0",
            }.ToJsonString()),
            ["patchFiles"] = new JsonObject { ["aw-auto-sec-security-updates.patch"] = PublicTextPatch.Replace("\r\n", "\n") },
            ["workFiles"] = new JsonObject
            {
                ["sandbox/agent/logs/session.jsonl"] = "synthetic private transcript",
                ["agent_execution.json"] = "synthetic private telemetry",
            },
        });

        Assert.Equal(["filesystem io-error"], result["value"]!["violations"]!.AsArray().Select(v => $"{v!["source"]} {v["reason"]}"));
        Assert.Equal(["auto-sec agent output scrub failed: filesystem io-error"], result["failures"]!.AsArray().Select(v => v!.GetValue<string>()));
        Assert.Null(result["stepOutputs"]!["publication_ready"]);
        Assert.Empty(result["publications"]!.AsArray());
        Assert.NotEmpty(result["remaining"]!.AsArray());
        AssertPublicationGuards(File.ReadAllText(Path.Combine(RepoRoot.Path, ".github", "workflows", "auto-sec.lock.yml")));
    }

    [Theory]
    [RequiresTools(["node"])]
    [InlineData("success", "true", true)]
    [InlineData("success", null, false)]
    [InlineData("success", "false", false)]
    [InlineData("failure", "true", false)]
    [InlineData("skipped", "true", false)]
    [InlineData("cancelled", "true", false)]
    public async Task CompiledPublicationsRequireSuccessfulScrubAndExplicitAuthorization(string outcome, string? ready, bool expected)
    {
        var conditions = CompiledPublicationConditions();
        var result = await RunHarnessAsync(new JsonObject
        {
            ["mode"] = "publication-decisions",
            ["conditions"] = conditions,
            ["outcome"] = outcome,
            ["stepOutputs"] = ready is null ? new JsonObject() : new JsonObject { ["publication_ready"] = ready },
        });
        Assert.Equal(expected ? conditions.Count : 0, result["publications"]!.AsArray().Count);
    }

    [Theory]
    [RequiresTools(["node"])]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public async Task PublicationGuardPreservesOtherJobsAndGatesEveryLaterStep(string newline)
    {
        var workflow = PublicationGuardFixture.ReplaceLineEndings(newline);
        var result = await RunHarnessAsync(new JsonObject { ["mode"] = "publication-guard", ["workflow"] = workflow });
        var guarded = result["value"]!.GetValue<string>();
        Assert.Equal(guarded, result["repeated"]!.GetValue<string>());
        AssertPublicationGuards(guarded);
        AssertSafeUploadPaths(guarded);
        Assert.Equal(
            workflow[..workflow.IndexOf("  agent:", StringComparison.Ordinal)],
            guarded[..guarded.IndexOf("  agent:", StringComparison.Ordinal)]);
        Assert.Equal(
            workflow[workflow.IndexOf("  other_job:", StringComparison.Ordinal)..],
            guarded[guarded.IndexOf("  other_job:", StringComparison.Ordinal)..]);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task CallHarnessAwaitsAsyncGateResults()
    {
        var result = await RunHarnessAsync(new JsonObject
        {
            ["mode"] = "call",
            ["fn"] = "checkPatchVersionPolicy",
            ["args"] = new JsonArray(new JsonArray()),
        });

        Assert.Empty(result["value"]!.AsArray());
    }

    [Theory]
    [RequiresTools(["node", "bash"])]
    [InlineData(0)]
    [InlineData(23)]
    public async Task PublicationGuardSuppressesLiveAgentStreamsWithoutChangingExitStatus(int exitCode)
    {
        var script = $"set -x\nsh -c 'printf \"private-alert-123\\n\"; printf \"private-range-malware\\n\" >&2; exit {exitCode}'\n";
        var unguarded = await RunHarnessAsync(new JsonObject { ["mode"] = "private-shell", ["script"] = script });
        Assert.Equal("private-alert-123\n", unguarded["stdout"]!.GetValue<string>().ReplaceLineEndings("\n"));
        Assert.Contains("private-range-malware", unguarded["stderr"]!.GetValue<string>());
        Assert.Equal(exitCode, unguarded["exitCode"]!.GetValue<int>());

        var workflow = PublicationGuardFixture.ReplaceLineEndings("\n").Replace(
            "          echo private-agent\n", string.Concat(script.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => $"          {line}\n")),
            StringComparison.Ordinal);
        var result = await RunHarnessAsync(new JsonObject { ["mode"] = "publication-guard", ["workflow"] = workflow });
        var guarded = result["value"]!.GetValue<string>();
        var execution = Assert.Single(AgentSteps(guarded),
            step => step.Children.TryGetValue(new YamlScalarNode("id"), out var id) && id.ToString() == "agentic_execution");
        var quiet = await RunHarnessAsync(new JsonObject
        {
            ["mode"] = "private-shell",
            ["script"] = execution.Children[new YamlScalarNode("run")].ToString(),
        });

        Assert.Equal("", quiet["stdout"]!.GetValue<string>());
        Assert.Equal("", quiet["stderr"]!.GetValue<string>());
        Assert.Equal(exitCode, quiet["exitCode"]!.GetValue<int>());
        Assert.Equal(guarded, result["repeated"]!.GetValue<string>());
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task PublicationGuardWithholdsPrivateDiagnosticsBeforeScrubbing()
    {
        var result = await RunHarnessAsync(new JsonObject { ["mode"] = "publication-guard", ["workflow"] = PublicationGuardFixture });
        var steps = AgentSteps(result["value"]!.GetValue<string>());
        foreach (var name in new[] { "Detect agent errors", "Redact secrets in logs" })
        {
            var step = Assert.Single(steps, step => step.Children[new YamlScalarNode("name")].ToString() == name);
            Assert.Equal("echo \"Auto-sec withholds agent-writable telemetry.\"", step.Children[new YamlScalarNode("run")].ToString());
            Assert.Equal("always()", step.Children[new YamlScalarNode("if")].ToString());
            Assert.False(step.Children.ContainsKey(new YamlScalarNode("uses")));
            Assert.False(step.Children.ContainsKey(new YamlScalarNode("with")));
        }
        var cleanup = Assert.Single(steps, step => step.Children[new YamlScalarNode("name")].ToString() == "Stop MCP Gateway");
        Assert.Equal("exec >/dev/null 2>&1\nbash stop-gateway.sh\n", cleanup.Children[new YamlScalarNode("run")].ToString());
    }

    [Theory]
    [RequiresTools(["node", "bash"])]
    [InlineData(0)]
    [InlineData(23)]
    public async Task PublicationGuardPreservesGatewayMaskAndSuppressesBackgroundChildStreams(int exitCode)
    {
        var script = $"sh -c 'printf \"private-gateway\\n\"; printf \"private-gateway-error\\n\" >&2; exit {exitCode}' &\nwait $!\n";
        var original = await RunHarnessAsync(new JsonObject { ["mode"] = "private-shell", ["script"] = script });
        Assert.Equal("private-gateway\n", original["stdout"]!.GetValue<string>().ReplaceLineEndings("\n"));
        Assert.Equal("private-gateway-error\n", original["stderr"]!.GetValue<string>().ReplaceLineEndings("\n"));
        Assert.Equal(exitCode, original["exitCode"]!.GetValue<int>());
        var workflow = PublicationGuardFixture.ReplaceLineEndings("\n").Replace(
            "          echo start-gateway\n", string.Concat(script.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => $"          {line}\n")),
            StringComparison.Ordinal);
        var result = await RunHarnessAsync(new JsonObject { ["mode"] = "publication-guard", ["workflow"] = workflow });
        var gateway = Assert.Single(AgentSteps(result["value"]!.GetValue<string>()),
            step => step.Children[new YamlScalarNode("name")].ToString() == "Start MCP Gateway");
        var quiet = await RunHarnessAsync(new JsonObject
        {
            ["mode"] = "private-shell",
            ["script"] = gateway.Children[new YamlScalarNode("run")].ToString(),
        });

        Assert.Equal("::add-mask::fixture-gateway-id\n", quiet["stdout"]!.GetValue<string>().ReplaceLineEndings("\n"));
        Assert.Equal("", quiet["stderr"]!.GetValue<string>());
        Assert.Equal(exitCode, quiet["exitCode"]!.GetValue<int>());
        Assert.Equal(result["value"]!.GetValue<string>(), result["repeated"]!.GetValue<string>());
    }

    [Theory]
    [RequiresTools(["node"])]
    [InlineData("\n", "      - run: echo future")]
    [InlineData("\r\n", "      - run: echo future")]
    [InlineData("\n", "      - name: Print firewall logs")]
    [InlineData("\r\n", "      - name: Print firewall logs")]
    public async Task PublicationGuardHandlesTerminalSingleLineSteps(string newline, string terminal)
    {
        var workflow = PublicationGuardFixture.ReplaceLineEndings("\n");
        workflow = (workflow[..workflow.IndexOf("  other_job:", StringComparison.Ordinal)].TrimEnd('\n')
            + "\n" + terminal).ReplaceLineEndings(newline);
        var result = await RunHarnessAsync(new JsonObject { ["mode"] = "publication-guard", ["workflow"] = workflow });

        Assert.Null(result["error"]);
        var guarded = result["value"]!.GetValue<string>();
        Assert.Equal(guarded, result["repeated"]!.GetValue<string>());
        AssertPublicationGuards(guarded);
        AssertSafeUploadPaths(guarded);
        if (terminal.Contains("name:", StringComparison.Ordinal))
        {
            Assert.Equal("echo \"Auto-sec withholds agent-writable telemetry.\"",
                PublicationSteps(guarded).Last().Children[new YamlScalarNode("run")].ToString());
        }
    }

    [Theory]
    [RequiresTools(["node"])]
    [InlineData("\n", false)]
    [InlineData("\r\n", false)]
    [InlineData("\n", true)]
    [InlineData("\r\n", true)]
    public async Task PublicationGuardRewritesUploadPathAtEndOfFile(string newline, bool blankLine)
    {
        var workflow = PublicationGuardFixture.ReplaceLineEndings("\n");
        if (blankLine)
        {
            workflow = workflow.Replace("            /tmp/gh-aw/aw-prompts/prompt.txt", "\n            /tmp/gh-aw/aw-prompts/prompt.txt", StringComparison.Ordinal);
        }
        workflow = workflow[..workflow.IndexOf("  other_job:", StringComparison.Ordinal)].TrimEnd('\n').ReplaceLineEndings(newline);
        var result = await RunHarnessAsync(new JsonObject { ["mode"] = "publication-guard", ["workflow"] = workflow });

        var guarded = result["value"]!.GetValue<string>();
        Assert.Equal(guarded, result["repeated"]!.GetValue<string>());
        AssertPublicationGuards(guarded);
        AssertSafeUploadPaths(guarded);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task PublicationGuardInsertsConditionAfterHeaderWithMixedNewlines()
    {
        var workflow = PublicationGuardFixture.ReplaceLineEndings("\n").Replace(
            "        run: echo future\n",
            "        run: |\n          echo first\r\n          echo second\n",
            StringComparison.Ordinal);
        var result = await RunHarnessAsync(new JsonObject { ["mode"] = "publication-guard", ["workflow"] = workflow });

        var guarded = result["value"]!.GetValue<string>();
        Assert.Equal(guarded, result["repeated"]!.GetValue<string>());
        AssertPublicationGuards(guarded);
        var step = Assert.Single(PublicationSteps(guarded),
            step => step.Children[new YamlScalarNode("name")].ToString() == "Future publication");
        Assert.Equal("echo first\necho second\n", step.Children[new YamlScalarNode("run")].ToString().ReplaceLineEndings("\n"));
    }

    [Theory]
    [RequiresTools(["node"])]
    [InlineData("\n", true)]
    [InlineData("\n", false)]
    [InlineData("\r\n", true)]
    [InlineData("\r\n", false)]
    public async Task PublicationGuardReplacesEveryTelemetryConsumer(string newline, bool conditional)
    {
        var workflow = PublicationGuardFixture.ReplaceLineEndings("\n");
        var telemetry = string.Concat(s_privateTelemetryStepNames.Select((name, index) =>
            $"      - name: {name}\n        id: private_telemetry_{index}\n"
            + (conditional ? "        if: always()\n" : "")
            + "        uses: actions/github-script@example\n        env:\n          INPUT_PATH: /tmp/private-input\n"
            + "        with:\n          script: |\n            console.log('untrusted telemetry');\n"));
        workflow = workflow.Replace(
            "      - name: Append agent step summary\n        if: always()\n        run: echo summary\n",
            telemetry, StringComparison.Ordinal).ReplaceLineEndings(newline);
        var result = await RunHarnessAsync(new JsonObject { ["mode"] = "publication-guard", ["workflow"] = workflow });

        var guarded = result["value"]!.GetValue<string>();
        Assert.Equal(guarded, result["repeated"]!.GetValue<string>());
        AssertPublicationGuards(guarded);
        var steps = PublicationSteps(guarded);
        for (var i = 0; i < s_privateTelemetryStepNames.Length; i++)
        {
            var step = Assert.Single(steps, step => step.Children[new YamlScalarNode("name")].ToString() == s_privateTelemetryStepNames[i]);
            Assert.Equal(["id", "if", "name", "run"], step.Children.Keys.Select(key => key.ToString()).Order(StringComparer.Ordinal));
            Assert.Equal($"private_telemetry_{i}", step.Children[new YamlScalarNode("id")].ToString());
            Assert.Equal(conditional
                ? "(always()) && steps.auto_sec_scrub.outcome == 'success' && steps.auto_sec_scrub.outputs.publication_ready == 'true'"
                : "(success()) && steps.auto_sec_scrub.outcome == 'success' && steps.auto_sec_scrub.outputs.publication_ready == 'true'",
                step.Children[new YamlScalarNode("if")].ToString());
            Assert.Equal("echo \"Auto-sec withholds agent-writable telemetry.\"", step.Children[new YamlScalarNode("run")].ToString());
        }
    }

    [Theory]
    [RequiresTools(["node"])]
    [InlineData("missing-agent")]
    [InlineData("missing-scrub")]
    [InlineData("duplicate-scrub")]
    [InlineData("publication-before-scrub")]
    [InlineData("missing-upload")]
    [InlineData("missing-upload-path")]
    [InlineData("duplicate-condition")]
    [InlineData("empty-condition")]
    [InlineData("missing-execution")]
    [InlineData("duplicate-execution")]
    [InlineData("execution-after-scrub")]
    [InlineData("unexpected-private-action")]
    [InlineData("missing-gateway-mask")]
    [InlineData("late-mcp-cli-mount")]
    public async Task PublicationGuardRejectsUnexpectedCompilerLayout(string scenario)
    {
        var workflow = PublicationGuardFixture.ReplaceLineEndings("\n");
        workflow = scenario switch
        {
            "missing-agent" => workflow.Replace("  agent:", "  renamed_agent:", StringComparison.Ordinal),
            "missing-scrub" => workflow.Replace("id: auto_sec_scrub", "id: renamed_scrub", StringComparison.Ordinal),
            "duplicate-scrub" => workflow.Replace("      - name: Ingest agent output\n", "      - name: Ingest agent output\n        id: auto_sec_scrub\n", StringComparison.Ordinal),
            "publication-before-scrub" => workflow.Replace("name: Before scrub", "name: Append agent step summary", StringComparison.Ordinal)
                .Replace("name: Append agent step summary\n        if: always()", "name: Renamed summary\n        if: always()", StringComparison.Ordinal),
            "missing-upload" => workflow.Replace("name: Upload agent artifacts", "name: Renamed upload", StringComparison.Ordinal),
            "missing-upload-path" => workflow.Replace("          path: |", "          renamed-path: |", StringComparison.Ordinal),
            "duplicate-condition" => workflow.Replace("      - name: Future publication\n",
                "      - name: Future publication\n        if: always()\n        if: success()\n", StringComparison.Ordinal),
            "empty-condition" => workflow.Replace("      - name: Future publication\n",
                "      - name: Future publication\n        if:\n", StringComparison.Ordinal),
            "missing-execution" => workflow.Replace("id: agentic_execution", "id: renamed_execution", StringComparison.Ordinal),
            "duplicate-execution" => workflow.Replace("name: Before scrub\n", "name: Before scrub\n        id: agentic_execution\n", StringComparison.Ordinal),
            "execution-after-scrub" => workflow.Replace("id: agentic_execution", "id: renamed_execution", StringComparison.Ordinal)
                .Replace("name: Future publication\n", "name: Future publication\n        id: agentic_execution\n", StringComparison.Ordinal),
            "unexpected-private-action" => workflow.Replace("        run: bash stop-gateway.sh", "        uses: unexpected/action@example", StringComparison.Ordinal),
            "missing-gateway-mask" => workflow.Replace("echo \"::add-mask::${MCP_GATEWAY_AGENT_ID}\"", "echo renamed-mask", StringComparison.Ordinal),
            "late-mcp-cli-mount" => workflow.Replace("name: Stop MCP Gateway", "name: Mount MCP servers as CLIs", StringComparison.Ordinal)
                .Replace("        run: bash stop-gateway.sh", "        uses: actions/github-script@example", StringComparison.Ordinal),
            _ => throw new InvalidOperationException(),
        };
        var result = await RunHarnessAsync(new JsonObject { ["mode"] = "publication-guard", ["workflow"] = workflow });
        if (scenario is "duplicate-condition" or "empty-condition")
        {
            Assert.Equal("auto-sec publication guard: expected one guarded step condition", result["error"]!.GetValue<string>());
        }
        else
        {
            Assert.StartsWith("auto-sec publication guard:", result["error"]!.GetValue<string>());
        }
    }

    private const string PublicationGuardFixture = """
        name: Fixture
        jobs:
          earlier_job:
            steps:
              - run: echo unchanged
          agent:
            steps:
              - name: Before scrub
                run: echo unchanged
              - name: Start MCP Gateway
                run: |
                  MCP_GATEWAY_AGENT_ID=fixture-gateway-id
                  echo "::add-mask::${MCP_GATEWAY_AGENT_ID}"
                  echo start-gateway
              - name: Execute GitHub Copilot CLI
                id: agentic_execution
                run: |
                  echo private-agent
              - name: Detect agent errors
                id: detect-agent-errors
                if: always()
                uses: actions/github-script@example
                with:
                  script: console.log('private diagnostic');
              - name: Stop MCP Gateway
                if: always()
                run: bash stop-gateway.sh
              - name: Redact secrets in logs
                if: always()
                uses: actions/github-script@example
                with:
                  script: console.log('private diagnostic');
              - name: Scrub auto-sec agent transcript and outputs
                id: auto_sec_scrub
                if: always()
                run: echo scrub
              - name: Append agent step summary
                if: always()
                run: echo summary
              - name: Ingest agent output
                if: always()
                run: echo ingest
              - name: Future publication
                run: echo future
              - name: Conditional publication
                if: steps.collect_output.outcome == 'success'
                run: echo conditional
              - name: Upload agent output fallback artifact
                if: always()
                uses: actions/upload-artifact@example
                with:
                  path: |
                    /tmp/gh-aw/agent_output.json
                    /tmp/gh-aw/agent_execution.json
              - name: Upload agent artifacts
                if: always()
                uses: actions/upload-artifact@example
                with:
                  path: |
                    /tmp/gh-aw/agent_output.json
                    /tmp/gh-aw/aw-prompts/prompt.txt
          other_job:
            steps:
              - run: echo unchanged
        """;

    private static void AssertPublicationGuards(string workflow)
    {
        var publications = PublicationSteps(workflow);
        Assert.All(publications, step =>
        {
            var condition = step.Children[new YamlScalarNode("if")].ToString();
            Assert.EndsWith(" && steps.auto_sec_scrub.outcome == 'success' && steps.auto_sec_scrub.outputs.publication_ready == 'true'", condition);
        });
    }

    [Fact]
    public void CompiledUploadsPublishOnlyValidatedOutputsAndCanonicalPatches()
        => AssertSafeUploadPaths(File.ReadAllText(Path.Combine(RepoRoot.Path, ".github", "workflows", "auto-sec.lock.yml")));

    [Fact]
    public void CompiledTelemetryConsumersReportWithholdingWithoutReadingFiles()
    {
        var steps = PublicationSteps(File.ReadAllText(Path.Combine(RepoRoot.Path, ".github", "workflows", "auto-sec.lock.yml")));
        foreach (var name in s_privateTelemetryStepNames)
        {
            var step = Assert.Single(steps, step => step.Children[new YamlScalarNode("name")].ToString() == name);
            Assert.Equal("echo \"Auto-sec withholds agent-writable telemetry.\"", step.Children[new YamlScalarNode("run")].ToString());
            Assert.False(step.Children.ContainsKey(new YamlScalarNode("uses")));
            Assert.False(step.Children.ContainsKey(new YamlScalarNode("env")));
            Assert.False(step.Children.ContainsKey(new YamlScalarNode("with")));
        }
    }

    [Fact]
    public void CompiledPrivateExecutionSuppressesStreamsAndWithholdsDiagnosticParsers()
    {
        var steps = AgentSteps(File.ReadAllText(Path.Combine(RepoRoot.Path, ".github", "workflows", "auto-sec.lock.yml")));
        var execution = Assert.Single(steps,
            step => step.Children.TryGetValue(new YamlScalarNode("id"), out var id) && id.ToString() == "agentic_execution");
        Assert.StartsWith("exec >/dev/null 2>&1\n", execution.Children[new YamlScalarNode("run")].ToString());
        var gateway = Assert.Single(steps, step => step.Children[new YamlScalarNode("name")].ToString() == "Start MCP Gateway");
        Assert.Contains("echo \"::add-mask::${MCP_GATEWAY_AGENT_ID}\"\nexec >/dev/null 2>&1\n",
            gateway.Children[new YamlScalarNode("run")].ToString());
        var collection = Assert.Single(steps, step => step.Children[new YamlScalarNode("name")].ToString() == "Collect alerts and Dependabot pull requests");
        Assert.Contains("exec >/dev/null 2>&1", collection.Children[new YamlScalarNode("run")].ToString());
        Assert.True(collection.Children[new YamlScalarNode("run")].ToString().IndexOf("exec >/dev/null 2>&1", StringComparison.Ordinal)
            < collection.Children[new YamlScalarNode("run")].ToString().IndexOf("gh api", StringComparison.Ordinal));
        foreach (var name in new[] { "Detect agent errors", "Redact secrets in logs" })
        {
            var diagnostic = Assert.Single(steps, step => step.Children[new YamlScalarNode("name")].ToString() == name);
            Assert.Equal("echo \"Auto-sec withholds agent-writable telemetry.\"", diagnostic.Children[new YamlScalarNode("run")].ToString());
            Assert.False(diagnostic.Children.ContainsKey(new YamlScalarNode("uses")));
        }
    }

    private static readonly string[] s_privateTelemetryStepNames =
    [
        "Append agent step summary",
        "Parse agent logs for step summary",
        "Parse MCP Gateway logs for step summary",
        "Print firewall logs",
        "Parse token usage for step summary",
        "Print AWF reflect summary",
        "Generate observability summary",
    ];

    private static void AssertSafeUploadPaths(string workflow)
    {
        var uploads = PublicationSteps(workflow).Where(step =>
            step.Children.TryGetValue(new YamlScalarNode("uses"), out var action)
            && action.ToString().StartsWith("actions/upload-artifact@", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, uploads.Length);
        foreach (var upload in uploads)
        {
            var config = Assert.IsType<YamlMappingNode>(upload.Children[new YamlScalarNode("with")]);
            var paths = config.Children[new YamlScalarNode("path")].ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
            string[] expected = upload.Children[new YamlScalarNode("name")].ToString() == "Upload agent artifacts"
                ? ["/tmp/gh-aw/agent_output.json", "/tmp/gh-aw/safeoutputs.jsonl", "/tmp/gh-aw/aw-auto-sec-security-updates.patch", "/tmp/gh-aw/aw-microsoft-aspire-auto-sec-security-updates.patch"]
                : ["/tmp/gh-aw/agent_output.json", "/tmp/gh-aw/safeoutputs.jsonl"];
            Assert.Equal(expected, paths);
        }
    }

    private static JsonArray CompiledPublicationConditions()
        => new(PublicationSteps(File.ReadAllText(Path.Combine(RepoRoot.Path, ".github", "workflows", "auto-sec.lock.yml")))
            .Select(step => (JsonNode?)JsonValue.Create(step.Children[new YamlScalarNode("if")].ToString())).ToArray());

    private static YamlMappingNode[] PublicationSteps(string workflow)
    {
        var steps = AgentSteps(workflow);
        var scrub = Assert.Single(steps, step => step.Children.TryGetValue(new YamlScalarNode("id"), out var id) && id.ToString() == "auto_sec_scrub");
        Assert.Equal("always()", scrub.Children[new YamlScalarNode("if")].ToString());
        var publications = steps[(Array.IndexOf(steps, scrub) + 1)..];
        Assert.NotEmpty(publications);
        return publications;
    }

    private static YamlMappingNode[] AgentSteps(string workflow)
    {
        var yaml = new YamlStream();
        yaml.Load(new StringReader(workflow));
        var root = Assert.IsType<YamlMappingNode>(Assert.Single(yaml.Documents).RootNode);
        var jobs = Assert.IsType<YamlMappingNode>(root.Children[new YamlScalarNode("jobs")]);
        var agent = Assert.IsType<YamlMappingNode>(jobs.Children[new YamlScalarNode("agent")]);
        return Assert.IsType<YamlSequenceNode>(agent.Children[new YamlScalarNode("steps")]).Children.Cast<YamlMappingNode>().ToArray();
    }

    [Theory]
    [RequiresTools(["node"])]
    [InlineData("extension/package-lock.json", "{\n  \"funding\": \"https://github.com/sponsors/y\"\n}", "{\n  \"resolved\": \"https://github.com/o/x/archive/v1.tgz\"\n}", "https://github.com/")]
    [InlineData("extension/package-lock.json", "{\n  \"resolved\": \"https://github.com/o/y/archive/v1.tgz\"\n}", "{\n  \"resolved\": \"https://github.com/o/x/archive/v1.tgz\"\n}", "")]
    [InlineData("extension/package-lock.json", "{}", "{\n  \"repository\": {\n    \"url\": \"git+https://gitlab.example/o/r.git\"\n  }\n}", "")]
    [InlineData("extension/package-lock.json", "{\n  \"lockfileVersion\": 1,\n  \"dependencies\": {}\n}", "{\n  \"lockfileVersion\": 1,\n  \"dependencies\": {\n    \"bugs\": {\n      \"version\": \"1.0.0\",\n      \"resolved\": \"https://evil.example/bugs-1.0.0.tgz\"\n    }\n  }\n}", "https://evil.example/")]
    [InlineData("extension/package-lock.json", "{\n  \"lockfileVersion\": 1,\n  \"dependencies\": {}\n}", "{\n  \"lockfileVersion\": 1,\n  \"dependencies\": {\n    \"funding\": { \"version\": \"1.0.0\", \"resolved\": \"https://evil.example/funding-1.0.0.tgz\" }\n  }\n}", "https://evil.example/")]
    [InlineData("extension/package-lock.json", "{}", "{\n  \"repository\": {\n    \"url\": \"git+https://gitlab.example/o/r.git\"", "git+https://gitlab.example/")]
    [InlineData("extension/package.json", "{}", "{\n  \"dependencies\": {\n    \"bugs\": \"https://evil.example/bugs.tgz\"\n  }\n}", "https://evil.example/")]
    [InlineData("extension/package.json", "{\n  \"bugs\": {\n    \"url\": \"https://github.com/o/r/issues\"\n  },\n  \"repository\": \"https://github.com/o/r\",\n  \"dependencies\": {\n    \"x\": \"^1.0.0\"\n  }\n}", "{\n  \"bugs\": {\n    \"url\": \"https://github.com/o/r/issues\"\n  },\n  \"repository\": \"https://github.com/o/r\",\n  \"dependencies\": {\n    \"x\": \"^1.0.1\"\n  }\n}", "")]
    [InlineData("extension/package.json", "{\n  \"repository\": \"https://github.com/o/r\"\n}", "{\n  \"repository\": \"https://github.com/o/r\",\n  \"dependencies\": {\n    \"x\": \"https://github.com/o/x/archive/v1.tgz\"\n  }\n}", "https://github.com/")]
    [InlineData("pyproject.toml", "[project.urls]\nHomepage = \"https://github.com/o/r\"\n", "[project]\ndependencies = [\"x @ https://github.com/o/x/archive/v1.tar.gz\"]\n", "https://github.com/")]
    [InlineData("extension/package.json", "{\n  \"dependencies\": {\n    \"x\": \"file:../x\"\n  }\n}", "{\n  \"dependencies\": {\n    \"x\": \"file:../x\",\n    \"y\": \"file:../y\"\n  }\n}", "file:../y")]
    public async Task MetadataUrlsNeverAuthorizePackageSources(string path, string baseText, string headText, string expected)
    {
        var result = await RunHarnessAsync(new JsonObject
        {
            ["mode"] = "call",
            ["fn"] = "findNewSources",
            ["args"] = new JsonArray(baseText, headText, path),
        });

        Assert.Equal(expected, string.Join(",", result["value"]!.AsArray().Select(source => source!.GetValue<string>())));
    }

    [Theory]
    [RequiresTools(["node"])]
    [InlineData("source = { registry = \"https://pypi.org/simple\" }", "source = { editable = \"../pkg\" }", "uv-local:editable:../pkg")]
    [InlineData("source = { registry = \"https://pypi.org/simple\" }", "source = { directory = '../pkg' }", "uv-local:directory:../pkg")]
    [InlineData("source = { registry = \"https://pypi.org/simple\" }", "source = { virtual = \"../pkg\" }", "uv-local:virtual:../pkg")]
    [InlineData("", "[package.source]\neditable = \"../pkg\"\n", "uv-local:editable:../pkg")]
    [InlineData("", "\"source\" = { \"editable\" = \"../pkg\" }", "uv-local:editable:../pkg")]
    [InlineData("", "[package.'source']\n'directory' = '../pkg'\n", "uv-local:directory:../pkg")]
    [InlineData("", "source = {\n  editable = \"..\\u002fpkg\"\n}", "uv-local:editable:../pkg")]
    [InlineData("source = { editable = \"../pkg\" }", "source = { editable = \".././pkg\" }", "")]
    [InlineData("source = { editable = \"../pkg\" }", "source = { virtual = \"../pkg\" }", "uv-local:virtual:../pkg")]
    public async Task UvLocalSourceDescriptorsParticipateInSourceAuthorization(string baseText, string headText, string expected)
    {
        var result = await RunHarnessAsync(new JsonObject
        {
            ["mode"] = "call",
            ["fn"] = "findNewSources",
            ["args"] = new JsonArray(baseText, headText, "uv.lock"),
        });

        Assert.Equal(expected, string.Join(",", result["value"]!.AsArray().Select(source => source!.GetValue<string>())));
    }

    [Theory]
    [RequiresTools(["node"])]
    [InlineData("editable")]
    [InlineData("directory")]
    [InlineData("virtual")]
    public async Task PatchContentGateRejectsRegistryToLocalUvSources(string kind)
    {
        const string source = "source = { registry = \"https://pypi.org/simple\" }";
        const string baseText = "[[package]]\nname = \"foo\"\nversion = \"1.0.0\"\n" + source + "\ndependencies = []\n";
        var result = await RunHarnessAsync(new JsonObject
        {
            ["mode"] = "patch-gate",
            ["patchFiles"] = new JsonObject { ["aw-test.patch"] = ReplacementPatch("uv.lock", baseText, source, $"source = {{ {kind} = \"../pkg\" }}") },
            ["workspaceFiles"] = new JsonObject { ["uv.lock"] = baseText },
        });

        Assert.Equal(["uv.lock new-package-source"], result["value"]!["violations"]!.AsArray().Select(v => $"{v!["path"]} {v["reason"]}"));
        Assert.Single(result["failures"]!.AsArray());
    }

    [Theory]
    [RequiresTools(["node"])]
    [InlineData("1.0.0-alpha", "1.0.0-alpha.1", -1)]
    [InlineData("1.0.0-alpha.1", "1.0.0-alpha.beta", -1)]
    [InlineData("1.0.0-alpha.beta", "1.0.0-beta", -1)]
    [InlineData("1.0.0-beta.2", "1.0.0-beta.11", -1)]
    [InlineData("1.0.0-beta.11", "1.0.0-rc.1", -1)]
    [InlineData("1.0.0-rc.1", "1.0.0", -1)]
    [InlineData("1.0.0-rc.10", "1.0.0-rc.9", 1)]
    [InlineData("1.0.0-rc.1", "1.0.0-rc.1", 0)]
    [InlineData("1.0.0-9007199254740992", "1.0.0-9007199254740993", -1)]
    [InlineData("9007199254740993.0.0", "9007199254740992.0.0", 1)]
    public async Task ComparesPrereleaseVersionsBySemVerPrecedence(string left, string right, int expected)
    {
        var result = await RunHarnessAsync(new JsonObject
        {
            ["mode"] = "call",
            ["fn"] = "compareVersions",
            ["args"] = new JsonArray(left, right),
        });

        Assert.Equal(expected, result["value"]!.GetValue<int>());
    }

    [Theory]
    [RequiresTools(["node"])]
    [InlineData("https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-public-npm/npm/registry/a/-/a-1.0.0.tgz", "")]
    [InlineData("https://dnceng.pkgs.visualstudio.com/public/_packaging/dotnet9-transport/nuget/v3/index.json", "")]
    [InlineData("https://registry.npmjs.org/a/-/a-1.0.0.tgz", "")]
    [InlineData("https://pkgs.dev.azure.com/contoso/_packaging/feed/npm/registry/a/-/a-1.0.0.tgz", "https://pkgs.dev.azure.com/contoso/_packaging/feed/")]
    [InlineData("https://pkgs.dev.azure.com/dnceng/internal/_packaging/feed/npm/registry/a", "https://pkgs.dev.azure.com/dnceng/internal/_packaging/feed/")]
    [InlineData("https://registry.example.com/a/-/a-1.0.0.tgz", "https://registry.example.com/")]
    [InlineData("https://registry.npmjs.org:443/a/-/a-1.0.0.tgz", "")]
    [InlineData("https://registry.npmjs.org:8443/a/-/a-1.0.0.tgz", "https://registry.npmjs.org:8443/")]
    [InlineData("git://github.com/a/b.git", "git://github.com/")]
    [InlineData("git://github.com:9418/a/b.git", "git://github.com/")]
    [InlineData("HTTPS://Registry.Example.com/a.tgz", "https://registry.example.com/")]
    [InlineData("HTTPS://REGISTRY.NPMJS.ORG/a/-/a-1.0.0.tgz", "")]
    [InlineData("https://[2001:db8::1]/a/-/a-1.0.0.tgz", "https://[2001:db8::1]/")]
    [InlineData("https://user@[2001:DB8::1]:8443/a.tgz", "https://[2001:db8::1]:8443/")]
    [InlineData("https://user@registry.npmjs.org@evil.example/pkg.tgz", "https://evil.example/")]
    [InlineData("https://evil.example\\@registry.npmjs.org/pkg.tgz", "https://evil.example/")]
    [InlineData("git+ssh://git@github.com:2222/a/b.git", "git+ssh://github.com:2222/")]
    [InlineData("https:\\/\\/evil.example\\/x.tgz", "https://evil.example/")]
    [InlineData("https\\u003a\\u002f\\u002fevil.example/x.tgz", "https://evil.example/")]
    [InlineData("file:../x.tgz", "file:../x.tgz")]
    [InlineData("x@link:../x", "link:../x")]
    [InlineData("portal:../x", "portal:../x")]
    [InlineData("file:///tmp/x.tgz", "file:///tmp/x.tgz")]
    public async Task FindsPackageSourcesAddedOnHead(string headUrl, string expected)
    {
        var result = await RunHarnessAsync(new JsonObject
        {
            ["mode"] = "call",
            ["fn"] = "findNewSources",
            ["args"] = new JsonArray("resolved \"https://registry.npmjs.org/b/-/b-1.0.0.tgz\"", $"resolved \"{headUrl}\""),
        });

        Assert.Equal(expected, string.Join(",", result["value"]!.AsArray().Select(source => source!.GetValue<string>())));
    }

    [Theory]
    [RequiresTools(["node"])]
    [InlineData("extension/package-lock.json", "", """{"packages":{"node_modules/lodash":{"version":"4.17.21","resolved":"https://r.example/lodash/-/lodash-4.17.21.tgz"}}}""", 0)]
    [InlineData("extension/package-lock.json", "", """{"packages":{"node_modules/lodash":{"version":"4.17.21","resolved":"https://r.example/evil/-/evil-1.0.0.tgz"}}}""", 1)]
    [InlineData("extension/package-lock.json", "", """{"packages":{"node_modules/lodash":{"version":"4.17.21","resolved":"https://r.example/lodash/-/lodash-4.17.20.tgz"}}}""", 1)]
    [InlineData("extension/package-lock.json", "", """{"packages":{"node_modules/@babel/parser":{"version":"7.29.3","resolved":"https://r.example/@babel%2fparser/-/parser-7.29.3.tgz"}}}""", 0)]
    [InlineData("extension/package-lock.json", "", """{"packages":{"node_modules/my-lodash":{"name":"lodash","version":"4.17.21","resolved":"https://r.example/lodash/-/lodash-4.17.21.tgz"}}}""", 0)]
    [InlineData("extension/package-lock.json", "", """{"dependencies":{"my-lodash":{"version":"npm:lodash@4.17.21","resolved":"https://r.example/lodash/-/lodash-4.17.21.tgz"}}}""", 0)]
    [InlineData("extension/package-lock.json", "", """{"dependencies":{"lodash":{"version":"4.17.21","resolved":"https://r.example/evil/-/evil-1.0.0.tgz"}}}""", 1)]
    // An artifact the base already carries was reviewed when it landed.
    [InlineData("extension/package-lock.json", """{"packages":{"node_modules/x":{"version":"1.0.0","resolved":"https://r.example/x/-/y-1.0.0.tgz"}}}""", """{"packages":{"node_modules/x":{"version":"1.0.0","resolved":"https://r.example/x/-/y-1.0.0.tgz"}}}""", 0)]
    [InlineData("extension/yarn.lock", "", "my-lodash@npm:lodash@^4:\n  version \"4.17.21\"\n  resolved \"https://r.example/lodash/-/lodash-4.17.21.tgz#abc\"\n", 0)]
    [InlineData("extension/yarn.lock", "", "\"lodash@npm:^4\":\n  version: 4.17.21\n  resolution: \"lodash@npm:4.17.21\"\n", 0)]
    [InlineData("extension/yarn.lock", "", "\"lodash@npm:^4\":\n  version: 4.17.21\n  resolution: \"evil@npm:1.0.0\"\n", 1)]
    [InlineData("extension/pnpm-lock.yaml", "", "packages:\n  lodash@4.17.21:\n    resolution: {integrity: sha512-x, tarball: https://r.example/lodash/-/lodash-4.17.21.tgz}\n", 0)]
    [InlineData("extension/pnpm-lock.yaml", "", "packages:\n  lodash@4.17.21:\n    resolution: {integrity: sha512-x, tarball: https://r.example/evil/-/evil-1.0.0.tgz}\n", 1)]
    [InlineData("extension/pnpm-lock.yaml", "", "packages:\n  lodash@4.17.21:\n    resolution:\n      integrity: sha512-x\n      tarball: https://r.example/lodash/-/lodash-4.17.21.tgz\n", 0)]
    [InlineData("extension/pnpm-lock.yaml", "", "packages:\n  lodash@4.17.21:\n    resolution:\n      tarball: 'https://r.example/evil/-/evil-1.0.0.tgz'\n", 1)]
    [InlineData("extension/pnpm-lock.yaml", "", "packages:\n  '@scope/name@1.0.0':\n    resolution:\n      tarball: \"https://r.example/@scope/name/-/name-1.0.0.tgz\"\n", 0)]
    [InlineData("extension/pnpm-lock.yaml", "", "packages:\n  lodash@4.17.21:\n    resolution:\n      tarball: https://r.example/lodash/-/lodash-4.17.20.tgz\n", 1)]
    [InlineData("extension/pnpm-lock.yaml", "", "packages:\n  invalid-entry:\n    resolution:\n      tarball: https://r.example/lodash/-/lodash-4.17.21.tgz\n", 1)]
    [InlineData("extension/pnpm-lock.yaml", "", "packages:\n  \"lodash@4.17.21\":\n    'resolution':\n      \"tarball\": https://r.example/lodash/-/lodash-4.17.21.tgz\n", 0)]
    [InlineData("extension/pnpm-lock.yaml", "", "packages:\n  lodash@4.17.21:\n    \"resolution\": {\"tarball\": https://r.example/evil/-/evil-1.0.0.tgz}\n", 1)]
    [InlineData("extension/pnpm-lock.yaml", "", "packages:\n  lodash@4.17.21:\n    \"tarball\": https://r.example/lodash/-/lodash-4.17.21.tgz\n", 1)]
    [InlineData("uv.lock", "", "[[package]]\nname = \"Jinja2\"\nversion = \"3.1.6\"\nsource = { registry = \"https://pypi.org/simple\" }\nsdist = { url = \"https://f.example/jinja2-3.1.6.tar.gz\", hash = \"sha256:x\" }\nwheels = [\n    { url = \"https://f.example/jinja2-3.1.6-py3-none-any.whl\", hash = \"sha256:y\" },\n]\n", 0)]
    [InlineData("uv.lock", "", "[[package]]\nname = \"python-dateutil\"\nversion = \"2.8.2\"\nsdist = { url = \"https://f.example/python-dateutil-2.8.2.tar.gz\" }\n", 0)]
    [InlineData("uv.lock", "", "[[package]]\nname = \"jinja2\"\nversion = \"3.1.6\"\nwheels = [\n    { url = \"https://f.example/evil-1.0-py3-none-any.whl\" },\n]\n", 1)]
    [InlineData("uv.lock", "", "[[package]]\nname = \"jinja2\"\nversion = \"3.1.6\"\nsdist = { url = \"https://f.example/jinja2-3.1.5.tar.gz\" }\n", 1)]
    // The entry moves to 4.17.21 but keeps the 4.17.20 tarball the base already listed.
    [InlineData("package-lock.json", "{\"packages\":{\"node_modules/lodash\":{\"version\":\"4.17.20\",\"resolved\":\"https://r.example/lodash/-/lodash-4.17.20.tgz\"}}}", "{\"packages\":{\"node_modules/lodash\":{\"version\":\"4.17.21\",\"resolved\":\"https://r.example/lodash/-/lodash-4.17.20.tgz\"}}}", 1)]
    [InlineData("yarn.lock", "lodash@^4.17.20:\n  version \"4.17.20\"\n  resolved \"https://r.example/lodash/-/lodash-4.17.20.tgz#abc\"\n", "lodash@^4.17.20:\n  version \"4.17.21\"\n  resolved \"https://r.example/lodash/-/lodash-4.17.20.tgz#abc\"\n", 1)]
    public async Task FindsLockfileArtifactsNotBoundToTheirEntry(string path, string baseText, string headText, int expectedCount)
    {
        var result = await RunHarnessAsync(new JsonObject
        {
            ["mode"] = "call",
            ["fn"] = "unboundLockfileArtifacts",
            ["args"] = new JsonArray(path, baseText, headText),
        });

        Assert.Equal(expectedCount, result["value"]!.AsArray().Count);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task ApprovesPrWhoseUnlistedVersionChangesPassEveryGate()
    {
        var scenario = CreateApprovalScenario();
        scenario["contents"]!["extension/yarn.lock@base"] = YarnLockEntry("lodash", "4.17.20") + YarnLockEntry("minimist", "1.2.5");
        scenario["contents"]!["extension/yarn.lock@head"] = YarnLockEntry("lodash", "4.17.21") + YarnLockEntry("minimist", "1.2.8") + YarnLockEntry("left-pad", "1.3.0");
        scenario["responses"]!["https://registry.npmjs.org/minimist"] = Response(new JsonObject { ["time"] = new JsonObject { ["1.2.8"] = "2026-09-01T00:00:00Z" } });
        scenario["responses"]!["https://registry.npmjs.org/left-pad"] = Response(new JsonObject { ["time"] = new JsonObject { ["1.3.0"] = "2026-09-01T00:00:00Z" } });

        var result = await RunHarnessAsync(scenario);

        var decision = Assert.Single(result["value"]!.AsArray());
        Assert.Equal("approve", decision!["decision"]!.GetValue<string>());
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task ApprovesPrThatRemovesAlertedTransitivePackage()
    {
        var scenario = CreateApprovalScenario();
        scenario["contents"]!["extension/yarn.lock@base"] = YarnLockEntry("lodash", "4.17.20") + YarnLockEntry("minimist", "1.2.5");
        scenario["contents"]!["extension/yarn.lock@head"] = YarnLockEntry("lodash", "4.17.21");
        scenario["alerts"]!.AsArray().Add(new JsonObject
        {
            ["number"] = 8,
            ["ecosystem"] = "npm",
            ["package"] = "minimist",
            ["manifest_path"] = "extension/yarn.lock",
            ["vulnerable_version_range"] = "< 1.2.6",
            ["first_patched_version"] = "1.2.6",
        });

        var result = await RunHarnessAsync(scenario);

        var decision = Assert.Single(result["value"]!.AsArray());
        Assert.Equal("approve", decision!["decision"]!.GetValue<string>());
        Assert.Equal([7, 8], decision["fixedAlerts"]!.AsArray().Select(n => n!.GetValue<int>()));
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task ApprovesPrWhoseLockfileUpgradesAlertedPackageTransitively()
    {
        var scenario = CreateApprovalScenario();
        scenario["contents"]!["extension/yarn.lock@base"] = YarnLockEntry("lodash", "4.17.20") + YarnLockEntry("minimist", "1.2.5");
        scenario["contents"]!["extension/yarn.lock@head"] = YarnLockEntry("lodash", "4.17.21") + YarnLockEntry("minimist", "1.2.8");
        scenario["responses"]!["https://registry.npmjs.org/minimist"] = Response(new JsonObject { ["time"] = new JsonObject { ["1.2.8"] = "2026-09-01T00:00:00Z" } });
        scenario["alerts"]!.AsArray().Add(new JsonObject
        {
            ["number"] = 8,
            ["ecosystem"] = "npm",
            ["package"] = "minimist",
            ["manifest_path"] = "extension/yarn.lock",
            ["vulnerable_version_range"] = "< 1.2.6",
            ["first_patched_version"] = "1.2.6",
        });

        var result = await RunHarnessAsync(scenario);

        var decision = Assert.Single(result["value"]!.AsArray());
        Assert.Equal("approve", decision!["decision"]!.GetValue<string>());
        Assert.Equal([7, 8], decision["fixedAlerts"]!.AsArray().Select(n => n!.GetValue<int>()));
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task ReportsMalwareSkipUnderGenericReason()
    {
        var scenario = CreateApprovalScenario();
        scenario["malwareNumbers"] = new JsonArray(7);

        var result = await RunHarnessAsync(scenario);

        var decision = Assert.Single(result["value"]!.AsArray());
        Assert.Equal("skip", decision!["decision"]!.GetValue<string>());
        Assert.Equal("malware-requires-review", decision["reasons"]![0]!.GetValue<string>());
        var info = Assert.Single(result["info"]!.AsArray())!.GetValue<string>();
        Assert.Equal("#101: skip human-review-required,fixes-no-open-alert", info);
        Assert.EndsWith("- #101: skip (human-review-required, fixes-no-open-alert)", result["summary"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task ApprovesGroupedPrWhenAlertDirectoryCarriesFixedVersion()
    {
        var scenario = CreateApprovalScenario();
        var pr = scenario["pr"]!.AsObject();
        pr["head_ref"] = "dependabot/npm_and_yarn/npm_and_yarn-1a2b3c4d5e";
        pr["title"] = "Bump the npm_and_yarn group across 2 directories with 1 update";
        pr["body"] = "Updates `lodash` from 4.17.20 to 4.17.21";
        scenario["files"]!.AsArray().Add("playground/app/yarn.lock");
        scenario["contents"]!["playground/app/yarn.lock@base"] = YarnLockEntry("lodash", "4.17.20");
        scenario["contents"]!["playground/app/yarn.lock@head"] = YarnLockEntry("lodash", "4.17.21");
        scenario["alerts"]!.AsArray().Add(new JsonObject
        {
            ["number"] = 8,
            ["ecosystem"] = "npm",
            ["package"] = "lodash",
            ["manifest_path"] = "playground/app/yarn.lock",
            ["first_patched_version"] = "4.17.21",
        });

        var result = await RunHarnessAsync(scenario);

        var decision = Assert.Single(result["value"]!.AsArray());
        Assert.Equal("approve", decision!["decision"]!.GetValue<string>());
        Assert.Equal([7, 8], decision["fixedAlerts"]!.AsArray().Select(n => n!.GetValue<int>()));
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task CapsApprovalsAfterSkippingIneligiblePullRequests()
    {
        var scenario = CreateApprovalScenario();
        var items = new JsonArray();
        var overrides = new JsonObject();
        for (var number = 1; number <= 14; number++)
        {
            items.Add(new JsonObject { ["type"] = "approve_dependabot_pr", ["pr_number"] = number, ["head_sha"] = HeadSha });
            if (number <= 3)
            {
                overrides[number.ToString(System.Globalization.CultureInfo.InvariantCulture)] = new JsonObject { ["user_login"] = "someone" };
            }
        }
        scenario["agentItems"] = items;
        scenario["prOverrides"] = overrides;

        var result = await RunHarnessAsync(scenario);

        Assert.Equal(
            ["not-dependabot", "not-dependabot", "not-dependabot", "approve", "approve", "approve", "approve", "approve", "approve", "approve", "approve", "approve", "approve", "approval-limit-reached"],
            result["value"]!.AsArray().Select(decision => decision!["decision"]!.GetValue<string>() == "approve"
                ? "approve"
                : decision["reasons"]![0]!.GetValue<string>()));
        Assert.Equal(Enumerable.Range(4, 10), result["reviews"]!.AsArray().Select(review => review!["pull_number"]!.GetValue<int>()));
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task UnconfirmedSubmissionsStillConsumeApprovalQuota()
    {
        var scenario = CreateApprovalScenario();
        scenario["approvalFailure"] = "submission-after-accept";
        var items = new JsonArray();
        for (var number = 1; number <= 12; number++)
        {
            items.Add(new JsonObject { ["type"] = "approve_dependabot_pr", ["pr_number"] = number, ["head_sha"] = HeadSha });
        }
        scenario["agentItems"] = items;

        var result = await RunHarnessAsync(scenario);

        Assert.Equal(Enumerable.Range(1, 10), result["reviews"]!.AsArray().Select(review => review!["pull_number"]!.GetValue<int>()));
        Assert.Equal(Enumerable.Repeat("Auto-sec approval failed: approval-submission-failed.", 10),
            result["failures"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.All(result["value"]!.AsArray(), decision => Assert.Equal("skip", decision!["decision"]!.GetValue<string>()));
        Assert.Equal(Enumerable.Repeat("approval-submission-failed", 10).Concat(Enumerable.Repeat("approval-limit-reached", 2)),
            result["value"]!.AsArray().Select(decision => Assert.Single(decision!["reasons"]!.AsArray())!.GetValue<string>()));
        Assert.StartsWith("auto-sec Dependabot approvals\nRequests: 12. Approved: 0. Skipped: 12.", result["summary"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Theory]
    [RequiresTools(["node"])]
    [InlineData(
        "app/yarn.lock",
        "foo@^1.0.0, foo@>=1.5.0:\n  version \"1.9.0\"\nfoo@^2.0.0:\n  version \"2.1.0\"\n",
        "foo@^1.0.0:\n  version \"1.9.0\"\nfoo@>=1.5.0, foo@^2.0.0:\n  version \"2.1.0\"\n",
        """[{"name":"foo","from":["1.9.0"],"to":"2.1.0"}]""")]
    [InlineData(
        "app/package-lock.json",
        """{"packages":{"node_modules/foo":{"version":"1.9.0"},"node_modules/a/node_modules/foo":{"version":"1.9.0"},"node_modules/b/node_modules/foo":{"version":"2.1.0"}}}""",
        """{"packages":{"node_modules/foo":{"version":"1.9.0"},"node_modules/a/node_modules/foo":{"version":"2.1.0"},"node_modules/b/node_modules/foo":{"version":"2.1.0"}}}""",
        """[{"name":"foo","from":["1.9.0"],"to":"2.1.0"}]""")]
    [InlineData(
        "app/pnpm-lock.yaml",
        "importers:\n  .:\n    dependencies:\n      foo:\n        specifier: ^1.0.0\n        version: 1.9.0\n\npackages:\n  foo@1.9.0:\n    resolution: {}\n  foo@2.1.0:\n    resolution: {}\n\nsnapshots:\n  a@1.0.0:\n    dependencies:\n      foo: 1.9.0\n  b@1.0.0:\n    dependencies:\n      foo: 2.1.0\n",
        "importers:\n  .:\n    dependencies:\n      foo:\n        specifier: ^1.0.0\n        version: 1.9.0\n\npackages:\n  foo@1.9.0:\n    resolution: {}\n  foo@2.1.0:\n    resolution: {}\n\nsnapshots:\n  a@1.0.0:\n    dependencies:\n      foo: 2.1.0\n  b@1.0.0:\n    dependencies:\n      foo: 2.1.0\n",
        """[{"name":"foo","from":["1.9.0"],"to":"2.1.0"}]""")]
    [InlineData(
        "app/yarn.lock",
        "foo@^1.0.0:\n  version \"1.9.0\"\nfoo@^2.0.0:\n  version \"2.1.0\"\n",
        "foo@^1.0.0:\n  version \"1.9.0\"\nfoo@^2.0.0:\n  version \"2.1.0\"\n",
        "[]")]
    public async Task DetectsConsumerMovingBetweenRetainedVersions(string path, string baseText, string headText, string expected)
    {
        var result = await RunHarnessAsync(new JsonObject
        {
            ["mode"] = "call",
            ["fn"] = "manifestVersionChanges",
            ["args"] = new JsonArray(path, baseText, headText, "npm"),
        });

        Assert.Equal(expected, result["value"]!.ToJsonString());
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task ParsesGroupedDependabotBody()
    {
        var result = await RunHarnessAsync(new JsonObject
        {
            ["mode"] = "call",
            ["fn"] = "parseDependabotUpdates",
            ["args"] = new JsonArray(
                "Bump the npm_and_yarn group across 2 directories with 2 updates",
                "Updates `lodash` from 4.17.20 to 4.17.21\nUpdates `@types/node` from 20.1.0 to 20.1.4\nUpdates `lodash` from 4.17.20 to 4.17.21\nUpdates `lodash` from 4.17.19 to 4.17.21"),
        });

        Assert.Equal(
            """[{"name":"lodash","from":"4.17.20","to":"4.17.21"},{"name":"@types/node","from":"20.1.0","to":"20.1.4"},{"name":"lodash","from":"4.17.19","to":"4.17.21"}]""",
            result["value"]!.ToJsonString());
    }

    [Theory]
    [RequiresTools(["node"])]
    [InlineData("app/yarn.lock", "lodash@^4.17.20:\n  version \"4.17.20\"\nminimist@^4.17.21:\n  version \"4.17.21\"\n", "lodash", "4.17.21", false)]
    [InlineData("app/yarn.lock", "\"lodash@^4.17.20\", lodash@^4.17.21:\n  version \"4.17.21\"\n", "lodash", "4.17.21", true)]
    [InlineData("app/yarn.lock", "\"lodash@npm:^4.17.20\":\n  version: 4.17.21\n", "lodash", "4.17.21", true)]
    [InlineData("app/yarn.lock", "lodash-es@^4.17.21:\n  version \"4.17.21\"\n", "lodash", "4.17.21", false)]
    [InlineData("app/yarn.lock", "parent@^1.0.0:\n  version \"1.0.0\"\n  dependencies:\n    lodash \"4.17.21\"\n", "lodash", "4.17.21", false)]
    [InlineData("app/package-lock.json", """{"packages":{"node_modules/lodash":{"version":"4.17.20"},"node_modules/minimist":{"version":"4.17.21"}}}""", "lodash", "4.17.21", false)]
    [InlineData("app/package-lock.json", """{"packages":{"node_modules/a/node_modules/@scope/lodash":{"version":"4.17.21"}}}""", "@scope/lodash", "4.17.21", true)]
    [InlineData("app/npm-shrinkwrap.json", """{"dependencies":{"a":{"version":"1.0.0","dependencies":{"lodash":{"version":"4.17.21"}}}}}""", "lodash", "4.17.21", true)]
    [InlineData("app/package.json", """{"dependencies":{"lodash":"^4.17.20","minimist":"^4.17.21"}}""", "lodash", "4.17.21", false)]
    [InlineData("app/package.json", """{"devDependencies":{"lodash":"~4.17.21"}}""", "lodash", "4.17.21", true)]
    [InlineData("app/package.json", """{"resolutions":{"**/lodash":"4.17.21"}}""", "lodash", "4.17.21", true)]
    [InlineData("app/pnpm-lock.yaml", "packages:\n\n  lodash@4.17.20:\n    resolution: {}\n\n  minimist@4.17.21:\n    resolution: {}\n", "lodash", "4.17.21", false)]
    [InlineData("app/pnpm-lock.yaml", "packages:\n\n  '@babel/parser@7.29.3':\n    resolution: {}\n", "@babel/parser", "7.29.3", true)]
    [InlineData("app/pnpm-lock.yaml", "packages:\n  /lodash/4.17.21:\n    resolution: {}\n", "lodash", "4.17.21", true)]
    [InlineData("app/pnpm-lock.yaml", "importers:\n  .:\n    dependencies:\n      lodash:\n        specifier: ^4.17.0\n        version: 4.17.21\n\npackages:\n  minimist@1.2.8:\n    resolution: {}\n", "lodash", "4.17.21", true)]
    [InlineData("app/uv.lock", "[[package]]\nname = \"jinja2\"\nversion = \"3.1.5\"\n\n[[package]]\nname = \"markupsafe\"\nversion = \"3.1.6\"\n", "jinja2", "3.1.6", false)]
    [InlineData("app/uv.lock", "[[package]]\nname = \"Jinja2\"\nversion = \"3.1.6\"\n", "jinja2", "3.1.6", true)]
    [InlineData("app/pyproject.toml", "dependencies = [\"jinja2>=3.1.5\", \"markupsafe==3.1.6\"]\n", "jinja2", "3.1.6", false)]
    [InlineData("app/pyproject.toml", "dependencies = [\"typing_extensions[x] >= 4.12.2, < 5; python_version < '3.11'\"]\n", "typing-extensions", "4.12.2", true)]
    [InlineData("Directory.Packages.props", "<PackageVersion Include=\"A\" Version=\"9.0.4\" />\n<PackageVersion Include=\"B\" Version=\"9.0.5\" />", "A", "9.0.5", false)]
    [InlineData("Directory.Packages.props", "<PackageVersion Version=\"9.0.5\" Include=\"System.Text.Json\" />", "system.text.json", "9.0.5", true)]
    [InlineData("Directory.Packages.props", "<PackageVersion Include=\"Npgsql\" Version=\"8.0.11\" />\n<PackageVersion Update=\"Npgsql\" Version=\"9.0.4\" />", "npgsql", "9.0.4", true)]
    [InlineData("app/requirements.txt", "jinja2==3.1.6\n", "jinja2", "3.1.6", false)]
    public async Task BindsVersionToItsOwnManifestEntry(string path, string text, string name, string version, bool expected)
    {
        var ecosystem = Path.GetFileName(path) switch
        {
            "Directory.Packages.props" => "nuget",
            "uv.lock" or "pyproject.toml" or "requirements.txt" => "pip",
            _ => "npm",
        };
        var result = await RunHarnessAsync(new JsonObject
        {
            ["mode"] = "call",
            ["fn"] = "manifestMentionsVersion",
            ["args"] = new JsonArray(path, text, ecosystem, name, version),
        });

        Assert.Equal(expected, result["value"]!.GetValue<bool>());
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task ReportsAlertsCoveredByDependabotPr()
    {
        var alerts = new JsonArray(
            new JsonObject { ["number"] = 1, ["ecosystem"] = "npm", ["package"] = "lodash", ["manifest_path"] = "app/yarn.lock", ["first_patched_version"] = "4.17.21", ["malware"] = false },
            new JsonObject { ["number"] = 2, ["ecosystem"] = "npm", ["package"] = "lodash", ["manifest_path"] = "other/yarn.lock", ["first_patched_version"] = "4.17.21", ["malware"] = false },
            new JsonObject { ["number"] = 3, ["ecosystem"] = "npm", ["package"] = "lodash", ["manifest_path"] = "app/yarn.lock", ["first_patched_version"] = "4.17.22", ["malware"] = false },
            new JsonObject { ["number"] = 4, ["ecosystem"] = "npm", ["package"] = "lodash", ["manifest_path"] = "app/yarn.lock", ["first_patched_version"] = null, ["malware"] = true, ["vulnerable_ranges"] = new JsonArray(">= 4.17.20, < 4.17.21") },
            // The new version is still inside the malware range, or the alert has no range, so
            // the PR does not fix either alert.
            new JsonObject { ["number"] = 12, ["ecosystem"] = "npm", ["package"] = "lodash", ["manifest_path"] = "app/yarn.lock", ["first_patched_version"] = null, ["malware"] = true, ["vulnerable_ranges"] = new JsonArray(">= 0") },
            new JsonObject { ["number"] = 13, ["ecosystem"] = "npm", ["package"] = "lodash", ["manifest_path"] = "app/yarn.lock", ["first_patched_version"] = null, ["malware"] = true },
            new JsonObject { ["number"] = 5, ["ecosystem"] = "pip", ["package"] = "lodash", ["manifest_path"] = "app/uv.lock", ["first_patched_version"] = "4.17.21", ["malware"] = false },
            new JsonObject { ["number"] = 6, ["ecosystem"] = "npm", ["package"] = "lodash", ["manifest_path"] = "nested/yarn.lock", ["first_patched_version"] = "4.17.21", ["malware"] = false },
            new JsonObject { ["number"] = 8, ["ecosystem"] = "npm", ["package"] = "lodash", ["manifest_path"] = "nested/yarn.lock", ["first_patched_version"] = null, ["malware"] = true },
            // Only the lockfile diff moves minimist, so it is covered through versionChanges.
            new JsonObject { ["number"] = 9, ["ecosystem"] = "npm", ["package"] = "minimist", ["manifest_path"] = "app/yarn.lock", ["first_patched_version"] = "1.2.6", ["malware"] = false },
            // The PR drops left-pad from app/, which fixes the alert by removal.
            new JsonObject { ["number"] = 10, ["ecosystem"] = "npm", ["package"] = "left-pad", ["manifest_path"] = "app/yarn.lock", ["first_patched_version"] = "1.3.1", ["malware"] = false },
            // other/ still carries left-pad, so that alert stays open.
            new JsonObject { ["number"] = 11, ["ecosystem"] = "npm", ["package"] = "left-pad", ["manifest_path"] = "other/yarn.lock", ["first_patched_version"] = "1.3.1", ["malware"] = false });
        var updates = new JsonArray(new JsonObject { ["name"] = "lodash", ["from"] = "4.17.20", ["to"] = "4.17.21" });
        var versionChanges = new JsonArray(new JsonObject { ["name"] = "minimist", ["from"] = new JsonArray("1.2.5"), ["to"] = "1.2.8" });
        var headContents = new JsonObject
        {
            ["app/yarn.lock"] = YarnLockEntry("lodash", "4.17.21") + YarnLockEntry("minimist", "1.2.8"),
            ["other/yarn.lock"] = YarnLockEntry("lodash", "4.17.20") + YarnLockEntry("left-pad", "1.3.0"),
            // A nested copy keeps the vulnerable version, so neither alert in nested/ is covered.
            ["nested/yarn.lock"] = YarnLockEntry("lodash", "4.17.21") + YarnLockEntry("lodash", "4.17.20"),
        };
        var baseContents = new JsonObject
        {
            ["app/yarn.lock"] = YarnLockEntry("lodash", "4.17.20") + YarnLockEntry("minimist", "1.2.5") + YarnLockEntry("left-pad", "1.3.0"),
            ["other/yarn.lock"] = YarnLockEntry("lodash", "4.17.20") + YarnLockEntry("left-pad", "1.3.0"),
            ["nested/yarn.lock"] = YarnLockEntry("lodash", "4.17.20"),
        };

        var result = await RunHarnessAsync(new JsonObject
        {
            ["mode"] = "call",
            ["fn"] = "coveredAlerts",
            ["args"] = new JsonArray(alerts, "npm", updates, versionChanges, headContents, baseContents),
        });

        Assert.Equal([1, 4, 9, 10], result["value"]!.AsArray().Select(n => n!.GetValue<int>()));
    }

    [Theory]
    [RequiresTools(["node"])]
    [InlineData("""{"lockfileVersion":3,"packages":{"":{"name":"app"},"node_modules/lodash":{"version":"4.17.21"}}}""", true)]
    [InlineData("""{"lockfileVersion":3,"packages":{"node_modules/lodash":""", false)]
    [InlineData("", false)]
    [InlineData("{}", false)]
    public async Task CoversRemovalOnlyWhenHeadManifestParses(string headText, bool expected)
    {
        var alerts = new JsonArray(
            new JsonObject { ["number"] = 1, ["ecosystem"] = "npm", ["package"] = "left-pad", ["manifest_path"] = "app/package-lock.json", ["first_patched_version"] = "1.3.1", ["malware"] = false });
        var baseContents = new JsonObject
        {
            ["app/package-lock.json"] = """{"lockfileVersion":3,"packages":{"":{"name":"app"},"node_modules/lodash":{"version":"4.17.20"},"node_modules/left-pad":{"version":"1.3.0"}}}""",
        };

        var result = await RunHarnessAsync(new JsonObject
        {
            ["mode"] = "call",
            ["fn"] = "coveredAlerts",
            ["args"] = new JsonArray(alerts, "npm", new JsonArray(), new JsonArray(), new JsonObject { ["app/package-lock.json"] = headText }, baseContents),
        });

        Assert.Equal(expected ? new[] { 1 } : Array.Empty<int>(), result["value"]!.AsArray().Select(n => n!.GetValue<int>()));
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task LookupReportsNuGetVersionMirroredOnApprovedFeed()
    {
        var result = await RunHarnessAsync(new JsonObject
        {
            ["mode"] = "lookup",
            ["ecosystem"] = "nuget",
            ["name"] = "System.Text.Json",
            ["version"] = "9.0.5",
            ["now"] = Now,
            ["nugetConfigText"] = File.ReadAllText(Path.Combine(RepoRoot.Path, "NuGet.config")),
            ["responses"] = new JsonObject
            {
                ["https://api.nuget.org/v3/registration5-gz-semver2/system.text.json/9.0.5.json"] = Response(new JsonObject { ["published"] = "2026-08-01T00:00:00Z" }),
                ["https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-eng/nuget/v3/index.json"] = ServiceIndex("https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-eng/nuget/v3/flat2/"),
                ["https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-eng/nuget/v3/flat2/system.text.json/index.json"] = Response(new JsonObject { ["versions"] = new JsonArray("9.0.4", "9.0.5") }),
            },
        });

        Assert.Equal(
            """{"ecosystem":"nuget","name":"System.Text.Json","version":"9.0.5","published_at":"2026-08-01T00:00:00Z","cooldown_satisfied":true,"available_on_approved_feed":true}""",
            result["value"]!.ToJsonString());
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task LookupProbesOnlyNuGetSourcesMappedToThePackage()
    {
        const string config = """
            <configuration>
              <packageSources>
                <clear />
                <add key="public" value="https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-public/nuget/v3/index.json" />
                <add key="transport" value="https://dnceng.pkgs.visualstudio.com/public/_packaging/dotnet9-transport/nuget/v3/index.json" />
                <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
              </packageSources>
              <packageSourceMapping>
                <packageSource key="public"><package pattern="*" /></packageSource>
                <packageSource key="transport"><package pattern="Contoso.Build*" /></packageSource>
                <packageSource key="nuget.org"><package pattern="Contoso.Build.Tasks" /></packageSource>
              </packageSourceMapping>
            </configuration>
            """;
        const string transportFlat = "https://dnceng.pkgs.visualstudio.com/public/_packaging/dotnet9-transport/nuget/v3/flat2/";
        const string publicIndex = "https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-public/nuget/v3/index.json";
        var responses = new JsonObject
        {
            ["https://api.nuget.org/v3/registration5-gz-semver2/contoso.build.engine/1.0.0.json"] = Response(new JsonObject { ["published"] = "2026-08-01T00:00:00Z" }),
            ["https://dnceng.pkgs.visualstudio.com/public/_packaging/dotnet9-transport/nuget/v3/index.json"] = ServiceIndex(transportFlat),
            [$"{transportFlat}contoso.build.engine/index.json"] = Response(new JsonObject { ["versions"] = new JsonArray("1.0.0") }),
            [publicIndex] = ServiceIndex("https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-public/nuget/v3/flat2/"),
        };

        // The longest prefix pattern wins, so only the transport feed is consulted.
        var prefixMapped = await RunHarnessAsync(NuGetLookup("Contoso.Build.Engine", config, responses));
        // An exact ID beats every prefix and maps only to a non-dnceng source, so nothing is probed.
        var exactMapped = await RunHarnessAsync(NuGetLookup("Contoso.Build.Tasks", config, responses.DeepClone().AsObject()));

        Assert.True(prefixMapped["value"]!["available_on_approved_feed"]!.GetValue<bool>());
        Assert.Equal(
            [
                "https://api.nuget.org/v3/registration5-gz-semver2/contoso.build.engine/1.0.0.json",
                "https://dnceng.pkgs.visualstudio.com/public/_packaging/dotnet9-transport/nuget/v3/index.json",
                $"{transportFlat}contoso.build.engine/index.json",
            ],
            prefixMapped["urls"]!.AsArray().Select(url => url!.GetValue<string>()));
        Assert.False(exactMapped["value"]!["available_on_approved_feed"]!.GetValue<bool>());
        Assert.Equal(
            ["https://api.nuget.org/v3/registration5-gz-semver2/contoso.build.tasks/1.0.0.json"],
            exactMapped["urls"]!.AsArray().Select(url => url!.GetValue<string>()));

        static JsonObject NuGetLookup(string name, string config, JsonObject responses) => new()
        {
            ["mode"] = "lookup",
            ["ecosystem"] = "nuget",
            ["name"] = name,
            ["version"] = "1.0.0",
            ["now"] = Now,
            ["nugetConfigText"] = config,
            ["responses"] = responses,
        };
    }

    [Theory]
    [RequiresTools(["node"])]
    [InlineData(true, "2026-08-01T00:00:00Z", true)]
    [InlineData(false, "2026-08-01T00:00:00Z", false)]
    [InlineData(null, "1900-01-01T00:00:00+00:00", false)]
    public async Task LookupTreatsUnlistedNuGetVersionsAsWithoutPublishDate(bool? listed, string published, bool expectedCooldown)
    {
        var leaf = new JsonObject { ["published"] = published };
        if (listed is not null)
        {
            leaf["listed"] = listed.Value;
        }
        var result = await RunHarnessAsync(new JsonObject
        {
            ["mode"] = "lookup",
            ["ecosystem"] = "nuget",
            ["name"] = "Contoso.Lib",
            ["version"] = "1.2.3",
            ["now"] = Now,
            ["responses"] = new JsonObject { ["https://api.nuget.org/v3/registration5-gz-semver2/contoso.lib/1.2.3.json"] = Response(leaf) },
        });

        Assert.Equal(expectedCooldown, result["value"]!["cooldown_satisfied"]!.GetValue<bool>());
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task LookupReportsUnmirroredNuGetVersionAndRecentNpmVersion()
    {
        var nuget = await RunHarnessAsync(new JsonObject
        {
            ["mode"] = "lookup",
            ["ecosystem"] = "nuget",
            ["name"] = "Contoso.Lib",
            ["version"] = "1.2.3",
            ["now"] = Now,
            ["nugetConfigText"] = File.ReadAllText(Path.Combine(RepoRoot.Path, "NuGet.config")),
            ["responses"] = new JsonObject
            {
                ["https://api.nuget.org/v3/registration5-gz-semver2/contoso.lib/1.2.3.json"] = Response(new JsonObject { ["published"] = "2026-08-01T00:00:00Z" }),
            },
        });
        var npm = await RunHarnessAsync(new JsonObject
        {
            ["mode"] = "lookup",
            ["ecosystem"] = "npm",
            ["name"] = "@scope/pkg",
            ["version"] = "1.0.1",
            ["now"] = Now,
            ["responses"] = new JsonObject
            {
                ["https://registry.npmjs.org/@scope%2Fpkg"] = Response(new JsonObject { ["time"] = new JsonObject { ["1.0.1"] = "2026-09-29T00:00:00Z" } }),
                ["https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-public-npm/npm/registry/@scope%2Fpkg"] = Response(new JsonObject { ["versions"] = new JsonObject { ["1.0.1"] = new JsonObject() } }),
            },
        });

        Assert.False(nuget["value"]!["available_on_approved_feed"]!.GetValue<bool>());
        Assert.True(nuget["value"]!["cooldown_satisfied"]!.GetValue<bool>());
        Assert.True(npm["value"]!["available_on_approved_feed"]!.GetValue<bool>());
        Assert.False(npm["value"]!["cooldown_satisfied"]!.GetValue<bool>());
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task LookupStartsPyPiCooldownFromNewestUpload()
    {
        var pip = await RunHarnessAsync(new JsonObject
        {
            ["mode"] = "lookup",
            ["ecosystem"] = "pip",
            ["name"] = "requests",
            ["version"] = "2.32.3",
            ["now"] = Now,
            ["responses"] = new JsonObject
            {
                ["https://pypi.org/pypi/requests/2.32.3/json"] = Response(new JsonObject
                {
                    ["urls"] = new JsonArray(
                        new JsonObject { ["upload_time_iso_8601"] = "2026-09-29T00:00:00Z" },
                        new JsonObject { ["upload_time_iso_8601"] = "2026-08-01T00:00:00Z" }),
                }),
            },
        });

        Assert.Equal("2026-09-29T00:00:00Z", pip["value"]!["published_at"]!.GetValue<string>());
        Assert.False(pip["value"]!["cooldown_satisfied"]!.GetValue<bool>());
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task PushGateAllowsAutoSecBranchAndIgnoresOtherOutputs()
    {
        var result = await RunHarnessAsync(CreatePushGateScenario(new JsonObject()));

        Assert.Empty(result["failures"]!.AsArray());
        Assert.Equal(1, result["value"]!["requests"]!.GetValue<int>());

        var noPush = await RunHarnessAsync(new JsonObject
        {
            ["mode"] = "push-gate",
            ["agentItems"] = new JsonArray(new JsonObject { ["type"] = "create_pull_request" }),
        });

        Assert.Empty(noPush["failures"]!.AsArray());
        Assert.Equal(0, noPush["value"]!["requests"]!.GetValue<int>());
    }

    [Theory]
    [RequiresTools(["node"])]
    [InlineData("wrong-head-branch")]
    [InlineData("wrong-head-repository")]
    [InlineData("wrong-base-branch")]
    [InlineData("not-open")]
    [InlineData("missing-pull-request-number")]
    public async Task PushGateFailsForPullRequestsOutsideAutoSecBranch(string reason)
    {
        var pr = new JsonObject();
        var item = new JsonObject { ["type"] = "push_to_pull_request_branch", ["pull_request_number"] = 202 };
        switch (reason)
        {
            case "wrong-head-branch":
                pr["head_ref"] = "feature/x";
                break;
            case "wrong-head-repository":
                pr["head_repo"] = "contoso/aspire";
                break;
            case "wrong-base-branch":
                pr["base_ref"] = "release/13.3";
                break;
            case "not-open":
                pr["state"] = "closed";
                break;
            case "missing-pull-request-number":
                item["pull_request_number"] = "abc";
                break;
        }

        var result = await RunHarnessAsync(CreatePushGateScenario(pr, item));

        var failure = Assert.Single(result["failures"]!.AsArray());
        Assert.Contains(reason, failure!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public void WorkflowRunsEveryTwelveHoursAndKeepsFeedConfigurationProtected()
    {
        var source = ReadWorkflow("auto-sec.md");
        var compiled = ReadWorkflow("auto-sec.lock.yml");

        Assert.Contains("- cron: \"17 */12 * * *\"", source, StringComparison.Ordinal);
        Assert.Contains("- cron: \"17 */12 * * *\"", compiled, StringComparison.Ordinal);
        Assert.Contains("labels: [auto-sec]", source, StringComparison.Ordinal);
        Assert.Contains("required-labels: [auto-sec]", source, StringComparison.Ordinal);
        Assert.Contains("check-branch-protection: false", source, StringComparison.Ordinal);
        Assert.Contains("allowed-branches: [\"auto-sec/security-updates\"]", source, StringComparison.Ordinal);
        Assert.Contains("approve_dependabot_pr:", compiled, StringComparison.Ordinal);
        Assert.Contains("gate.runApprovalJob({ github, approver, context, core })", compiled, StringComparison.Ordinal);

        var protectedFiles = GetSection(source, "^    protected-files: &auto-sec-protected", "^  push-to-pull-request-branch:");
        Assert.Contains("policy: blocked", protectedFiles, StringComparison.Ordinal);
        Assert.DoesNotContain("NuGet.config", protectedFiles, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("global.json", protectedFiles, StringComparison.Ordinal);
        Assert.DoesNotContain(".npmrc", protectedFiles, StringComparison.Ordinal);

        // Approval requests carry only identifiers; free text would be persisted in the
        // agent output artifact.
        var approvalInputs = GetSection(source, "^      inputs:", "^      steps:");
        Assert.Equal(
            ["head_sha", "pr_number"],
            Regex.Matches(approvalInputs, "^        ([a-z_]+):\r?$", RegexOptions.Multiline).Select(match => match.Groups[1].Value).Order(StringComparer.Ordinal));
        Assert.Contains("covered_alerts: m.coveredAlerts(alerts, ecosystem, updates, versionChanges, headContents, baseContents)", source, StringComparison.Ordinal);
        Assert.Contains("gh pr list --repo \"${REPO}\" --author \"app/dependabot\" --state open --base main", source, StringComparison.Ordinal);

        // push-to-pull-request-branch has no branch filter, so a deterministic gate step must
        // run before the safe-output handler to restrict pushes to the auto-sec branch.
        var gateStep = compiled.IndexOf("name: Restrict pushes to the auto-sec branch", StringComparison.Ordinal);
        var handlerStep = compiled.IndexOf("name: Process Safe Outputs", StringComparison.Ordinal);
        Assert.True(gateStep >= 0 && handlerStep > gateStep, "Push gate must run before Process Safe Outputs.");
        var gateSection = compiled[gateStep..handlerStep];
        Assert.Contains("GH_AW_AGENT_OUTPUT: ${{ steps.setup-agent-output-env.outputs.GH_AW_AGENT_OUTPUT }}", gateSection, StringComparison.Ordinal);
        Assert.Contains("gate.runPushTargetGate({ github, context, core })", gateSection, StringComparison.Ordinal);

        // The file allowlist admits executable manifests, so the patch content gate must run
        // before the handlers apply any patch, and both outputs must use the `am` transport
        // whose patch files the gate inspects.
        var patchGateStep = compiled.IndexOf("name: Validate auto-sec patch contents", StringComparison.Ordinal);
        Assert.True(patchGateStep >= 0 && handlerStep > patchGateStep, "Patch content gate must run before Process Safe Outputs.");
        var patchGateSection = compiled[patchGateStep..handlerStep];
        Assert.Contains("contains(needs.agent.outputs.output_types, 'create_pull_request')", patchGateSection, StringComparison.Ordinal);
        Assert.Contains("contains(needs.agent.outputs.output_types, 'push_to_pull_request_branch')", patchGateSection, StringComparison.Ordinal);
        Assert.Contains("await gate.runPatchContentGate({ github, context, core })", patchGateSection, StringComparison.Ordinal);

        // Agent-authored titles, bodies, and commit messages are published, so the public
        // text gate must also run before the handlers and read the agent output.
        var textGateStep = compiled.IndexOf("name: Validate auto-sec public text", StringComparison.Ordinal);
        Assert.True(textGateStep >= 0 && handlerStep > textGateStep, "Public text gate must run before Process Safe Outputs.");
        var textGateSection = compiled[textGateStep..handlerStep];
        Assert.Contains("contains(needs.agent.outputs.output_types, 'create_pull_request')", textGateSection, StringComparison.Ordinal);
        Assert.Contains("contains(needs.agent.outputs.output_types, 'push_to_pull_request_branch')", textGateSection, StringComparison.Ordinal);
        Assert.Contains("GH_AW_AGENT_OUTPUT: ${{ steps.setup-agent-output-env.outputs.GH_AW_AGENT_OUTPUT }}", textGateSection, StringComparison.Ordinal);
        Assert.Contains("await gate.runPublicTextGate({ core, github, context })", textGateSection, StringComparison.Ordinal);
        // The agent artifact and run summaries are public, so the transcript must be deleted and
        // the outputs reduced to template text before anything reads or uploads them. The gate
        // module is copied outside the writable checkout before the agent runs.
        Assert.Contains("name: Copy gate module for the agent output scrub", compiled, StringComparison.Ordinal);
        var redactStep = compiled.IndexOf("name: Redact secrets in logs", StringComparison.Ordinal);
        var scrubStep = compiled.IndexOf("name: Scrub auto-sec agent transcript and outputs", StringComparison.Ordinal);
        Assert.True(redactStep >= 0 && scrubStep > redactStep, "Agent output scrub must run after Redact secrets in logs.");
        foreach (var later in new[] { "name: Append agent step summary", "name: Copy Safe Outputs", "name: Ingest agent output", "name: Parse agent logs for step summary", "name: Upload agent artifacts" })
        {
            var laterStep = compiled.IndexOf(later, StringComparison.Ordinal);
            Assert.True(laterStep > scrubStep, $"Agent output scrub must run before {later}.");
        }
        Assert.Contains("runAgentOutputScrub({ core, github, context })", compiled, StringComparison.Ordinal);
        Assert.Contains("GH_AW_MISSING_TOOL_CREATE_ISSUE: \"false\"", compiled, StringComparison.Ordinal);
        Assert.Contains("GH_AW_REPORT_INCOMPLETE_CREATE_ISSUE: \"false\"", compiled, StringComparison.Ordinal);
        Assert.Contains("GH_AW_FAILURE_REPORT_AS_ISSUE: \"false\"", compiled, StringComparison.Ordinal);
        Assert.Equal(2, Regex.Matches(source, "^    patch-format: am\r?$", RegexOptions.Multiline).Count);
        var compiledPatchFormats = Regex.Matches(compiled, "patch_format\\\\\":\\\\\"([a-z]+)\\\\\"").Select(match => match.Groups[1].Value).ToArray();
        Assert.True(compiledPatchFormats.Length >= 2, "Both code-writing outputs must configure patch_format.");
        Assert.All(compiledPatchFormats, format => Assert.Equal("am", format));
    }

    private static JsonObject CreatePushGateScenario(JsonObject prOverrides, JsonObject? item = null)
    {
        var pr = new JsonObject
        {
            ["number"] = 202,
            ["state"] = "open",
            ["user_login"] = "github-actions[bot]",
            ["head_sha"] = HeadSha,
            ["head_ref"] = "auto-sec/security-updates",
            ["head_repo"] = "microsoft/aspire",
        };
        foreach (var (key, value) in prOverrides)
        {
            pr[key] = value?.DeepClone();
        }

        return new JsonObject
        {
            ["mode"] = "push-gate",
            ["pr"] = pr,
            ["agentItems"] = new JsonArray(item ?? new JsonObject { ["type"] = "push_to_pull_request_branch", ["pull_request_number"] = 202 }),
        };
    }

    private static JsonObject Response(JsonNode body) => new() { ["status"] = 200, ["body"] = body };

    // Git's object ID for a blob (git's identity scheme, not a security hash), so a test
    // patch's `index` line names the exact base the gate must rebuild from.
    private static string GitBlobId(string text)
    {
        var body = Encoding.UTF8.GetBytes(text);
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA1.HashData([.. Encoding.ASCII.GetBytes($"blob {body.Length}\0"), .. body]));
    }

    private static string YarnLockEntry(string name, string version, string feedPrefix = "https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-public-npm/")
        => $"{name}@^{version}:\n  version \"{version}\"\n  resolved \"{feedPrefix}npm/registry/{name}/-/{name}-{version}.tgz\"\n";

    private static JsonObject ServiceIndex(string flatContainer) => Response(new JsonObject
    {
        ["resources"] = new JsonArray(new JsonObject { ["@id"] = flatContainer, ["@type"] = "PackageBaseAddress/3.0.0" }),
    });

    private static JsonObject CreateCollectionScenario() => new()
    {
        ["mode"] = "collect-prs",
        ["prs"] = new JsonArray(new JsonObject
        {
            ["number"] = 101,
            ["headRefName"] = "dependabot/npm_and_yarn/extension/lodash-4.17.21",
            ["headRefOid"] = "head",
            ["baseRefOid"] = "base",
            ["title"] = "Bump lodash from 4.17.20 to 4.17.21 in /extension",
            ["body"] = "",
            ["isDraft"] = false,
            ["statusCheckRollup"] = new JsonArray(new JsonObject { ["conclusion"] = "SUCCESS" }),
        }),
        ["changedFiles"] = new JsonArray(
            new JsonArray(new JsonObject { ["filename"] = "README.md", ["status"] = "modified" }),
            new JsonArray(new JsonObject { ["filename"] = "extension/yarn.lock", ["status"] = "modified" })),
        ["contents"] = new JsonObject
        {
            ["repos/microsoft/aspire/contents/extension/yarn.lock?ref=base"] = YarnLockEntry("lodash", "4.17.20"),
            ["repos/microsoft/aspire/contents/extension/yarn.lock?ref=head"] = YarnLockEntry("lodash", "4.17.21"),
        },
        ["alerts"] = new JsonArray(new JsonObject
        {
            ["number"] = 7,
            ["ecosystem"] = "npm",
            ["package"] = "lodash",
            ["manifest_path"] = "extension/yarn.lock",
            ["vulnerable_version_range"] = "< 4.17.21",
            ["first_patched_version"] = "4.17.21",
        }),
    };

    private static JsonObject CreateApprovalScenario() => new()
    {
        ["mode"] = "approve",
        ["now"] = Now,
        ["staged"] = false,
        ["agentItems"] = new JsonArray(new JsonObject { ["type"] = "approve_dependabot_pr", ["pr_number"] = 101, ["head_sha"] = HeadSha }),
        ["pr"] = new JsonObject
        {
            ["number"] = 101,
            ["user_login"] = "dependabot[bot]",
            ["head_sha"] = HeadSha,
            ["head_ref"] = "dependabot/npm_and_yarn/extension/lodash-4.17.21",
            ["title"] = "Bump lodash from 4.17.20 to 4.17.21 in /extension",
            ["body"] = "Bumps [lodash](https://github.com/lodash/lodash) from 4.17.20 to 4.17.21.",
        },
        ["files"] = new JsonArray("extension/package.json", "extension/yarn.lock"),
        ["contents"] = new JsonObject
        {
            ["extension/yarn.lock@base"] = YarnLockEntry("lodash", "4.17.20"),
            ["extension/yarn.lock@head"] = YarnLockEntry("lodash", "4.17.21"),
        },
        ["alerts"] = new JsonArray(new JsonObject
        {
            ["number"] = 7,
            ["ecosystem"] = "npm",
            ["package"] = "lodash",
            ["manifest_path"] = "extension/yarn.lock",
            ["vulnerable_version_range"] = "< 4.17.21",
            ["first_patched_version"] = "4.17.21",
        }),
        ["checkRuns"] = new JsonArray(new JsonObject { ["name"] = "ci", ["status"] = "completed", ["conclusion"] = "success" }),
        ["statuses"] = new JsonArray(),
        ["reviews"] = new JsonArray(),
        ["responses"] = new JsonObject
        {
            ["https://registry.npmjs.org/lodash"] = Response(new JsonObject { ["time"] = new JsonObject { ["4.17.21"] = "2026-09-01T00:00:00Z" } }),
        },
    };

    private async Task<JsonNode> RunHarnessAsync(JsonObject request)
    {
        if (request["mode"]?.GetValue<string>() is "agent-scrub" or "public-text-gate")
        {
            request["baseFiles"] ??= new JsonObject
            {
                ["extension/package-lock.json"] = PublicTextBase,
                ["extension/npm-shrinkwrap.json"] = PublicTextBase,
            };
        }
        using var workspace = TemporaryWorkspace.Create(testOutput);
        var requestPath = Path.Combine(workspace.Path, "request.json");
        var resultPath = Path.Combine(workspace.Path, "result.json");
        await File.WriteAllTextAsync(requestPath, request.ToJsonString());

        using var command = new NodeCommand(testOutput, "auto-sec");
        command.WithWorkingDirectory(RepoRoot.Path).WithTimeout(TimeSpan.FromSeconds(30));
        var result = await command.ExecuteScriptAsync(
            Path.Combine(RepoRoot.Path, "tests", "Infrastructure.Tests", "WorkflowScripts", "auto-sec.harness.js"),
            requestPath,
            resultPath);

        Assert.Equal(0, result.ExitCode);
        var response = JsonNode.Parse(await File.ReadAllTextAsync(resultPath));
        Assert.NotNull(response);
        return response;
    }

    private static string ReadWorkflow(string fileName)
        => File.ReadAllText(Path.Combine(RepoRoot.Path, ".github", "workflows", fileName));

    private static string GetSection(string text, string startPattern, string endPattern)
    {
        var options = RegexOptions.Multiline | RegexOptions.CultureInvariant;
        var start = Regex.Match(text, startPattern, options);
        Assert.True(start.Success, $"Missing section start '{startPattern}'.");
        var end = Regex.Match(text[(start.Index + start.Length)..], endPattern, options);
        Assert.True(end.Success, $"Missing section end '{endPattern}'.");
        return text.Substring(start.Index, start.Length + end.Index);
    }
}
