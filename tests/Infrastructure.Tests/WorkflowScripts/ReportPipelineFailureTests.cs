// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.TestUtilities;
using Xunit;

namespace Infrastructure.Tests;

/// <summary>
/// Tests for the pure helpers in
/// .github/workflows/report-pipeline-failure.js.
/// </summary>
public sealed class ReportPipelineFailureTests : IDisposable
{
    private static readonly JsonSerializerOptions s_jsonOptions = new(JsonSerializerDefaults.Web);

    private readonly TemporaryWorkspace _workspace;
    private readonly string _repoRoot;
    private readonly string _harnessPath;
    private readonly ITestOutputHelper _output;

    public ReportPipelineFailureTests(ITestOutputHelper output)
    {
        _output = output;
        _workspace = TemporaryWorkspace.Create(output);
        _repoRoot = RepoRoot.Path;
        _harnessPath = Path.Combine(_repoRoot, "tests", "Infrastructure.Tests", "WorkflowScripts", "report-pipeline-failure.harness.js");
    }

    public void Dispose() => _workspace.Dispose();

    [Theory]
    [InlineData("deployment-tests.yml", "<!-- ci-failure:deployment-tests.yml:scheduled -->")]
    [InlineData("tests-daily-smoke.yml", "<!-- ci-failure:tests-daily-smoke.yml:scheduled -->")]
    [RequiresTools(["node"])]
    public async Task BuildMarkerEmbedsWorkflowFile(string file, string expected)
    {
        var marker = await InvokeHarnessAsync<string>("buildMarker", new { workflowFile = file });

        Assert.Equal(expected, marker);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task BuildIssueTitleIsNeutral()
    {
        // The title must not pre-classify the failure as test-vs-infra: a nightly
        // deployment/smoke failure can be either, and these pipelines do not inspect
        // results to tell them apart.
        var title = await InvokeHarnessAsync<string>("buildIssueTitle", new { displayName = "Deployment E2E Tests" });

        Assert.Equal("Nightly run failing: Deployment E2E Tests", title);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task BuildIssueBodyIsStaticDescriptionWithMarker()
    {
        // Each failed run is recorded as a comment, so the body is a fixed
        // description: the marker (for lookup) plus prose, with no per-run table.
        var body = await InvokeHarnessAsync<string>(
            "buildIssueBody",
            new
            {
                marker = "<!-- ci-failure:deployment-tests.yml:scheduled -->",
                displayName = "Deployment E2E Tests",
                workflowFile = "deployment-tests.yml",
            });

        Assert.Contains("<!-- ci-failure:deployment-tests.yml:scheduled -->", body);
        Assert.Contains("is failing on its nightly run", body);
        Assert.Contains("comment below", body);
        Assert.DoesNotContain("ci-failure-runs:begin", body);
        Assert.DoesNotContain("[run #", body);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task BuildIssueBodyEmbedsCcWhenProvided()
    {
        // The first filing notifies a team via an @mention in the body; subsequent
        // failures notify via comment.
        var withCc = await InvokeHarnessAsync<string>(
            "buildIssueBody",
            new
            {
                marker = "<!-- ci-failure:deployment-tests.yml:scheduled -->",
                displayName = "Deployment E2E Tests",
                workflowFile = "deployment-tests.yml",
                cc = "@microsoft/aspire-team",
            });
        Assert.Contains("/cc @microsoft/aspire-team", withCc);

        var withoutCc = await InvokeHarnessAsync<string>(
            "buildIssueBody",
            new
            {
                marker = "<!-- ci-failure:tests-daily-smoke.yml:scheduled -->",
                displayName = "Daily CLI Smoke Tests",
                workflowFile = "tests-daily-smoke.yml",
            });
        Assert.DoesNotContain("/cc", withoutCc);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task FormatCommentReferencesRunAndCommit()
    {
        var comment = await InvokeHarnessAsync<string>(
            "formatComment",
            new { run = new { runNumber = 7, runUrl = "https://x/runs/7", sha = "abcdef0123456789" } });

        Assert.Contains("[run #7](https://x/runs/7)", comment);
        Assert.Contains("`abcdef01`", comment);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task FormatCommentAppendsDetailWhenProvided()
    {
        // Per-run caller detail (e.g. the smoke suite's CLI versions) rides on the
        // failure comment that records the run.
        var comment = await InvokeHarnessAsync<string>(
            "formatComment",
            new
            {
                run = new { runNumber = 7, runUrl = "https://x/runs/7", sha = "abcdef0123456789" },
                detail = "### Aspire CLI versions tested\n\n- 9.0.0"
            });

        Assert.Contains("[run #7](https://x/runs/7)", comment);
        Assert.Contains("### Aspire CLI versions tested", comment);
        Assert.Contains("- 9.0.0", comment);
    }

    private async Task<T> InvokeHarnessAsync<T>(string operation, object payload)
    {
        var requestPath = Path.Combine(_workspace.Path, $"{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(new { operation, payload }, s_jsonOptions));

        using var command = new NodeCommand(_output, "report-pipeline-failure");
        command.WithWorkingDirectory(_repoRoot);

        var result = await command.ExecuteScriptAsync(_harnessPath, requestPath);
        Assert.Equal(0, result.ExitCode);

        var response = JsonSerializer.Deserialize<HarnessResponse<T>>(result.Output, s_jsonOptions);
        Assert.NotNull(response);
        return response!.Result;
    }

    private sealed record HarnessResponse<T>(T Result);
}