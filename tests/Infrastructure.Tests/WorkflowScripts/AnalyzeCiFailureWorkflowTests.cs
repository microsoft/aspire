// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.TestUtilities;
using Xunit;

namespace Infrastructure.Tests;

/// <summary>
/// Tests for the analyze-ci-failure workflow and its JavaScript helper.
/// </summary>
public sealed class AnalyzeCiFailureWorkflowTests : IDisposable
{
    private static readonly JsonSerializerOptions s_jsonOptions = new(JsonSerializerDefaults.Web);

    private readonly TemporaryWorkspace _workspace;
    private readonly string _repoRoot;
    private readonly string _scriptPath;
    private readonly ITestOutputHelper _output;

    public AnalyzeCiFailureWorkflowTests(ITestOutputHelper output)
    {
        _output = output;
        _workspace = TemporaryWorkspace.Create(output);
        _repoRoot = RepoRoot.Path;
        _scriptPath = Path.Combine(_repoRoot, ".github", "workflows", "analyze-ci-failure.js");
    }

    public void Dispose() => _workspace.Dispose();

    [Theory]
    [InlineData("analyze-ci-failure.md")]
    [InlineData("analyze-ci-failure.lock.yml")]
    public void PublishAnalysisJobPersistsRequiredEnvironmentVariables(string workflowName)
    {
        var workflowPath = Path.Combine(_repoRoot, ".github", "workflows", workflowName);
        var workflow = File.ReadAllText(workflowPath);
        var persistIndex = workflow.IndexOf("- name: Persist publish environment", StringComparison.Ordinal);
        var checkoutIndex = workflow.IndexOf("- name: Checkout issue renderer", persistIndex, StringComparison.Ordinal);
        var publishIndex = workflow.IndexOf("- name: Publish analysis data and comment on PR", checkoutIndex, StringComparison.Ordinal);

        Assert.True(persistIndex >= 0, $"The environment persistence step was not found in {workflowName}.");
        Assert.True(checkoutIndex > persistIndex, $"The checkout step must follow environment persistence in {workflowName}.");
        Assert.True(publishIndex > checkoutIndex, $"The publish step must follow checkout in {workflowName}.");

        var persistStep = workflow[persistIndex..checkoutIndex];
        Assert.Contains("GH_AW_AGENT_OUTPUT=$GH_AW_AGENT_OUTPUT", persistStep, StringComparison.Ordinal);
        Assert.Contains("GH_TOKEN=$GH_TOKEN", persistStep, StringComparison.Ordinal);
        Assert.Contains("$GITHUB_ENV", persistStep, StringComparison.Ordinal);

        if (workflowName.EndsWith(".lock.yml", StringComparison.Ordinal))
        {
            Assert.Contains("GH_AW_AGENT_OUTPUT:", persistStep, StringComparison.Ordinal);
            Assert.Contains("GH_TOKEN:", persistStep, StringComparison.Ordinal);
        }
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task AddOccurrenceUsesTheCauseJobInAMultiJobRun()
    {
        var analysis = CreateAnalysis();
        var cause = new
        {
            id = "later-job-failure",
            type = "infra-failure",
            title = "Later job failed",
            job_name = "Tests / Linux",
            error_pattern = "connection reset",
            failure_details = "Password: memory-branch-secret"
        };

        var output = await InvokeScriptAsync("add-occurrence", analysis, cause);
        var result = JsonSerializer.Deserialize<JsonElement>(output, s_jsonOptions);

        var occurrence = Assert.Single(result.GetProperty("occurrences").EnumerateArray());
        Assert.Equal("Tests / Linux", occurrence.GetProperty("job").GetString());
        Assert.Equal(987654, occurrence.GetProperty("run_id").GetInt32());
        Assert.Equal("Password: [REDACTED]", result.GetProperty("failure_details").GetString());

        var occurrenceRow = await InvokeScriptAsync("occurrence-row", analysis, cause);
        Assert.Equal("| 2026-07-22 | [987654](https://github.com/microsoft/aspire/actions/runs/987654) | Tests / Linux | #18763 |", occurrenceRow);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task BuildFlakyTestIssueBodyUsesMatchingJobAndEscapesHtml()
    {
        var analysis = CreateAnalysis(
            failedTests:
            [
                new
                {
                    name = "Tests.SampleTheory(`value`)\n@maintainers",
                    job = "Tests / Windows",
                    error = "Wrong job",
                    stack_trace = "",
                    reason = "Wrong classification"
                },
                new
                {
                    name = "Tests.SampleTheory(`value`)\n@maintainers",
                    job = "Tests / Linux",
                    error = "Expected <actual> & stable",
                    stack_trace = "at <frame>",
                    standard_output = "Connecting to https://user:password@example.com\nSECRET_TOKEN=unmasked-token\nRetry remains useful",
                    standard_error = "Server=database;Password=unmasked-password;Timeout=30\nAssertion context remains useful",
                    reason = "Retry <later> & inspect \"logs\".\n@team\n# Heading\n[logs](https://example.com)"
                }
            ]);
        var cause = new
        {
            id = "sample-theory-flake",
            type = "flaky-test",
            title = "Sample theory is flaky",
            test_name = "Tests.SampleTheory(`value`)\n@maintainers",
            job_name = "Tests / Linux",
            error_pattern = "Expected actual"
        };

        var body = await InvokeScriptAsync(
            "issue-body",
            analysis,
            cause,
            "<!-- ci-failure-cause:sample-theory-flake -->");

        var expected = """
            <!-- ci-failure-cause:sample-theory-flake -->

            ## Build Information

            Build: https://github.com/microsoft/aspire/actions/runs/987654
            Build error leg or test failing: Tests / Linux / ``Tests.SampleTheory(`value`) @maintainers``
            Pull request: #18763

            ## Classification Analysis

            <pre>
            Retry &lt;later&gt; &amp; inspect &quot;logs&quot;.
            @team
            # Heading
            [logs](https://example.com)
            </pre>

            ## Failure Information

            <details>
            <summary>Test output</summary>

            <pre>
            Error:
            Expected &lt;actual&gt; &amp; stable

            Stack Trace:
            at &lt;frame&gt;

            Standard Output:
            Connecting to https://[REDACTED]:[REDACTED]@example.com
            SECRET_TOKEN=[REDACTED]
            Retry remains useful

            Standard Error:
            Server=database;Password=[REDACTED];Timeout=30
            Assertion context remains useful
            </pre>

            </details>

            ## Description

            Sample theory is flaky

            **Type**: flaky-test

            ## Occurrences

            | Date | Build | Job | PR |
            |------|-------|-----|----|
            | 2026-07-22 | [987654](https://github.com/microsoft/aspire/actions/runs/987654) | Tests / Linux | #18763 |
            """.ReplaceLineEndings("\n");

        Assert.Equal(expected, body);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task BuildInfrastructureIssueBodyUsesTheMatchingJob()
    {
        var analysis = CreateAnalysis();
        var cause = new
        {
            id = "linux-network-failure",
            type = "infra-failure",
            title = "Linux network failure",
            job_name = "Tests / Linux",
            analysis = "Fallback should not be used",
            failure_details = "Request to <feed> failed & timed out"
        };

        var body = await InvokeScriptAsync(
            "issue-body",
            analysis,
            cause,
            "<!-- ci-failure-cause:linux-network-failure -->");

        Assert.Contains("Build error leg: Tests / Linux", body);
        Assert.Contains("Linux runner lost &lt;network&gt; connectivity.", body);
        Assert.Contains("Request to &lt;feed&gt; failed &amp; timed out", body);
        Assert.Contains("| Tests / Linux | #18763 |", body);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task BuildInfrastructureIssueBodyUsesCauseAnalysisWhenJobDoesNotMatch()
    {
        var analysis = CreateAnalysis();
        var cause = new
        {
            id = "unknown-job-failure",
            type = "infra-failure",
            title = "Unknown job failure",
            job_name = "Tests / Missing",
            analysis = "Cause-specific analysis",
            failure_details = "Failure details"
        };

        var body = await InvokeScriptAsync(
            "issue-body",
            analysis,
            cause,
            "<!-- ci-failure-cause:unknown-job-failure -->");

        Assert.Contains("<pre>\nCause-specific analysis\n</pre>", body);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task FormatTestFailuresUsesSafeMarkdownFences()
    {
        var failures = new[]
        {
            new
            {
                test = "Tests.SampleTheory(`value`)\n@maintainers",
                error = "Expected ``` but got value",
                stack_trace = "at ````frame````",
                standard_output = "before\n```\n@team\n# heading",
                standard_error = "stderr"
            }
        };

        var output = await InvokeScriptAsync("format-test-failures", failures);

        var expected = """
            ### ``Tests.SampleTheory(`value`) @maintainers``

            **Error:**
            ````
            Expected ``` but got value
            ````
            **Stack Trace:**
            `````
            at ````frame````
            `````
            **Standard Output (sensitive values redacted):**
            ````
            before
            ```
            @team
            # heading
            ````
            **Standard Error (sensitive values redacted):**
            ```
            stderr
            ```
            """.ReplaceLineEndings("\n");

        Assert.Equal(expected, output);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task RedactOperationRemovesSensitiveValuesAndPreservesDiagnostics()
    {
        var privateKeyAcrossTruncationBoundary = $"{new string('x', 3950)}-----BEGIN PRIVATE KEY-----\n{new string('k', 200)}\n-----END PRIVATE KEY-----\nExpected 42 but got 41";
        var input = new
        {
            standard_output = privateKeyAcrossTruncationBoundary,
            standard_error = "Host=db;Password=secret-value;Timeout=30\nTOKEN: colon-secret\nhttps://user:pass@example.com/path",
            truncated_private_key = "Diagnostic prefix\n-----BEGIN RSA PRIVATE KEY-----\nsecret-key-material",
            nested = new[] { "eyJ1234567890.abcdefghijk.ABCDEFGHIJK" }
        };

        var output = await InvokeScriptAsync("redact", input);
        var redacted = JsonSerializer.Deserialize<JsonElement>(output, s_jsonOptions);
        Assert.Equal(
            $"{new string('x', 3950)}[REDACTED]\nExpected 42 but got 41",
            redacted.GetProperty("standard_output").GetString());
        Assert.Equal(
            "Host=db;Password=[REDACTED];Timeout=30\nTOKEN: [REDACTED]\nhttps://[REDACTED]:[REDACTED]@example.com/path",
            redacted.GetProperty("standard_error").GetString());
        Assert.Equal("Diagnostic prefix\n[REDACTED]", redacted.GetProperty("truncated_private_key").GetString());
        Assert.Equal("[REDACTED]", redacted.GetProperty("nested")[0].GetString());
    }

    private static object CreateAnalysis(object[]? failedTests = null)
    {
        return new
        {
            run_id = 987654,
            run_url = "https://github.com/microsoft/aspire/actions/runs/987654",
            analyzed_at = "2026-07-22T10:30:00Z",
            pr = new { number = 18763 },
            failed_jobs = new[]
            {
                new { name = "Tests / Windows", classification = "transient-infra", reason = "Windows runner failed" },
                new { name = "Tests / Linux", classification = "transient-infra", reason = "Linux runner lost <network> connectivity." }
            },
            failed_tests = failedTests ?? []
        };
    }

    private async Task<string> InvokeScriptAsync(string operation, object analysis, object cause, string? marker = null)
    {
        var analysisPath = Path.Combine(_workspace.Path, $"{Guid.NewGuid():N}-analysis.json");
        var causePath = Path.Combine(_workspace.Path, $"{Guid.NewGuid():N}-cause.json");
        await File.WriteAllTextAsync(analysisPath, JsonSerializer.Serialize(analysis, s_jsonOptions));
        await File.WriteAllTextAsync(causePath, JsonSerializer.Serialize(cause, s_jsonOptions));

        using var command = new NodeCommand(_output, "analyze-ci-failure");
        command.WithWorkingDirectory(_repoRoot);

        var arguments = marker is null
            ? new[] { operation, analysisPath, causePath }
            : new[] { operation, analysisPath, causePath, marker };
        var result = await command.ExecuteScriptAsync(_scriptPath, arguments);
        Assert.Equal(0, result.ExitCode);

        return result.Output.ReplaceLineEndings("\n");
    }

    private async Task<string> InvokeScriptAsync(string operation, object input)
    {
        var inputPath = Path.Combine(_workspace.Path, $"{Guid.NewGuid():N}-input.json");
        await File.WriteAllTextAsync(inputPath, JsonSerializer.Serialize(input, s_jsonOptions));

        using var command = new NodeCommand(_output, $"analyze-ci-failure-{operation}");
        command.WithWorkingDirectory(_repoRoot);

        var result = await command.ExecuteScriptAsync(_scriptPath, operation, inputPath);
        Assert.Equal(0, result.ExitCode);

        return result.Output.ReplaceLineEndings("\n");
    }
}