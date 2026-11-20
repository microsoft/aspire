// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AnalyzeCiFailure;
using Aspire.TestUtilities;
using VerifyXunit;
using Xunit;

namespace Infrastructure.Tests;

/// <summary>Checks live progress visibility, safe diagnostics and separation of stdout path outputs.</summary>
public sealed partial class AnalyzeCiFailureWorkflowTests
{
    [Fact]
    public async Task ProgressIsReadableBeforeDiagnosticFinalizationAndDoesNotDumpPayloads()
    {
        using var invocation = new AnalysisInvocation();
        _invocationDirectories.Add(invocation.RootDirectory);
        var path = Path.Combine(invocation.DiagnosticsDirectory, "progress.log");

        Assert.True(File.Exists(path));
        invocation.Progress.Diagnostics.StartCurrentStage();
        invocation.Progress.Stage = "jobs";
        invocation.Progress.Diagnostics.Record("job-log", "partial", new { job_id = "77", exit_code = 23, payload = "private-payload" });
        invocation.Progress.RecordProblem(AnalysisProblemCategory.Fatal, "jobs", "token=private-value;\n::warning::injected");
        var text = await File.ReadAllTextAsync(path);
        text = Regex.Replace(text, @"(?m)^\[[^\]]+\] ", "[time] ");
        text = Regex.Replace(text, @"duration_ms=\d+", "duration_ms=<elapsed>");

        Assert.False(File.Exists(Path.Combine(invocation.DiagnosticsDirectory, "summary.json")));
        await Verifier.Verify(text, "log").UseDirectory("Snapshots");
    }

    [Fact]
    [RequiresTools(["bash", "git"])]
    public async Task CollectionStreamsProgressToStderrWithoutChangingStdoutPathProtocol()
    {
        var result = await RunCollectionAsync();

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("[identity] started", _lastStandardError, StringComparison.Ordinal);
        Assert.Contains("[jobs] started", _lastStandardError, StringComparison.Ordinal);
        Assert.Collection(
            _lastStandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries),
            line => Assert.StartsWith("workspace_dir=", line),
            line => Assert.StartsWith("evidence_dir=", line),
            line => Assert.StartsWith("replay_dir=", line),
            line => Assert.StartsWith("recovery_dir=", line),
            line => Assert.StartsWith("diagnostics_dir=", line));
        Assert.Equal(
            _lastStandardError,
            await File.ReadAllTextAsync(Path.Combine(Path.GetDirectoryName(DiagnosticSummary)!, "progress.log")));
    }

    [Fact]
    public async Task ProgressFileFailureIsReportedWithoutReplacingOriginalFailure()
    {
        using var invocation = new AnalysisInvocation();
        _invocationDirectories.Add(invocation.RootDirectory);
        var path = Path.Combine(invocation.DiagnosticsDirectory, "progress.log");
        Assert.True(File.Exists(path));
        File.Delete(path);
        Directory.CreateDirectory(path);

        invocation.Progress.Stage = "jobs";
        invocation.Capture.Failure = new ValidationException("original acquisition error");
        invocation.Progress.RecordProblem(AnalysisProblemCategory.Fatal, "jobs", invocation.Capture.Failure.Message);
        await invocation.WriteDiagnosticsAsync("failed");

        var summary = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(invocation.DiagnosticsDirectory, "summary.json")))!;
        Assert.Equal("original acquisition error", summary["exception"]!["message"]!.GetValue<string>());
        Assert.Contains(
            summary["events"]!.AsArray(),
            item => item!["action"]!.GetValue<string>() == "progress-log" && item["result"]!.GetValue<string>() == "disabled");
    }

    [Fact]
    public async Task ProgressFileIsBoundedAndReportsTruncation()
    {
        using var invocation = new AnalysisInvocation();
        _invocationDirectories.Add(invocation.RootDirectory);
        var message = new string('x', count: 1000);
        for (var i = 0; i < 3 * AnalysisDiagnostics.MaximumManifestEntries; i++)
        {
            invocation.Progress.Diagnostics.WriteProgress(message);
        }

        await invocation.WriteDiagnosticsAsync("ready");

        var path = Path.Combine(invocation.DiagnosticsDirectory, "progress.log");
        Assert.True(new FileInfo(path).Length <= AnalysisDiagnostics.MaximumFileBytes);
        Assert.EndsWith(
            $"Progress log file reached its size limit; progress continues on stderr.{Environment.NewLine}",
            await File.ReadAllTextAsync(path));
        var summary = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(invocation.DiagnosticsDirectory, "summary.json")))!;
        Assert.Contains(
            summary["events"]!.AsArray(),
            item => item!["action"]!.GetValue<string>() == "progress-log" && item["result"]!.GetValue<string>() == "truncated");
    }

    [Fact]
    [RequiresTools(["bash", "git"])]
    public async Task JobListingFailureNamesOperationAndDoesNotReachTestParsing()
    {
        var result = await RunCollectionAsync("job-list-api-failure");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("::error::Listing workflow jobs failed: GET /repos/microsoft/aspire/actions/runs/123/attempts/2/jobs: HTTP 502.", _lastStandardError, StringComparison.Ordinal);
        var summary = JsonNode.Parse(await File.ReadAllTextAsync(DiagnosticSummary))!;
        Assert.Equal(
            ["identity", "jobs"],
            summary["events"]!.AsArray()
                .Where(item => item!["action"]!.GetValue<string>() == "stage")
                .Select(item => item!["stage"]!.GetValue<string>()));
        Assert.Null(summary["outcome"]!["has_work"]);
        var text = await File.ReadAllTextAsync(Path.Combine(Path.GetDirectoryName(DiagnosticSummary)!, "progress.log"));
        Assert.Contains("[jobs] started: Listing workflow jobs", text, StringComparison.Ordinal);
        Assert.Contains("[jobs] failed: Listing workflow jobs", text, StringComparison.Ordinal);
    }

    [Fact]
    [RequiresTools(["bash", "git"])]
    public async Task MalformedTestResultsAreIdentifiedAsParsingProblems()
    {
        var result = await RunCollectionAsync("malformed-trx");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("[test-evidence] started: Collecting and parsing test results", _lastStandardError, StringComparison.Ordinal);
        Assert.Contains("stage=parsing: Cannot parse test result", _lastStandardError, StringComparison.Ordinal);
        var summary = JsonNode.Parse(await File.ReadAllTextAsync(DiagnosticSummary))!;
        Assert.Equal("degraded", summary["status"]!.GetValue<string>());
    }
}
