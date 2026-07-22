// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.TestUtilities;
using Xunit;

namespace Infrastructure.Tests;

/// <summary>
/// Tests for .github/workflows/analyze-ci-failure.js.
/// </summary>
public sealed class AnalyzeCiFailureWorkflowTests : IDisposable
{
    private static readonly JsonSerializerOptions s_jsonOptions = new(JsonSerializerDefaults.Web);

    private readonly TemporaryWorkspace _workspace;
    private readonly string _repoRoot;
    private readonly string _harnessPath;
    private readonly ITestOutputHelper _output;

    public AnalyzeCiFailureWorkflowTests(ITestOutputHelper output)
    {
        _output = output;
        _workspace = TemporaryWorkspace.Create(output);
        _repoRoot = RepoRoot.Path;
        _harnessPath = Path.Combine(_repoRoot, "tests", "Infrastructure.Tests", "WorkflowScripts", "analyze-ci-failure.harness.js");
    }

    public void Dispose() => _workspace.Dispose();

    [Fact]
    [RequiresTools(["node"])]
    public async Task AddOccurrenceUsesTheCauseJobInAMultiJobRun()
    {
        var analysis = CreateAnalysis();
        var cause = new
        {
            id = "later-job-failure",
            type = "transient-infra",
            title = "Later job failed",
            job_name = "Tests / Linux",
            error_pattern = "connection reset"
        };

        var result = await InvokeHarnessAsync<JsonElement>("addOccurrence", new { analysis, cause });

        var occurrence = Assert.Single(result.GetProperty("occurrences").EnumerateArray());
        Assert.Equal("Tests / Linux", occurrence.GetProperty("job").GetString());
        Assert.Equal(987654, occurrence.GetProperty("run_id").GetInt32());
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
                    name = "Tests.SampleTheory(value: 1)",
                    job = "Tests / Windows",
                    error = "Wrong job",
                    stack_trace = "",
                    standard_output = "",
                    standard_error = "",
                    reason = "Wrong classification"
                },
                new
                {
                    name = "Tests.SampleTheory(value: 1)",
                    job = "Tests / Linux",
                    error = "Expected <actual> & stable",
                    stack_trace = "at <frame>",
                    standard_output = "stdout <b>bold</b>",
                    standard_error = "stderr 'quoted'",
                    reason = "Retry <later> & inspect \"logs\"."
                }
            ]);
        var cause = new
        {
            id = "sample-theory-flake",
            type = "flaky-test",
            title = "Sample theory is flaky",
            test_name = "Tests.SampleTheory(value: 1)",
            job_name = "Tests / Linux",
            error_pattern = "Expected actual"
        };

        var body = await InvokeHarnessAsync<string>(
            "buildIssueBody",
            new { analysis, cause, marker = "<!-- ci-failure-cause:sample-theory-flake -->" });

        var expected = """
            <!-- ci-failure-cause:sample-theory-flake -->

            ## Build Information

            Build: https://github.com/microsoft/aspire/actions/runs/987654
            Build error leg or test failing: Tests / Linux / `Tests.SampleTheory(value: 1)`
            Pull request: #18763

            ## Classification Analysis

            Retry &lt;later&gt; &amp; inspect &quot;logs&quot;.

            ## Failure Information

            <details>
            <summary>Test output</summary>

            <pre>
            Error:
            Expected &lt;actual&gt; &amp; stable

            Stack Trace:
            at &lt;frame&gt;

            Standard Output:
            stdout &lt;b&gt;bold&lt;/b&gt;

            Standard Error:
            stderr &#39;quoted&#39;
            </pre>

            </details>

            ## Description

            Sample theory is flaky

            **Type**: flaky-test

            ## Occurrences

            | Date | Build | Job | PR |
            |------|-------|-----|----|
            | 2026-07-22 | [987654](https://github.com/microsoft/aspire/actions/runs/987654) | Tests / Linux | #18763 |
            """.ReplaceLineEndings("\n") + "\n";

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
            type = "transient-infra",
            title = "Linux network failure",
            job_name = "Tests / Linux",
            analysis = "Fallback should not be used",
            failure_details = "Request to <feed> failed & timed out"
        };

        var body = await InvokeHarnessAsync<string>(
            "buildIssueBody",
            new { analysis, cause, marker = "<!-- ci-failure-cause:linux-network-failure -->" });

        Assert.Contains("Build error leg: Tests / Linux", body);
        Assert.Contains("Linux runner lost &lt;network&gt; connectivity.", body);
        Assert.Contains("Request to &lt;feed&gt; failed &amp; timed out", body);
        Assert.Contains("| Tests / Linux | #18763 |", body);
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

    private async Task<T> InvokeHarnessAsync<T>(string operation, object payload)
    {
        var requestPath = Path.Combine(_workspace.Path, $"{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(new { operation, payload }, s_jsonOptions));

        using var command = new NodeCommand(_output, "analyze-ci-failure");
        command.WithWorkingDirectory(_repoRoot);

        var result = await command.ExecuteScriptAsync(_harnessPath, requestPath);
        Assert.Equal(0, result.ExitCode);

        var response = JsonSerializer.Deserialize<HarnessResponse<T>>(result.Output, s_jsonOptions);
        Assert.NotNull(response);
        return response!.Result;
    }

    private sealed record HarnessResponse<T>(T Result);
}