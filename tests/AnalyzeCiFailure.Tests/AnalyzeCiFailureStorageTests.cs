// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using AnalyzeCiFailure;
using Xunit;

namespace Infrastructure.Tests;

/// <summary>Checks retained captures, immutable inputs and scratch-only cleanup after failures.</summary>
public sealed partial class AnalyzeCiFailureWorkflowTests
{
    [Fact]
    public async Task CollectorCannotBeInvokedAgainAfterCancellation()
    {
        using var scratch = new AnalysisScratchWorkspace();
        var collector = new WorkflowRunEvidenceCollector(
            _workspace.Path,
            "microsoft/aspire",
            scratch,
            new InvocationCapture(),
            new AspireCiWorkflowConventions(),
            commandEnvironment: null,
            CreateGitHubClient());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var firstProgress = new AnalysisProgress();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            collector.CollectAsync(new("microsoft/aspire", RunId: 123, Attempt: 1), firstProgress, cancellation.Token));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            collector.CollectAsync(new("microsoft/aspire", RunId: 456, Attempt: 1), new AnalysisProgress(), cancellation.Token));

        Assert.Null(firstProgress.RunId);
        Assert.Empty(Directory.GetFileSystemEntries(_workspace.Path));
    }

    [Fact]
    public async Task FailedCollectionRetainsPartialCapturesOutsidePublicationEvidence()
    {
        var result = await RunCollectionAsync("history-transport");

        Assert.Equal(1, result.ExitCode);
        var recovery = RecoveryDirectory("collection");
        var capturedRun = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(recovery, "diagnostics", "run.json")));
        Assert.Equal(123, capturedRun.RootElement.GetProperty("id").GetInt64());
        var outcome = TransientJson.Read<AnalysisOutcome>(await File.ReadAllTextAsync(Path.Combine(recovery, "collection-outcome.json")));
        Assert.Equal("failed", outcome.Status);
        Assert.Equal("history", Assert.Single(outcome.Problems).Stage);
        Assert.False(File.Exists(Path.Combine(EvidenceDirectory, "evidence.json")));
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(recovery, "manifest.json")));
        Assert.Equal("AnalyzeCiFailure.ValidationException", manifest.RootElement.GetProperty("exception").GetProperty("type").GetString());
        Assert.Contains(
            "CopyPriorCauseHistoryAsync",
            manifest.RootElement.GetProperty("exception").GetProperty("stack_trace").GetString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectedEvidenceCannotReplaceTheOriginalFailureWithARecoverySerializationError()
    {
        var result = await RunCollectionAsync("nonpositive-step");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Evidence jobs must be a nonempty array", result.Output, StringComparison.Ordinal);
        var recovery = RecoveryDirectory("collection");
        using var rejected = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(recovery, "rejected-or-degraded-evidence.json")));
        Assert.Equal(0, rejected.RootElement.GetProperty("jobs")[0].GetProperty("steps")[0].GetProperty("number").GetInt32());
        Assert.True(File.Exists(Path.Combine(recovery, "manifest.json")));
        var outcome = TransientJson.Read<AnalysisOutcome>(await File.ReadAllTextAsync(new EvidencePaths(EvidenceDirectory).Outcome));
        Assert.Equal("failed", outcome.Status);
        Assert.False(File.Exists(new EvidencePaths(EvidenceDirectory).Bundle));
    }

    [Fact]
    public async Task RecoveryManifestBoundsItsInventory()
    {
        using var scratch = new AnalysisScratchWorkspace();
        Directory.CreateDirectory(scratch.Results);
        for (var i = 0; i < 1025; i++)
        {
            await File.WriteAllTextAsync(Path.Combine(scratch.Results, $"{i:D5}.trx"), "malformed");
        }
        var directory = RecoveryDirectory("collection");

        await RecoveryCaptureWriter.WriteAsync(directory, new AnalysisProgress { Stage = "parsing" }, "failed", new InvocationCapture(), scratch);

        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "manifest.json")));
        Assert.Equal(1024, manifest.RootElement.GetProperty("files").GetArrayLength());
        Assert.True(manifest.RootElement.GetProperty("inventory_limited").GetBoolean());
        Assert.Equal(1024, Directory.GetFiles(Path.Combine(directory, "extracted-results")).Length);
    }

    [Fact]
    public async Task DegradedCollectionRetainsSanitizedReportForDiagnosis()
    {
        var result = await RunCollectionAsync("malformed-trx");

        Assert.Equal(0, result.ExitCode);
        var recovery = RecoveryDirectory("collection");
        var report = Assert.Single(Directory.GetFiles(Path.Combine(recovery, "extracted-results"), "*.trx", SearchOption.AllDirectories));
        Assert.Equal("malformed", await File.ReadAllTextAsync(report));
        var outcome = TransientJson.Read<AnalysisOutcome>(await File.ReadAllTextAsync(Path.Combine(recovery, "collection-outcome.json")));
        Assert.Equal("degraded", outcome.Status);
        Assert.True(File.Exists(Path.Combine(recovery, "diagnostics", "selected-test-result-artifacts.json")));
    }

    [Fact]
    public async Task OptionalAcquisitionFailureRetainsRecoveryWithoutBlockingUsableEvidence()
    {
        var result = await RunCollectionAsync("missing-pr-metadata");

        Assert.Equal(0, result.ExitCode);
        var recovery = RecoveryDirectory("collection");
        var outcome = TransientJson.Read<AnalysisOutcome>(await File.ReadAllTextAsync(Path.Combine(recovery, "collection-outcome.json")));
        Assert.Equal("ready", outcome.Status);
        Assert.Contains(outcome.Problems, problem => problem.Stage == "pr-metadata");
        Assert.True(File.Exists(Path.Combine(recovery, "diagnostics", "all-jobs.json")));
        Assert.True(File.Exists(new EvidencePaths(EvidenceDirectory).Bundle));
    }

    [Fact]
    public async Task RejectedRunIdentityRetainsReturnedMetadataOnlyAsRecovery()
    {
        var result = await RunCollectionAsync("wrong-run");

        Assert.Equal(1, result.ExitCode);
        var recovery = RecoveryDirectory("collection");
        using var run = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(recovery, "diagnostics", "run.json")));
        Assert.Equal(999, run.RootElement.GetProperty("id").GetInt64());
        Assert.False(File.Exists(new EvidencePaths(EvidenceDirectory).Bundle));
    }

    [Fact]
    public async Task RecoveryRedactsAndBoundsIntermediatesWithoutMutatingTheirInputs()
    {
        using var scratch = new AnalysisScratchWorkspace();
        Directory.CreateDirectory(scratch.Results);
        await File.WriteAllTextAsync(Path.Combine(scratch.Results, "failed.trx"), "<error>token=private-value</error>");
        await File.WriteAllTextAsync(Path.Combine(scratch.Results, "oversized.trx"), new string('x', AnalysisDiagnostics.MaximumFileBytes + 1));
        var capture = new InvocationCapture();
        capture.Documents.Add("diagnostics/run.json", System.Text.Json.Nodes.JsonNode.Parse("""{"message":"password=private-value"}"""));
        for (var i = 0; i < 10; i++)
        {
            capture.Attachments.Add($"diagnostics/job-{i}.log", CollectedAttachment.FromText(new string('x', AnalysisDiagnostics.MaximumFileBytes)));
        }
        var directory = RecoveryDirectory("collection");

        await RecoveryCaptureWriter.WriteAsync(directory, new AnalysisProgress { Stage = "parsing" }, "failed", capture, scratch);

        Assert.All(
            Directory.GetFiles(directory, "*", SearchOption.AllDirectories),
            file => Assert.DoesNotMatch("private-value", File.ReadAllText(file)));
        Assert.True(Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
            .Where(file => Path.GetFileName(file) is not ("manifest.json" or "collection-outcome.json"))
            .Sum(file => new FileInfo(file).Length) <= RecoveryCaptureWriter.MaximumTotalBytes);
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "manifest.json")));
        Assert.Contains(
            manifest.RootElement.GetProperty("files").EnumerateArray(),
            entry => entry.GetProperty("file").GetString() == "extracted-results/oversized.trx" && entry.GetProperty("state").GetString() == "omitted");
        Assert.Equal("<error>token=private-value</error>", await File.ReadAllTextAsync(Path.Combine(scratch.Results, "failed.trx")));
    }

    [Fact]
    public async Task ReplayUsesRelocatedDiagnosticsAndHistoryWithoutReadingNeighboringFiles()
    {
        var collected = await RunCollectionAsync("stored-history");
        Assert.True(collected.ExitCode == 0, collected.Output);
        var paths = new EvidencePaths(EvidenceDirectory);
        var summary = await File.ReadAllTextAsync(paths.Summary);
        await File.WriteAllTextAsync(Path.Combine(paths.RootDirectory, "unexpected.json"), "not JSON");

        var replay = await RunValidatorCommandAsync(["replay", "--working-dir", _workspace.Path]);

        Assert.True(replay.ExitCode == 0, replay.Output);
        var output = new EvidencePaths(ReplayDirectory);
        Assert.Equal(summary, await File.ReadAllTextAsync(output.Summary));
        using var index = JsonDocument.Parse(await File.ReadAllTextAsync(output.PriorCauseIndex));
        Assert.All(
            index.RootElement.EnumerateArray(),
            entry => Assert.True(File.Exists(Path.Combine(output.RootDirectory, entry.GetProperty("file").GetString()!))));
        Assert.False(File.Exists(Path.Combine(output.RootDirectory, "unexpected.json")));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [SkipOnPlatform(TestPlatforms.Windows, "Creating directory symlinks requires elevated privileges on Windows.")]
    public async Task PreviousOutputSymlinkNeverChangesItsTarget(bool collection, bool recovery)
    {
        var paths = new EvidencePaths(EvidenceDirectory);
        var target = collection ? Path.Combine(_workspace.Path, "saved-evidence") : paths.RootDirectory;
        Directory.CreateDirectory(Path.Combine(target, "history"));
        await File.WriteAllTextAsync(
            Path.Combine(target, "evidence.json"),
            TransientJson.Write(AnalyzeCiFailureFixtures.CreateEvidence(RunScope.PullRequest)));
        await File.WriteAllTextAsync(Path.Combine(target, "analysis-summary.md"), "original summary");
        await File.WriteAllTextAsync(Path.Combine(target, "collection-outcome.json"), "original outcome");
        await File.WriteAllTextAsync(Path.Combine(target, "history", "cause-index.json"), "original catalogue");
        var before = Directory.GetFiles(target, "*", SearchOption.AllDirectories)
            .ToDictionary(file => Path.GetRelativePath(target, file), File.ReadAllText);
        var destination = recovery ? RecoveryDirectory("collection") :
            collection ? paths.RootDirectory : new EvidencePaths(ReplayDirectory).RootDirectory;
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        Directory.CreateSymbolicLink(destination, target);

        if (collection)
        {
            using var invocation = new AnalysisInvocation();
            _invocationDirectories.Add(invocation.RootDirectory);
            await Assert.ThrowsAsync<ValidationException>(() => AnalyzeCiFailure.EvidencePreparation.CollectEvidenceAndPrepareAnalysisAsync(
                _workspace.Path,
                "microsoft/aspire",
                invocation,
                "invalid",
                "1",
                CreateGitHubClient(),
                CancellationToken.None));
        }
        else
        {
            var result = await RunValidatorCommandAsync(["replay", "--working-dir", _workspace.Path]);
            Assert.True(result.ExitCode == 0, result.Output);
        }

        Assert.All(before, pair => Assert.Equal(pair.Value, File.ReadAllText(Path.Combine(target, pair.Key))));
        Assert.Equal(
            before.Keys.Order(),
            Directory.GetFiles(target, "*", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(target, file)).Order());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReplayNeverModifiesCapturedInputs(bool invalid)
    {
        var paths = new EvidencePaths(EvidenceDirectory);
        Directory.CreateDirectory(paths.RootDirectory);
        var text = invalid ? "{}" : TransientJson.Write(AnalyzeCiFailureFixtures.CreateEvidence(RunScope.PullRequest));
        await File.WriteAllTextAsync(paths.Bundle, text);
        await File.WriteAllTextAsync(paths.Summary, "original summary");
        var before = Directory.GetFiles(paths.RootDirectory).ToDictionary(path => Path.GetFileName(path), File.ReadAllText);

        var result = await RunValidatorCommandAsync(["replay", "--working-dir", _workspace.Path]);

        Assert.Equal(invalid ? 1 : 0, result.ExitCode);
        Assert.All(before, pair => Assert.Equal(pair.Value, File.ReadAllText(Path.Combine(paths.RootDirectory, pair.Key!))));
        Assert.Equal(before.Keys.Order(), Directory.GetFiles(paths.RootDirectory).Select(Path.GetFileName).Order());
        Assert.True(File.Exists(Path.Combine(ReplayDirectory, "collection-outcome.json")));
    }
}
