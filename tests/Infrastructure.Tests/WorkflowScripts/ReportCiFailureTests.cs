// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.TestUtilities;
using Xunit;

namespace Infrastructure.Tests;

/// <summary>
/// Tests for the pure decision/formatting helpers in
/// .github/workflows/report-ci-failure.js: the per-branch dedup marker, issue
/// title/body (autoClose:true stamped), and the per-run failure comment.
/// </summary>
public sealed class ReportCiFailureTests : IDisposable
{
    private static readonly JsonSerializerOptions s_jsonOptions = new(JsonSerializerDefaults.Web);

    private readonly TemporaryWorkspace _workspace;
    private readonly string _repoRoot;
    private readonly string _harnessPath;
    private readonly ITestOutputHelper _output;

    public ReportCiFailureTests(ITestOutputHelper output)
    {
        _output = output;
        _workspace = TemporaryWorkspace.Create(output);
        _repoRoot = RepoRoot.Path;
        _harnessPath = Path.Combine(_repoRoot, "tests", "Infrastructure.Tests", "WorkflowScripts", "report-ci-failure.harness.js");
    }

    public void Dispose() => _workspace.Dispose();

    [Theory]
    [InlineData("main", "<!-- ci-failure:ci.yml:push:main -->")]
    [InlineData("release/13.3", "<!-- ci-failure:ci.yml:push:release/13.3 -->")]
    [RequiresTools(["node"])]
    public async Task BuildMarkerEmbedsRef(string @ref, string expected)
    {
        var marker = await InvokeHarnessAsync<string>("buildMarker", new { @ref });

        Assert.Equal(expected, marker);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task BuildIssueTitleNamesTheBranch()
    {
        var title = await InvokeHarnessAsync<string>("buildIssueTitle", new { @ref = "release/13.3" });

        Assert.Equal("CI failing on `release/13.3`", title);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task BuildIssueBodyStampsAutoCloseTrueAndCarriesMarker()
    {
        var marker = "<!-- ci-failure:ci.yml:push:main -->";
        var body = await InvokeHarnessAsync<string>("buildIssueBody", new { marker, @ref = "main" });

        Assert.Contains(marker, body);
        // Self-closing on green, so the watchdog may also close it as a backstop.
        Assert.Contains("<!-- autoclose:true -->", body);
        Assert.Contains("on push to `main`", body);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task FormatCommentLinksTheRunAndCommit()
    {
        var comment = await InvokeHarnessAsync<string>(
            "formatComment",
            new { run = new { runNumber = 42, runUrl = "https://github.com/microsoft/aspire/actions/runs/42", sha = "abcdef0123456789" } });

        Assert.Contains("[run #42](https://github.com/microsoft/aspire/actions/runs/42)", comment);
        Assert.Contains("`abcdef01`", comment);
    }

    private async Task<T> InvokeHarnessAsync<T>(string operation, object payload)
    {
        var requestPath = Path.Combine(_workspace.Path, $"{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(new { operation, payload }, s_jsonOptions));

        using var command = new NodeCommand(_output, "report-ci-failure");
        command.WithWorkingDirectory(_repoRoot);

        var result = await command.ExecuteScriptAsync(_harnessPath, requestPath);
        Assert.Equal(0, result.ExitCode);

        var response = JsonSerializer.Deserialize<HarnessResponse<T>>(result.Output, s_jsonOptions);
        Assert.NotNull(response);
        return response!.Result;
    }

    private sealed record HarnessResponse<T>(T Result);
}