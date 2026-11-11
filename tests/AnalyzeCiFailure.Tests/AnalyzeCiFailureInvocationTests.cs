// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using AnalyzeCiFailure;
using Aspire.TestUtilities;
using Xunit;

namespace Infrastructure.Tests;

/// <summary>Checks operation input boundaries, independent output lifetimes and offline replay behavior.</summary>
public sealed partial class AnalyzeCiFailureWorkflowTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [SkipOnPlatform(TestPlatforms.Windows, "Creating directory symlinks requires elevated privileges on Windows.")]
    public async Task HistoryCheckoutValidationResolvesInputAndDestinationAliases(bool historyThroughLink)
    {
        var evidence = Directory.CreateDirectory(Path.Combine(_workspace.Path, "saved-evidence")).FullName;
        var proposal = Directory.CreateDirectory(Path.Combine(_workspace.Path, "saved-proposal")).FullName;
        var alias = Path.Combine(_workspace.Path, "evidence-alias");
        Directory.CreateSymbolicLink(alias, evidence);
        var input = historyThroughLink ? evidence : alias;
        var checkout = Path.Combine(historyThroughLink ? alias : evidence, "history-checkout");

        var result = await RunValidatorCommandAsync(
            ["publish", "--repo", "microsoft/aspire", "--evidence-dir", input,
                "--analysis-dir", proposal, "--history-dir", checkout]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("--history-dir must be outside", result.Output, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFileSystemEntries(evidence));
        Assert.Empty(Directory.GetFileSystemEntries(proposal));
    }

    [Theory]
    [InlineData("valid", 0, false)]
    [InlineData("missing-artifact", 0, true)]
    [InlineData("history-transport", 1, true)]
    [RequiresTools(["bash", "git"])]
    public async Task WorkflowArtifactInputsSurviveScratchCleanup(
        string scenario,
        int expectedExitCode,
        bool recoveryExpected)
    {
        var result = await RunCollectionAsync(scenario, useWorkflowAdapter: true);

        Assert.Equal(expectedExitCode, result.ExitCode);
        var outputs = await File.ReadAllTextAsync(Path.Combine(_workspace.Path, "workflow-output"));
        var evidence = ReadNamedPath(outputs, "evidence_dir");
        var recovery = ReadNamedPath(outputs, "recovery_dir");
        var diagnostics = ReadNamedPath(outputs, "diagnostics_dir");
        var workspace = ReadNamedPath(outputs, "workspace_dir");
        Assert.Equal(EvidenceDirectory, evidence);
        Assert.True(File.Exists(Path.Combine(evidence, "collection-outcome.json")));
        Assert.True(File.Exists(Path.Combine(diagnostics, "summary.json")));
        Assert.False(Directory.Exists(Path.Combine(workspace, "scratch")));
        Assert.False(Directory.Exists(Path.Combine(_workspace.Path, "ci-failure-data")));
        if (expectedExitCode == 0)
        {
            Assert.True(File.Exists(Path.Combine(evidence, "evidence.json")));
            Assert.True(File.Exists(Path.Combine(evidence, "analysis-summary.md")));
            Assert.True(File.Exists(Path.Combine(evidence, "diagnostics", "job-77.log")));
        }
        Assert.Equal(recoveryExpected, File.Exists(Path.Combine(recovery, "manifest.json")));
        if (recoveryExpected)
        {
            Assert.True(File.Exists(Path.Combine(recovery, "diagnostics", "job-77.log")));
        }
    }

    [Fact]
    [RequiresTools(["bash", "git"])]
    public async Task GeneratedDiagnosticJsonUsesNativeLineEnding()
    {
        var result = await RunCollectionAsync();

        Assert.Equal(0, result.ExitCode);
        var json = await File.ReadAllTextAsync(Path.Combine(EvidenceDirectory, "diagnostics", "run.json"));
        Assert.EndsWith(Environment.NewLine, json);
        using var document = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal(123, document.RootElement.GetProperty("id").GetInt64());
    }

    [Theory]
    [InlineData("collect", "--run-id", "--evidence-dir")]
    [InlineData("replay", "--evidence-dir", "--run-id")]
    public async Task CollectionAndReplayExposeDistinctInputContracts(string command, string supported, string unsupported)
    {
        var help = await RunValidatorCommandAsync([command, "--help"]);

        Assert.Equal(0, help.ExitCode);
        Assert.Contains(supported, help.Output, StringComparison.Ordinal);
        var invalid = await RunValidatorCommandAsync([command, unsupported, "input"]);
        Assert.Equal(2, invalid.ExitCode);
        Assert.Contains($"Unrecognized command or argument '{unsupported}'", invalid.Output, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFileSystemEntries(_workspace.Path));
    }

    [Theory]
    [InlineData("--repo", "collect", "--run-id", "123")]
    [InlineData("--run-id", "collect", "--repo", "microsoft/aspire")]
    [InlineData("--evidence-dir", "replay")]
    public async Task OperationSpecificInputsAreRequiredBeforeAcquisition(string missing, params string[] arguments)
    {
        var result = await RunProcessAsync(
            Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
            ["exec", "--runtimeconfig", Path.ChangeExtension(typeof(AnalyzeCiFailureWorkflowTests).Assembly.Location, ".runtimeconfig.json"),
                typeof(AnalyzeCiFailure.Program).Assembly.Location, .. arguments]);

        Assert.Equal(2, result.ExitCode);
        Assert.Contains($"Option '{missing}' is required.", result.Output, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFileSystemEntries(_workspace.Path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [RequiresTools(["bash", "git"])]
    public async Task UnavailableTestResultsAllowCollectionAndReplayButNotValidation(bool useWorkflowAdapter)
    {
        var collection = await RunCollectionAsync("missing-artifact", useWorkflowAdapter: useWorkflowAdapter);

        Assert.Equal(0, collection.ExitCode);
        var input = new EvidencePaths(EvidenceDirectory);
        var original = await File.ReadAllTextAsync(input.Bundle);
        var evidence = TransientJson.Read<EvidenceBundle>(original);
        Assert.Equal(EvidenceState.Unavailable, evidence.TestEvidence.State);
        Assert.Empty(evidence.TestEvidence.Failures);
        Assert.NotEmpty(evidence.TestEvidence.Problems);

        var replay = await RunValidatorCommandAsync(["replay", "--evidence-dir", input.RootDirectory]);

        Assert.Equal(0, replay.ExitCode);
        var output = new EvidencePaths(ReadNamedPath(replay.Output, "replay_dir"));
        var replayed = TransientJson.Read<EvidenceBundle>(await File.ReadAllTextAsync(output.Bundle));
        Assert.Equal(EvidenceState.Unavailable, replayed.TestEvidence.State);
        Assert.Equal(original, await File.ReadAllTextAsync(input.Bundle));
        Assert.Equal("degraded", TransientJson.Read<AnalysisOutcome>(await File.ReadAllTextAsync(output.Outcome)).Status);
        var error = Assert.Throws<ValidationException>(() =>
            AnalysisValidator.Validate(replayed, AnalyzeCiFailureFixtures.CreateInfrastructureProposal(replayed), "microsoft/aspire"));
        Assert.Contains("publication and reruns are not authorized", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RepeatedFailedInvocationsRetainIndependentOutputsWithoutResettingPreviousFiles()
    {
        var first = await RunValidatorCommandAsync(["collect", "--repo", "microsoft/aspire", "--run-id", "invalid"]);
        Assert.Equal(1, first.ExitCode);
        var firstRoot = ReadNamedPath(first.Output, "workspace_dir");
        var firstOutcome = Path.Combine(firstRoot, "evidence", "collection-outcome.json");
        var bytes = await File.ReadAllBytesAsync(firstOutcome);
        await File.WriteAllTextAsync(Path.Combine(firstRoot, "operator-note.txt"), "keep");

        var second = await RunValidatorCommandAsync(["collect", "--repo", "microsoft/aspire", "--run-id", "invalid"]);

        Assert.Equal(1, second.ExitCode);
        var secondRoot = ReadNamedPath(second.Output, "workspace_dir");
        Assert.NotEqual(firstRoot, secondRoot);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(firstOutcome));
        Assert.Equal("keep", await File.ReadAllTextAsync(Path.Combine(firstRoot, "operator-note.txt")));
        Assert.True(File.Exists(Path.Combine(secondRoot, "recovery", "collection", "manifest.json")));
        Assert.False(Directory.Exists(Path.Combine(firstRoot, "scratch")));
        Assert.False(Directory.Exists(Path.Combine(secondRoot, "scratch")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [SkipOnPlatform(TestPlatforms.Windows, "Creating directory symlinks requires elevated privileges on Windows.")]
    public async Task ReplayUsesFreshOutputEvenWhenInputIsLinkedToAnEarlierReplay(bool invalid)
    {
        var saved = Directory.CreateDirectory(Path.Combine(_workspace.Path, "earlier-replay")).FullName;
        var original = invalid ? "{}" : TransientJson.Write(AnalyzeCiFailureFixtures.CreateEvidence(RunScope.PullRequest));
        await File.WriteAllTextAsync(Path.Combine(saved, "evidence.json"), original);
        var linkedInput = Path.Combine(_workspace.Path, "linked-evidence");
        Directory.CreateSymbolicLink(linkedInput, saved);
        var result = await RunValidatorCommandAsync(["replay", "--evidence-dir", linkedInput]);

        Assert.True(result.ExitCode == (invalid ? 1 : 0), result.Output);
        var output = ReadNamedPath(result.Output, "replay_dir");
        Assert.NotEqual(saved, output);
        Assert.Equal(original, await File.ReadAllTextAsync(Path.Combine(saved, "evidence.json")));
        Assert.Equal(!invalid, File.Exists(Path.Combine(output, "analysis-summary.md")));
    }

    [Fact]
    public async Task InvocationDisposesOnlyScratchAndRetainsPublishedOutputs()
    {
        var invocation = new AnalysisInvocation();
        _invocationDirectories.Add(invocation.RootDirectory);
        Directory.CreateDirectory(invocation.Evidence.RootDirectory);
        Directory.CreateDirectory(invocation.Scratch.Requests);
        await File.WriteAllTextAsync(invocation.Evidence.Summary, "retain");
        await File.WriteAllTextAsync(Path.Combine(invocation.Scratch.Requests, "request.json"), "discard");

        invocation.Dispose();

        Assert.False(Directory.Exists(invocation.Scratch.RootDirectory));
        Assert.Equal("retain", await File.ReadAllTextAsync(invocation.Evidence.Summary));
    }

    [Fact]
    public async Task WorkflowOutputCancellationRetainsSameInvocationsScratchDiagnostics()
    {
        using var invocation = new AnalysisInvocation();
        _invocationDirectories.Add(invocation.RootDirectory);
        var output = Path.Combine(_workspace.Path, "workflow-output");
        await invocation.WritePathsAsync("collection", output);
        Directory.CreateDirectory(invocation.Scratch.Results);
        await File.WriteAllTextAsync(Path.Combine(invocation.Scratch.Results, "failed.trx"), "current report");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var exitCode = await EvidencePreparation.CompleteCollectionAsync(
            invocation,
            invocation.Progress.CreateOutcome("ready"),
            output,
            cancellation.Token);

        Assert.Equal(1, exitCode);
        var report = Path.Combine(invocation.RecoveryDirectory("collection"), "extracted-results", "failed.trx");
        Assert.Equal("current report", await File.ReadAllTextAsync(report));
        var lines = await File.ReadAllLinesAsync(output);
        Assert.Contains("recovery_dir=" + invocation.RecoveryDirectory("collection"), lines);
        Assert.All(lines, line => Assert.False(line.StartsWith("has_work=", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("evidence")]
    [InlineData("proposal")]
    public async Task PublicationRejectsHistoryCheckoutInsideItsInputs(string input)
    {
        var evidence = Directory.CreateDirectory(Path.Combine(_workspace.Path, "input-evidence")).FullName;
        var proposal = Directory.CreateDirectory(Path.Combine(_workspace.Path, "input-proposal")).FullName;

        var result = await RunValidatorCommandAsync(["publish", "--repo", "microsoft/aspire",
            "--evidence-dir", evidence, "--analysis-dir", proposal,
            "--history-dir", Path.Combine(input == "evidence" ? evidence : proposal, "history-checkout")]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("--history-dir must be outside", result.Output, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFileSystemEntries(evidence));
        Assert.Empty(Directory.GetFileSystemEntries(proposal));
    }

    private static string ReadNamedPath(string output, string name)
        => Assert.Single(output.Split('\n'), line => line.StartsWith(name + "=", StringComparison.Ordinal))[(name.Length + 1)..].TrimEnd('\r');
}
