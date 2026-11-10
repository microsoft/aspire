// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text.Json;
using AnalyzeCiFailure;
using Aspire.TestUtilities;
using Xunit;

namespace Infrastructure.Tests;

/// <summary>Checks exact request binding, fresh run and PR facts, and guarded rerun effects.</summary>
public sealed partial class AnalyzeCiFailureWorkflowTests
{
    [Theory]
    [InlineData("9007199254740992", "enabled", true)]
    [InlineData("9007199254740993", "enabled", true)]
    [InlineData("9223372036854775807", "enabled", true)]
    [InlineData("9007199254740993", "main", true)]
    [InlineData("9007199254740993", "disabled", false)]
    [InlineData("9007199254740993", "enabled-uppercase", true)]
    [InlineData("9007199254740993", "enabled-one", false)]
    [InlineData("9007199254740993", "enabled-empty", false)]
    [InlineData("9007199254740993", "extra-field", true)]
    [InlineData("9007199254740993", "wrong-run", false)]
    [InlineData("9007199254740993", "wrong-pr", false)]
    [InlineData("9007199254740993", "unavailable", false)]
    [InlineData("9007199254740993", "infrastructure-tests", false)]
    [InlineData("9007199254740993", "code-issue", false)]
    [InlineData("9007199254740993", "closed", false)]
    [InlineData("9007199254740993", "locked", false)]
    [InlineData("9007199254740993", "unknown-pr", false)]
    [InlineData("9007199254740993", "lookup-failure", false)]
    [InlineData("9007199254740993", "advanced", false)]
    [InlineData("9007199254740993", "wrong-current-id", false)]
    [InlineData("9007199254740993", "missing-current-id", false)]
    [InlineData("9007199254740993", "missing-current-attempt", false)]
    [InlineData("9007199254740993", "malformed-current-attempt", false)]
    [InlineData("9007199254740993", "run-lookup-failure", false)]
    [InlineData("9007199254740993", "malformed-current-json", false)]
    [InlineData("9007199254740993", "post-failure", false)]
    [InlineData("9007199254740993", "duplicate-requests", false)]
    [InlineData("9223372036854775808", "enabled", false)]
    [InlineData("123\n", "enabled", false)]
    [InlineData("123\r", "enabled", false)]
    [InlineData("123\u2028", "enabled", false)]
    [RequiresTools(["bash"])]
    public async Task CSharpRerunValidatesFreshFactsAndUsesExactStringApiPaths(string id, string scenario, bool mutation)
    {
        var parsedId = long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0
            ? number : 9007199254740993;
        var evidence = AnalyzeCiFailureFixtures.CreateEvidence(scenario == "main" ? RunScope.Main : RunScope.PullRequest);
        evidence = evidence with
        {
            Run = evidence.Run with { Id = parsedId, Url = $"https://github.com/microsoft/aspire/actions/runs/{parsedId}" },
            SubjectPr = evidence.SubjectPr is { } pr ? pr with { Number = 9007199254740993 } : null,
            TestEvidence = evidence.TestEvidence with { State = scenario == "unavailable" ? EvidenceState.Unavailable : EvidenceState.Complete },
        };
        var proposal = AnalyzeCiFailureFixtures.CreateInfrastructureProposal(evidence);
        if (scenario == "infrastructure-tests")
        {
            evidence = evidence with
            {
                TestEvidence = evidence.TestEvidence with
                {
                    Failures =
                    [
                        new CollectedTestFailure
                        {
                            Ref = "test-1", JobId = 123, Name = "Tests.Fixture", DisplayName = "Tests.Fixture",
                            Diagnostics = new CollectedDiagnostics { Error = "Runner prerequisite failed", StackTrace = "", StandardOutput = "", StandardError = "" },
                            Sources = [new TestResultSource { ArtifactId = 10, ResultFile = "results.trx", Format = TestArtifactFormat.Trx, ExecutionId = "execution" }],
                        },
                    ],
                },
            };
            proposal = proposal with
            {
                Tests = [new ProposedTestDecision { Ref = "test-1", Classification = TestClassification.TransientInfrastructure, Reason = "Runner prerequisite failed" }],
            };
        }
        if (scenario == "code-issue")
        {
            proposal = proposal with { Jobs = [proposal.Jobs[0] with { Classification = JobClassification.CodeIssue }], Causes = [] };
        }
        var data = Directory.CreateDirectory(EvidenceDirectory).FullName;
        var analysis = Directory.CreateDirectory(Path.Combine(_workspace.Path, "proposal")).FullName;
        await File.WriteAllTextAsync(Path.Combine(data, "evidence.json"), TransientJson.Write(evidence));
        await File.WriteAllTextAsync(Path.Combine(analysis, "proposal.json"), TransientJson.Write(proposal));
        // A planted legacy acceptance file must not replace fresh evidence/proposal validation.
        await File.WriteAllTextAsync(
            Path.Combine(analysis, "accepted.json"),
            """{"schema_version":1,"verdict":"transient-infra","run_id":"9007199254740993"}""");
        var request = new
        {
            type = "rerun_failed_jobs", run_id = scenario == "wrong-run" ? "1" : id,
            pr_numbers = scenario == "main" ? "" : scenario == "wrong-pr" ? "1" : "9007199254740993",
            reason = "Transient infrastructure",
        };
        var output = Path.Combine(_workspace.Path, "agent-output.json");
        await File.WriteAllTextAsync(
            output,
            JsonSerializer.Serialize(new
            {
                items = scenario == "duplicate-requests" ? new[] { request, request } : [request],
            }));
        if (scenario == "extra-field")
        {
            var envelope = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(output))!;
            envelope["engine_metadata"] = "not authority";
            envelope["items"]![0]!["engine_added_field"] = 123;
            await File.WriteAllTextAsync(output, envelope.ToJsonString());
        }
        var fakeGh = await CreateFakeGhAsync("""
            #!/usr/bin/env bash
            set -eu
            printf '%s\n' "$*" >> rerun-calls.log
            case "$*" in
              "api repos/microsoft/aspire/pulls/"*)
                if [ "$RERUN_SCENARIO" = lookup-failure ]; then printf 'lookup failed: token=private-value' >&2; exit 23; fi
                if [ "$RERUN_SCENARIO" = unknown-pr ]; then printf '%s' '{"state":"open","locked":"false"}'
                elif [ "$RERUN_SCENARIO" = closed ]; then printf '%s' '{"state":"closed","locked":false}'
                elif [ "$RERUN_SCENARIO" = locked ]; then printf '%s' '{"state":"open","locked":true}'
                else printf '%s' '{"state":"open","locked":false}'; fi ;;
              *"/rerun-failed-jobs --method POST")
                if [ "$RERUN_SCENARIO" = post-failure ]; then printf 'request failed: token=private-value' >&2; exit 23; fi
                printf '{}' ;;
              "api repos/microsoft/aspire/actions/runs/"*)
                if [ "$RERUN_SCENARIO" = run-lookup-failure ]; then printf 'lookup failed: token=private-value' >&2; exit 23; fi
                if [ "$RERUN_SCENARIO" = malformed-current-json ]; then printf 'not JSON'
                elif [ "$RERUN_SCENARIO" = missing-current-id ]; then printf '{"run_attempt":1}'
                elif [ "$RERUN_SCENARIO" = missing-current-attempt ]; then printf '{"id":%s}' "$RERUN_ID"
                elif [ "$RERUN_SCENARIO" = malformed-current-attempt ]; then printf '{"id":%s,"run_attempt":"1"}' "$RERUN_ID"
                else printf '{"id":%s,"run_attempt":%s}' "$RERUN_LOOKUP_ID" "$RERUN_ATTEMPT"; fi ;;
              *) printf 'Unexpected call: %s\n' "$*" >&2; exit 99 ;;
            esac
            """);
        Assert.True(File.Exists(fakeGh));

        var result = await RunValidatorCommandAsync(
            ["workflow", "rerun", "--repo", "microsoft/aspire", "--analysis-dir", analysis, "--working-dir", _workspace.Path],
            new Dictionary<string, string>
            {
                ["PATH"] = $"{Path.GetDirectoryName(fakeGh)}{Path.PathSeparator}{Environment.GetEnvironmentVariable("PATH")}",
                ["GH_AW_AGENT_OUTPUT"] = output,
                ["ENABLE_RERUN"] = scenario switch
                {
                    "disabled" => "false",
                    "enabled-empty" => "",
                    "enabled-one" => "1",
                    "enabled-uppercase" => "TRUE",
                    _ => "true",
                },
                ["RERUN_SCENARIO"] = scenario,
                ["RERUN_ID"] = parsedId.ToString(CultureInfo.InvariantCulture),
                ["RERUN_LOOKUP_ID"] = scenario == "wrong-current-id" ? "99" : parsedId.ToString(CultureInfo.InvariantCulture),
                ["RERUN_ATTEMPT"] = scenario == "advanced" ? "2" : "1",
            });
        var calls = File.Exists(Path.Combine(_workspace.Path, "rerun-calls.log"))
            ? await File.ReadAllLinesAsync(Path.Combine(_workspace.Path, "rerun-calls.log")) : [];
        var runEndpoint = $"api repos/microsoft/aspire/actions/runs/{parsedId}";
        var prEndpoint = "api repos/microsoft/aspire/pulls/9007199254740993";
        if (mutation)
        {
            Assert.Equal(0, result.ExitCode);
            Assert.Equal(
                scenario == "main"
                ? new[] { runEndpoint, runEndpoint + "/rerun-failed-jobs --method POST" }
                : [prEndpoint, runEndpoint, runEndpoint + "/rerun-failed-jobs --method POST"],
                calls);
        }
        else if (scenario is "closed" or "locked" or "unknown-pr" or "lookup-failure" or "advanced")
        {
            Assert.Equal(0, result.ExitCode);
            Assert.Equal(scenario == "advanced" ? new[] { prEndpoint, runEndpoint } : [prEndpoint], calls);
        }
        else if (scenario is "wrong-current-id" or "missing-current-id" or "missing-current-attempt" or "malformed-current-attempt" or
            "run-lookup-failure" or "malformed-current-json" or "post-failure")
        {
            Assert.Equal(1, result.ExitCode);
            Assert.Equal(
                scenario == "post-failure"
                ? new[] { prEndpoint, runEndpoint, runEndpoint + "/rerun-failed-jobs --method POST" }
                : [prEndpoint, runEndpoint],
                calls);
            Assert.Contains(evidence.Run.Url, result.Output, StringComparison.Ordinal);
            if (scenario is "run-lookup-failure" or "post-failure")
            {
                Assert.Contains("HTTP 500.", result.Output, StringComparison.Ordinal);
            }
        }
        else
        {
            Assert.Equal(scenario is "disabled" or "enabled-empty" or "enabled-one" ? 0 : 1, result.ExitCode);
            Assert.Empty(calls);
        }
    }
}
