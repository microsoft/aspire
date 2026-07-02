// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace Infrastructure.Tests;

/// <summary>
/// End-to-end tests for the GenerateTestSummary tool.
/// </summary>
public sealed class GenerateTestSummaryToolTests : IClassFixture<GenerateTestSummaryFixture>, IDisposable
{
    private readonly TestTempDirectory _tempDirectory = new();
    private readonly GenerateTestSummaryFixture _fixture;
    private readonly ITestOutputHelper _output;

    public GenerateTestSummaryToolTests(GenerateTestSummaryFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    public void Dispose() => _tempDirectory.Dispose();

    [Fact]
    public async Task IncludesStructuredErrorMessageAndStackTraceInReport()
    {
        var trxPath = Path.Combine(_tempDirectory.Path, "sample.trx");
        TestTrxBuilder.CreateTrxFile(
            trxPath,
            new TestTrxCase(
                CanonicalTestName: "Tests.Namespace.Type.Method",
                DisplayName: "Tests.Namespace.Type.Method",
                Outcome: "Failed",
                ErrorMessage: "Expected 1 but found 2.",
                StackTrace: "at Tests.Namespace.Type.Method() in TestFile.cs:line 42",
                StdOut: "stdout line 1"));

        var result = await RunToolAsync(trxPath);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Expected 1 but found 2.", result.Output, StringComparison.Ordinal);
        Assert.Contains("at Tests.Namespace.Type.Method() in TestFile.cs:line 42", result.Output, StringComparison.Ordinal);
        Assert.Contains("### StdOut", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailedTestsJsonListsOnlyFailedTestNames()
    {
        var trxPath = Path.Combine(_tempDirectory.Path, "mixed.trx");
        TestTrxBuilder.CreateTrxFile(
            trxPath,
            new TestTrxCase("Tests.Type.PassingMethod", "Tests.Type.PassingMethod", "Passed"),
            new TestTrxCase("Tests.Type.FailingMethod", "Tests.Type.FailingMethod", "Failed", ErrorMessage: "boom"),
            new TestTrxCase("Tests.Type.ErroredMethod", "Tests.Type.ErroredMethod", "Error", ErrorMessage: "kaboom"));

        var jsonPath = Path.Combine(_tempDirectory.Path, "failed.json");
        var result = await RunToolAsync(trxPath, "--failed-tests-json", jsonPath);

        Assert.Equal(0, result.ExitCode);
        var payload = JsonSerializer.Deserialize<FailedTestsPayload>(
            await File.ReadAllTextAsync(jsonPath),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.NotNull(payload);
        Assert.Equal(2, payload!.Count);
        Assert.Contains("Tests.Type.FailingMethod", payload.FailedTests);
        Assert.Contains("Tests.Type.ErroredMethod", payload.FailedTests);
        Assert.DoesNotContain("Tests.Type.PassingMethod", payload.FailedTests);
    }

    [Fact]
    public async Task FailedTestsJsonTreatsAbortedAsFailure()
    {
        // Aborted is a failed outcome (matches CreateFailingTestIssue). Without it,
        // a red run whose only failures aborted would report zero failures and be
        // misfiled as infra, dropping the failing-test signal.
        var trxPath = Path.Combine(_tempDirectory.Path, "aborted.trx");
        TestTrxBuilder.CreateTrxFile(
            trxPath,
            new TestTrxCase("Tests.Type.AbortedMethod", "Tests.Type.AbortedMethod", "Aborted", ErrorMessage: "aborted"),
            new TestTrxCase("Tests.Type.PassingMethod", "Tests.Type.PassingMethod", "Passed"));

        var jsonPath = Path.Combine(_tempDirectory.Path, "failed.json");
        var result = await RunToolAsync(trxPath, "--failed-tests-json", jsonPath);

        Assert.Equal(0, result.ExitCode);
        var payload = JsonSerializer.Deserialize<FailedTestsPayload>(
            await File.ReadAllTextAsync(jsonPath),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.NotNull(payload);
        Assert.Equal(1, payload!.Count);
        Assert.Contains("Tests.Type.AbortedMethod", payload.FailedTests);
        Assert.DoesNotContain("Tests.Type.PassingMethod", payload.FailedTests);
    }

    [Fact]
    public async Task FailedTestsJsonIsEmptyWhenAllPass()
    {
        var trxPath = Path.Combine(_tempDirectory.Path, "allpass.trx");
        TestTrxBuilder.CreateTrxFile(
            trxPath,
            new TestTrxCase("Tests.Type.A", "Tests.Type.A", "Passed"),
            new TestTrxCase("Tests.Type.B", "Tests.Type.B", "Passed"));

        var jsonPath = Path.Combine(_tempDirectory.Path, "failed.json");
        var result = await RunToolAsync(trxPath, "--failed-tests-json", jsonPath);

        Assert.Equal(0, result.ExitCode);
        var payload = JsonSerializer.Deserialize<FailedTestsPayload>(
            await File.ReadAllTextAsync(jsonPath),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.NotNull(payload);
        Assert.Equal(0, payload!.Count);
        Assert.Empty(payload.FailedTests);
        // A clean read with zero failures is trustworthy infra, not lost results.
        Assert.False(payload.ExtractionFailed);
    }

    [Fact]
    public async Task FailedTestsJsonSetsExtractionFailedWhenAllTrxUnreadable()
    {
        // A genuinely-red run whose only .trx files are corrupt must not be
        // reported as zero failures: the reporter would file that as infra and
        // drop the failing-test signal. extractionFailed flags it as a test
        // failure instead. Falsifiable: without the flag the payload is
        // indistinguishable from a clean zero-failure run.
        var trxDir = Path.Combine(_tempDirectory.Path, "corrupt");
        Directory.CreateDirectory(trxDir);
        await File.WriteAllTextAsync(Path.Combine(trxDir, "broken.trx"), "this is not valid trx xml <<<");

        var jsonPath = Path.Combine(_tempDirectory.Path, "failed.json");
        var result = await RunToolAsync(trxDir, "--failed-tests-json", jsonPath);

        Assert.Equal(0, result.ExitCode);
        var payload = JsonSerializer.Deserialize<FailedTestsPayload>(
            await File.ReadAllTextAsync(jsonPath),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.NotNull(payload);
        Assert.Equal(0, payload!.Count);
        Assert.Empty(payload.FailedTests);
        Assert.True(payload.ExtractionFailed);
    }

    [Fact]
    public async Task FailedTestsJsonSetsExtractionFailedWhenSomeTrxUnreadableAndNoFailuresFound()
    {
        // Partial-corrupt: one .trx reads cleanly with only passing tests, another
        // cannot be read. A "zero failures" result is not trustworthy here — the
        // unreadable file may have held the failures — so extractionFailed is set
        // and the reporter files a test-failure issue rather than dropping the
        // signal as infra. Falsifiable: if the flag only considered the
        // all-unreadable case, this would report a clean zero and be misfiled.
        var trxDir = Path.Combine(_tempDirectory.Path, "partial");
        Directory.CreateDirectory(trxDir);
        TestTrxBuilder.CreateTrxFile(
            Path.Combine(trxDir, "clean.trx"),
            new TestTrxCase("Tests.Type.PassingMethod", "Tests.Type.PassingMethod", "Passed"));
        await File.WriteAllTextAsync(Path.Combine(trxDir, "broken.trx"), "this is not valid trx xml <<<");

        var jsonPath = Path.Combine(_tempDirectory.Path, "failed.json");
        var result = await RunToolAsync(trxDir, "--failed-tests-json", jsonPath);

        Assert.Equal(0, result.ExitCode);
        var payload = JsonSerializer.Deserialize<FailedTestsPayload>(
            await File.ReadAllTextAsync(jsonPath),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.NotNull(payload);
        Assert.Equal(0, payload!.Count);
        Assert.Empty(payload.FailedTests);
        Assert.True(payload.ExtractionFailed);
    }

    [Fact]
    public async Task FailedTestsJsonDoesNotSetExtractionFailedWhenFailuresFoundDespiteUnreadableTrx()
    {
        // A readable .trx with a real failure plus an unreadable .trx: the collected
        // failure is trustworthy, so extractionFailed stays false even though one
        // file could not be read. The failing test name is still reported.
        var trxDir = Path.Combine(_tempDirectory.Path, "partial-with-failure");
        Directory.CreateDirectory(trxDir);
        TestTrxBuilder.CreateTrxFile(
            Path.Combine(trxDir, "failed.trx"),
            new TestTrxCase("Tests.Type.FailingMethod", "Tests.Type.FailingMethod", "Failed", ErrorMessage: "boom"));
        await File.WriteAllTextAsync(Path.Combine(trxDir, "broken.trx"), "this is not valid trx xml <<<");

        var jsonPath = Path.Combine(_tempDirectory.Path, "failed.json");
        var result = await RunToolAsync(trxDir, "--failed-tests-json", jsonPath);

        Assert.Equal(0, result.ExitCode);
        var payload = JsonSerializer.Deserialize<FailedTestsPayload>(
            await File.ReadAllTextAsync(jsonPath),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.NotNull(payload);
        Assert.Equal(1, payload!.Count);
        Assert.Contains("Tests.Type.FailingMethod", payload.FailedTests);
        Assert.False(payload.ExtractionFailed);
    }

    [Fact]
    public async Task FailedTestsJsonHandlesRealTrxTimestamps()
    {
        // Regression: real MTP `--report-trx` files stamp startTime/endTime as
        // ISO-8601 DateTimeOffset. The previous implementation read TRX via
        // TrxReader.GetTestResultsFromTrx, which TimeSpan.Parse-es those attributes
        // and throws FormatException — silently skipping every real .trx and
        // reporting zero failures.
        var trxPath = Path.Combine(_tempDirectory.Path, "timestamped.trx");
        TestTrxBuilder.CreateTrxFile(
            trxPath,
            new TestTrxCase(
                "Tests.Type.TimedFailure", "Tests.Type.TimedFailure", "Failed",
                ErrorMessage: "boom",
                StartTime: "2026-06-08T18:34:22.1234567+00:00",
                EndTime: "2026-06-08T18:34:25.7654321+00:00"));

        var jsonPath = Path.Combine(_tempDirectory.Path, "failed.json");
        var result = await RunToolAsync(trxPath, "--failed-tests-json", jsonPath);

        Assert.Equal(0, result.ExitCode);
        var payload = JsonSerializer.Deserialize<FailedTestsPayload>(
            await File.ReadAllTextAsync(jsonPath),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.NotNull(payload);
        Assert.Equal(1, payload!.Count);
        Assert.Contains("Tests.Type.TimedFailure", payload.FailedTests);
    }

    private async Task<ToolResult> RunToolAsync(string trxPath, params string[] extraArgs)
    {
        ProcessStartInfo processStartInfo = new()
        {
            FileName = "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        processStartInfo.ArgumentList.Add("run");
        processStartInfo.ArgumentList.Add("--no-build");
        processStartInfo.ArgumentList.Add("--project");
        processStartInfo.ArgumentList.Add(_fixture.ToolProjectPath);
        processStartInfo.ArgumentList.Add("--");
        processStartInfo.ArgumentList.Add(trxPath);

        foreach (var extraArg in extraArgs)
        {
            processStartInfo.ArgumentList.Add(extraArg);
        }

        // Prevent the tool from writing sample data to the real GitHub Actions job summary.
        processStartInfo.Environment.Remove("GITHUB_STEP_SUMMARY");

        using var process = new Process { StartInfo = processStartInfo };
        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        using var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try
        {
            await process.WaitForExitAsync(cancellationTokenSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(stdout))
        {
            _output.WriteLine(stdout);
        }

        if (!string.IsNullOrWhiteSpace(stderr))
        {
            _output.WriteLine(stderr);
        }

        return new ToolResult(process.ExitCode, stdout, stderr);
    }

    private sealed record ToolResult(int ExitCode, string Output, string Error);

    private sealed record FailedTestsPayload(string[] FailedTests, int Count, bool ExtractionFailed);
}
