// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.TestUtilities;
using Xunit;

namespace Infrastructure.Tests;

/// <summary>
/// Tests for the pure helpers in
/// .github/workflows/report-specialized-test-failures.js.
/// </summary>
public sealed class ReportSpecializedTestFailuresTests : IDisposable
{
    private static readonly JsonSerializerOptions s_jsonOptions = new(JsonSerializerDefaults.Web);

    private readonly TestTempDirectory _tempDirectory = new();
    private readonly string _repoRoot;
    private readonly string _harnessPath;
    private readonly ITestOutputHelper _output;

    public ReportSpecializedTestFailuresTests(ITestOutputHelper output)
    {
        _output = output;
        _repoRoot = RepoRoot.Path;
        _harnessPath = Path.Combine(_repoRoot, "tests", "Infrastructure.Tests", "WorkflowScripts", "report-specialized-test-failures.harness.js");
    }

    public void Dispose() => _tempDirectory.Dispose();

    [Theory]
    [InlineData("test-failures", "<!-- ci-failure:tests-outerloop.yml:test-failures -->")]
    [InlineData("infra", "<!-- ci-failure:tests-quarantine.yml:infra -->")]
    [RequiresTools(["node"])]
    public async Task BuildMarkerEmbedsWorkflowAndKind(string kind, string expected)
    {
        var file = kind == "infra" ? "tests-quarantine.yml" : "tests-outerloop.yml";
        var marker = await InvokeHarnessAsync<string>("buildMarker", new { workflowFile = file, kind });

        Assert.Equal(expected, marker);
    }

    [Theory]
    [InlineData("test-failures", "failing-test")]
    [InlineData("infra", "automation-broken")]
    [RequiresTools(["node"])]
    public async Task LabelForKindMapsCorrectly(string kind, string expected)
    {
        var label = await InvokeHarnessAsync<string>("labelForKind", new { kind });

        Assert.Equal(expected, label);
    }

    [Theory]
    [InlineData("test-failures", "Test failures: Outerloop Tests")]
    [InlineData("infra", "CI infrastructure failing: Outerloop Tests")]
    [RequiresTools(["node"])]
    public async Task BuildIssueTitleVariesByKind(string kind, string expected)
    {
        var title = await InvokeHarnessAsync<string>("buildIssueTitle", new { displayName = "Outerloop Tests", kind });

        Assert.Equal(expected, title);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task ClassifyFailureTreatsQuarantineFailureAsInfra()
    {
        // ignoreTestFailures (quarantine): even with failed tests reported, a failed
        // run is infra because run-tests.yml swallows test failures.
        var kind = await InvokeHarnessAsync<string>(
            "classifyFailure",
            new { result = "failure", failedCount = 5, ignoreTestFailures = true });

        Assert.Equal("infra", kind);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task ClassifyFailureIsTestFailuresWhenOuterloopTestsFailed()
    {
        var kind = await InvokeHarnessAsync<string>(
            "classifyFailure",
            new { result = "failure", failedCount = 3, ignoreTestFailures = false });

        Assert.Equal("test-failures", kind);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task ClassifyFailureIsInfraWhenOuterloopFailedWithNoTestFailures()
    {
        var kind = await InvokeHarnessAsync<string>(
            "classifyFailure",
            new { result = "failure", failedCount = 0, ignoreTestFailures = false });

        Assert.Equal("infra", kind);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task ClassifyFailureIsNoneWhenRunSucceeded()
    {
        var kind = await InvokeHarnessAsync<string>(
            "classifyFailure",
            new { result = "success", failedCount = 0, ignoreTestFailures = false });

        Assert.Equal("none", kind);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task ClassifyFailureIsTestFailuresWhenExtractionFailedOnRedOuterloopRun()
    {
        // A genuinely-red outerloop run whose results could not be extracted
        // (download/tool flake) must not be misfiled as infra — that would lose
        // the failing-test signal and open a second, mismatched issue.
        var kind = await InvokeHarnessAsync<string>(
            "classifyFailure",
            new { result = "failure", failedCount = 0, ignoreTestFailures = false, extractionFailed = true });

        Assert.Equal("test-failures", kind);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task BuildIssueBodyIsStaticDescriptionWithMarker()
    {
        // Each failed run is recorded as a comment, so the body is a fixed
        // description: the marker (for lookup) plus kind-specific prose, no table.
        var body = await InvokeHarnessAsync<string>(
            "buildIssueBody",
            new
            {
                marker = "<!-- ci-failure:tests-outerloop.yml:test-failures -->",
                displayName = "Outerloop Tests",
                workflowFile = "tests-outerloop.yml",
                kind = "test-failures",
            });

        Assert.StartsWith("<!-- ci-failure:tests-outerloop.yml:test-failures -->", body);
        Assert.Contains("Outerloop tests are failing", body);
        Assert.Contains("added as a comment", body);
        Assert.DoesNotContain("ci-failure-runs:begin", body);
        Assert.DoesNotContain("[run #", body);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task FormatCommentForTestFailuresWithNoNamesPointsAtArtifacts()
    {
        var comment = await InvokeHarnessAsync<string>(
            "formatComment",
            new
            {
                kind = "test-failures",
                run = new { runNumber = 7, runUrl = "https://x/runs/7" },
                failedTests = Array.Empty<string>()
            });

        Assert.Contains("could not be extracted", comment);
        Assert.Contains("[run #7](https://x/runs/7)", comment);
        Assert.DoesNotContain("0 test(s) failed", comment);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task FormatCommentEscapesBacktickInTestName()
    {
        // A test display name containing a backtick must not break out of the
        // inline code span (Markdown/mention injection).
        var comment = await InvokeHarnessAsync<string>(
            "formatComment",
            new
            {
                kind = "test-failures",
                run = new { runNumber = 7, runUrl = "https://x/runs/7" },
                failedTests = new[] { "Tests.Type.Method(arg: `evil`)" }
            });

        Assert.Contains("Tests.Type.Method(arg: `evil`)", comment);
        // Wrapped in a longer fence so the embedded backtick cannot close the span.
        Assert.Contains("``", comment);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task FormatCommentListsFailedTestsCapped()
    {
        var comment = await InvokeHarnessAsync<string>(
            "formatComment",
            new
            {
                kind = "test-failures",
                run = new { runNumber = 7, runUrl = "https://x/runs/7" },
                failedTests = new[] { "A.B.C", "D.E.F", "G.H.I" },
                maxListed = 2
            });

        Assert.Contains("3 test(s) failed in [run #7](https://x/runs/7)", comment);
        Assert.Contains("`A.B.C`", comment);
        Assert.Contains("`D.E.F`", comment);
        Assert.Contains("and 1 more", comment);
        Assert.DoesNotContain("`G.H.I`", comment);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task FormatCommentForInfraReferencesRunOnly()
    {
        var comment = await InvokeHarnessAsync<string>(
            "formatComment",
            new
            {
                kind = "infra",
                run = new { runNumber = 7, runUrl = "https://x/runs/7" },
                failedTests = Array.Empty<string>()
            });

        Assert.Contains("Infrastructure failure", comment);
        Assert.Contains("[run #7](https://x/runs/7)", comment);
    }

    private async Task<T> InvokeHarnessAsync<T>(string operation, object payload)
    {
        var requestPath = Path.Combine(_tempDirectory.Path, $"{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(new { operation, payload }, s_jsonOptions));

        using var command = new NodeCommand(_output, "report-specialized-test-failures");
        command.WithWorkingDirectory(_repoRoot);

        var result = await command.ExecuteScriptAsync(_harnessPath, requestPath);
        Assert.Equal(0, result.ExitCode);

        var response = JsonSerializer.Deserialize<HarnessResponse<T>>(result.Output, s_jsonOptions);
        Assert.NotNull(response);
        return response!.Result;
    }

    private sealed record HarnessResponse<T>(T Result);
}
