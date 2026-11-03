// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.TestUtilities;
using Xunit;

namespace Infrastructure.Tests;

public sealed class FilterNonBlockingTestArtifactsTests(ITestOutputHelper output)
{
    private static readonly string s_scriptPath = Path.Combine(
        RepoRoot.Path,
        ".github",
        "workflows",
        "filter-nonblocking-test-artifacts.sh");

    [Fact]
    [RequiresTools(["bash"])]
    public async Task RemovesMarkedArtifactAndPreservesGatingArtifact()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var allLogsPath = Path.Combine(workspace.Path, "all-logs");
        var advisoryResultsPath = Path.Combine(allLogsPath, "logs-advisory", "testresults");
        var gatingResultsPath = Path.Combine(allLogsPath, "logs-gating", "testresults");
        Directory.CreateDirectory(advisoryResultsPath);
        Directory.CreateDirectory(gatingResultsPath);
        File.WriteAllText(Path.Combine(advisoryResultsPath, "ignore-test-failures.marker"), "Dashboard Playwright");
        File.WriteAllText(Path.Combine(advisoryResultsPath, "advisory.trx"), "advisory failure");
        File.WriteAllText(Path.Combine(gatingResultsPath, "gating.trx"), "gating failure");

        var result = await ProcessRunner.RunAsync(output, "bash", [s_scriptPath, allLogsPath], RepoRoot.Path);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(
            ["logs-gating"],
            Directory.EnumerateDirectories(allLogsPath)
                .Select(Path.GetFileName)
                .Order(StringComparer.Ordinal)
                .ToArray());
        Assert.Equal(
            ["gating.trx"],
            Directory.EnumerateFiles(gatingResultsPath)
                .Select(Path.GetFileName)
                .Order(StringComparer.Ordinal)
                .ToArray());
        Assert.Contains("Excluding non-gating test results from failure classification", result.StandardOutput);
    }

    [Fact]
    [RequiresTools(["bash"])]
    public async Task RejectsMarkerOutsideArtifactDirectory()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var allLogsPath = Path.Combine(workspace.Path, "all-logs");
        var advisoryResultsPath = Path.Combine(allLogsPath, "logs-advisory", "testresults");
        var unexpectedResultsPath = Path.Combine(allLogsPath, "unexpected", "testresults");
        Directory.CreateDirectory(advisoryResultsPath);
        Directory.CreateDirectory(unexpectedResultsPath);
        File.WriteAllText(Path.Combine(advisoryResultsPath, "ignore-test-failures.marker"), "Dashboard Playwright");
        var markerPath = Path.Combine(unexpectedResultsPath, "ignore-test-failures.marker");
        File.WriteAllText(markerPath, "unexpected");

        var result = await ProcessRunner.RunAsync(output, "bash", [s_scriptPath, allLogsPath], RepoRoot.Path);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal($"Unexpected ignore marker location: {markerPath}\n", result.StandardError);
        Assert.Equal(
            ["logs-advisory", "unexpected"],
            Directory.EnumerateDirectories(allLogsPath)
                .Select(Path.GetFileName)
                .Order(StringComparer.Ordinal)
                .ToArray());
    }

    [Fact]
    [RequiresTools(["bash"])]
    public async Task RejectsMarkerOutsideTestResultsDirectory()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var allLogsPath = Path.Combine(workspace.Path, "all-logs");
        var advisoryResultsPath = Path.Combine(allLogsPath, "logs-advisory", "testresults");
        var misplacedResultsPath = Path.Combine(allLogsPath, "logs-gating", "not-testresults");
        Directory.CreateDirectory(advisoryResultsPath);
        Directory.CreateDirectory(misplacedResultsPath);
        File.WriteAllText(Path.Combine(advisoryResultsPath, "ignore-test-failures.marker"), "Dashboard Playwright");
        var markerPath = Path.Combine(misplacedResultsPath, "ignore-test-failures.marker");
        File.WriteAllText(markerPath, "unexpected");

        var result = await ProcessRunner.RunAsync(output, "bash", [s_scriptPath, allLogsPath], RepoRoot.Path);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal($"Unexpected ignore marker location: {markerPath}\n", result.StandardError);
        Assert.Equal(
            ["logs-advisory", "logs-gating"],
            Directory.EnumerateDirectories(allLogsPath)
                .Select(Path.GetFileName)
                .Order(StringComparer.Ordinal)
                .ToArray());
    }

    [Fact]
    [RequiresTools(["bash"])]
    public async Task RejectsMarkerInNestedArtifactDirectory()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var allLogsPath = Path.Combine(workspace.Path, "all-logs");
        var advisoryResultsPath = Path.Combine(allLogsPath, "logs-advisory", "testresults");
        var nestedResultsPath = Path.Combine(allLogsPath, "logs-gating", "nested", "testresults");
        Directory.CreateDirectory(advisoryResultsPath);
        Directory.CreateDirectory(nestedResultsPath);
        File.WriteAllText(Path.Combine(advisoryResultsPath, "ignore-test-failures.marker"), "Dashboard Playwright");
        var markerPath = Path.Combine(nestedResultsPath, "ignore-test-failures.marker");
        File.WriteAllText(markerPath, "unexpected");

        var result = await ProcessRunner.RunAsync(output, "bash", [s_scriptPath, allLogsPath], RepoRoot.Path);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal($"Unexpected ignore marker location: {markerPath}\n", result.StandardError);
        Assert.Equal(
            ["logs-advisory", "logs-gating"],
            Directory.EnumerateDirectories(allLogsPath)
                .Select(Path.GetFileName)
                .Order(StringComparer.Ordinal)
                .ToArray());
    }
}
