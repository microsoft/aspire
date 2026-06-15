// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
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

    private async Task<ToolResult> RunToolAsync(string trxPath)
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
}
