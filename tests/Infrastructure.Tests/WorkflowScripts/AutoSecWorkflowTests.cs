// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Aspire.TestUtilities;
using Xunit;

namespace Infrastructure.Tests;

[Trait("Category", "AgenticWorkflow")]
public sealed class AutoSecWorkflowTests(ITestOutputHelper testOutput)
{
    private const string HeadSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Now = "2026-10-01T00:00:00Z";

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
    [InlineData("extension/package.json", PackageJsonBase, "    \"lodash\": \"^4.17.20\"", "    \"lodash\": \"^4.17.21\"", "stale", "non-version-manifest-edit")]
    [InlineData("extension/package.json", PackageJsonBase, "    \"preinstall\": \"1.0.0\"", "    \"preinstall\": \"1.0.1\"", "workspace", "non-version-manifest-edit")]
    [InlineData("extension/package.json", PackageJsonBase, "    \"build\": \"vite --mode=1\",", "    \"build\": \"vite --mode=2\",", "workspace", "non-version-manifest-edit")]
    [InlineData("extension/package.json", PackageJsonBase, "  \"scripts\": {", "  \"scripts\": {\n    \"postinstall\": \"node x.js\",", "workspace", "non-version-manifest-edit")]
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
        // Replacement hunk with a context line on each side, applied at the real position in
        // baseText and labeled with baseText's git blob ID.
        var baseLines = baseText.Split('\n');
        var removed = oldLine.Split('\n');
        var at = Array.IndexOf(baseLines, removed[0]);
        Assert.True(at > 0 && at + removed.Length < baseLines.Length);
        Assert.Equal(removed, baseLines[at..(at + removed.Length)]);
        var added = newLine.Split('\n');
        var hunk = string.Join('\n', [$" {baseLines[at - 1]}", .. removed.Select(line => $"-{line}"), .. added.Select(line => $"+{line}"), $" {baseLines[at + removed.Length]}"]);
        var patch = $"From 0000000000000000000000000000000000000000 Mon Sep 17 00:00:00 2001\nSubject: [PATCH] update\n\n---\ndiff --git a/{path} b/{path}\nindex {GitBlobId(baseText)[..7]}..2222222 100644\n--- a/{path}\n+++ b/{path}\n@@ -{at},{removed.Length + 2} +{at},{added.Length + 2} @@\n{hunk}\n-- \n2.43.0\n";

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

        // These hunks only exercise the source check, so they never rebuild a full file; the
        // gate's separate reconstruction requirement always reports them and is not under test.
        var violations = result["value"]!["violations"]!.AsArray()
            .Select(v => $"{v!["path"]} {v["reason"]}")
            .Where(v => v != "extension/package-lock.json unreconstructable-file-diff")
            .ToArray();
        Assert.Equal(expectedReason.Length == 0 ? [] : [$"extension/package-lock.json {expectedReason}"], violations);
    }

    private const string PublicTextPatch = """
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
        @@ -1,3 +1,3 @@
             "node_modules/lodash": {
        -      "version": "4.17.20",
        +      "version": "4.17.21",
             },
        @@ -9,3 +9,3 @@
             "node_modules/@types/node": {
        -      "version": "22.0.0",
        +      "version": "22.1.0",
             },
        -- 
        2.43.0

        """;

    private const string PublicTextBody = """
        This is an automated pull request created by the auto-sec workflow.

        It updates the following dependencies to newer, non-breaking versions that have
        been published for at least 7 days and resolve from the existing package sources:

        | Package | Manifest | From | To |
        | --- | --- | --- | --- |
        | lodash | extension/package-lock.json | 4.17.20 | 4.17.21 |
        | `@types/node` | extension/package-lock.json | 22.0.0 | 22.1.0 |

        No package sources or feeds were changed. Please review the lockfile diffs and
        CI results before merging.
        """;

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
        var pushMessage = "Update dependencies\n\nlodash 4.17.20 -> 4.17.21";
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

    [Fact]
    [RequiresTools(["node"])]
    public async Task AgentOutputScrubRemovesTranscriptAndKeepsTemplateOutputs()
    {
        const string headSha = "0123456789abcdef0123456789abcdef01234567";
        var result = await RunHarnessAsync(new JsonObject
        {
            ["mode"] = "agent-scrub",
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
        Assert.Equal(["agent_execution.json", "aw-auto-sec-security-updates.patch"], result["remaining"]!.AsArray().Select(name => name!.GetValue<string>()));
        var outputs = result["outputs"]!.AsArray().Select(line => JsonNode.Parse(line!.GetValue<string>())!).ToArray();
        Assert.Collection(
            outputs,
            item => Assert.Equal("create_pull_request", item["type"]!.GetValue<string>()),
            item => Assert.Equal(new JsonObject { ["type"] = "approve_dependabot_pr", ["pr_number"] = 101, ["head_sha"] = headSha }.ToJsonString(), item.ToJsonString()),
            item => Assert.Equal("noop", item["type"]!.GetValue<string>()));
    }

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

        Assert.Equal([expected], result["value"]!["violations"]!.AsArray().Select(v => $"{v!["source"]} {v["reason"]}"));
        Assert.Empty(result["outputs"]!.AsArray());
        Assert.Empty(result["remaining"]!.AsArray());
        // The failure names the field and reason only, never the rejected text.
        var failure = Assert.Single(result["failures"]!.AsArray())!.GetValue<string>();
        Assert.DoesNotContain("alert 42", failure, StringComparison.OrdinalIgnoreCase);
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
        Assert.Contains("await gate.runPublicTextGate({ core })", textGateSection, StringComparison.Ordinal);
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
        Assert.Contains("runAgentOutputScrub({ core })", compiled, StringComparison.Ordinal);
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
