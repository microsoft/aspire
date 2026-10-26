// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.TestUtilities;
using Xunit;

namespace Infrastructure.Tests;

public class VerifyNonBlockingTestResultsTests : IDisposable
{
    private readonly TemporaryWorkspace _workspace;
    private readonly string _scriptPath;
    private readonly ITestOutputHelper _output;

    public VerifyNonBlockingTestResultsTests(ITestOutputHelper output)
    {
        _output = output;
        _workspace = TemporaryWorkspace.Create(output);
        _scriptPath = Path.Combine(RepoRoot.Path, "eng", "scripts", "verify-nonblocking-test-results.ps1");
    }

    public void Dispose() => _workspace.Dispose();

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(13)]
    [RequiresTools(["pwsh"])]
    public async Task AcceptsKnownTestOutcomesWithExecutedTests(int exitCode)
    {
        var (testResultsPath, exitCodePath) = CreateInputs(exitCode, testCount: 1);

        var result = await RunScript(testResultsPath, exitCodePath);

        result.EnsureSuccessful();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(13)]
    [RequiresTools(["pwsh"])]
    public async Task RejectsKnownTestOutcomesWithoutTrx(int exitCode)
    {
        var (testResultsPath, exitCodePath) = CreateInputs(exitCode, testCount: null);

        var result = await RunScript(testResultsPath, exitCodePath);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("No .trx files found", result.Output);
    }

    [Theory]
    [InlineData("session.cast")]
    [InlineData("test-hangdump.dmp")]
    [RequiresTools(["pwsh"])]
    public async Task RejectsExecutionArtifactsWithoutTrx(string artifactName)
    {
        var (testResultsPath, exitCodePath) = CreateInputs(exitCode: 2, testCount: null);
        File.WriteAllText(Path.Combine(testResultsPath, artifactName), "");

        var result = await RunScript(testResultsPath, exitCodePath);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("No .trx files found", result.Output);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(127)]
    [RequiresTools(["pwsh"])]
    public async Task RejectsUnknownOrInfrastructureExitCodes(int exitCode)
    {
        var (testResultsPath, exitCodePath) = CreateInputs(exitCode, testCount: 1);

        var result = await RunScript(testResultsPath, exitCodePath);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains($"unclassified code {exitCode}", result.Output);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task RejectsMissingExitCodeEvidence()
    {
        var testResultsPath = _workspace.CreateDirectory("testresults").FullName;
        var exitCodePath = Path.Combine(_workspace.Path, "missing-exit-code.txt");

        var result = await RunScript(testResultsPath, exitCodePath);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("No test exit code file found", result.Output);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task RejectsZeroExecutedTests()
    {
        var (testResultsPath, exitCodePath) = CreateInputs(exitCode: 0, testCount: 0);

        var result = await RunScript(testResultsPath, exitCodePath);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("contain zero executed tests", result.Output);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task AcceptsZeroExecutedTestsWhenExplicitlyAllowed()
    {
        var (testResultsPath, exitCodePath) = CreateInputs(exitCode: 0, testCount: 0);

        var result = await RunScript(testResultsPath, exitCodePath, allowZeroTests: true);

        result.EnsureSuccessful();
        Assert.Contains("explicitly allows", result.Output);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(13)]
    [RequiresTools(["pwsh"])]
    public async Task RejectsZeroExecutedTestsForNonSuccessOutcomesWhenExplicitlyAllowed(int exitCode)
    {
        var (testResultsPath, exitCodePath) = CreateInputs(exitCode, testCount: 0);

        var result = await RunScript(testResultsPath, exitCodePath, allowZeroTests: true);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("contain zero executed tests", result.Output);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task RejectsAllSkippedTests()
    {
        var (testResultsPath, exitCodePath) = CreateInputs(exitCode: 0, testCount: 90, executedTestCount: 0);

        var result = await RunScript(testResultsPath, exitCodePath);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("contain zero executed tests", result.Output);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task AcceptsAllSkippedTestsWhenExplicitlyAllowed()
    {
        var (testResultsPath, exitCodePath) = CreateInputs(exitCode: 0, testCount: 90, executedTestCount: 0);

        var result = await RunScript(testResultsPath, exitCodePath, allowZeroTests: true);

        result.EnsureSuccessful();
        Assert.Contains("explicitly allows", result.Output);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task RejectsMalformedTrxWhenAnotherTrxIsValid()
    {
        var (testResultsPath, exitCodePath) = CreateInputs(exitCode: 0, testCount: 1);
        File.WriteAllText(Path.Combine(testResultsPath, "malformed.trx"), "<TestRun>");

        var result = await RunScript(testResultsPath, exitCodePath);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Failed to parse TRX file malformed.trx", result.Output);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task RejectsMissingTestCountWhenZeroTestsAreAllowed()
    {
        var (testResultsPath, exitCodePath) = CreateInputs(exitCode: 0, testCount: null);
        File.WriteAllText(
            Path.Combine(testResultsPath, "incomplete.trx"),
            """
            <TestRun>
              <ResultSummary />
            </TestRun>
            """);

        var result = await RunScript(testResultsPath, exitCodePath, allowZeroTests: true);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("does not contain a Counters total value", result.Output);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("invalid")]
    [RequiresTools(["pwsh"])]
    public async Task RejectsInvalidTestCount(string testCount)
    {
        var (testResultsPath, exitCodePath) = CreateInputs(exitCode: 0, testCount: null);
        File.WriteAllText(
            Path.Combine(testResultsPath, "invalid-count.trx"),
            $"""
            <TestRun>
              <ResultSummary>
                <Counters total="{testCount}" executed="0" />
              </ResultSummary>
            </TestRun>
            """);

        var result = await RunScript(testResultsPath, exitCodePath);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Counters total must be a nonnegative integer", result.Output);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task RejectsMissingExecutedTestCount()
    {
        var (testResultsPath, exitCodePath) = CreateInputs(exitCode: 0, testCount: null);
        File.WriteAllText(
            Path.Combine(testResultsPath, "missing-executed-count.trx"),
            """
            <TestRun>
              <ResultSummary>
                <Counters total="1" />
              </ResultSummary>
            </TestRun>
            """);

        var result = await RunScript(testResultsPath, exitCodePath);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("does not contain a Counters executed value", result.Output);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("invalid")]
    [InlineData("2")]
    [RequiresTools(["pwsh"])]
    public async Task RejectsInvalidExecutedTestCount(string executedTestCount)
    {
        var (testResultsPath, exitCodePath) = CreateInputs(exitCode: 0, testCount: null);
        File.WriteAllText(
            Path.Combine(testResultsPath, "invalid-executed-count.trx"),
            $"""
            <TestRun>
              <ResultSummary>
                <Counters total="1" executed="{executedTestCount}" />
              </ResultSummary>
            </TestRun>
            """);

        var result = await RunScript(testResultsPath, exitCodePath);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Counters executed must be an integer between zero and total", result.Output);
    }

    private (string TestResultsPath, string ExitCodePath) CreateInputs(int exitCode, int? testCount, int? executedTestCount = null)
    {
        var testResultsPath = _workspace.CreateDirectory(Guid.NewGuid().ToString("N")).FullName;
        var exitCodePath = Path.Combine(testResultsPath, "test-exit-code.txt");
        File.WriteAllText(exitCodePath, exitCode.ToString());

        if (testCount is not null)
        {
            var executedCount = executedTestCount ?? testCount.Value;
            File.WriteAllText(
                Path.Combine(testResultsPath, "results.trx"),
                $"""
                <?xml version="1.0" encoding="utf-8"?>
                <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
                  <ResultSummary>
                    <Counters total="{testCount}" executed="{executedCount}" notExecuted="{testCount - executedCount}" />
                  </ResultSummary>
                </TestRun>
                """);
        }

        return (testResultsPath, exitCodePath);
    }

    private async Task<CommandResult> RunScript(string testResultsPath, string exitCodePath, bool allowZeroTests = false)
    {
        using var command = new PowerShellCommand(_scriptPath, _output);
        var arguments = new List<string>
        {
            "-TestResultsPath", $"\"{testResultsPath}\"",
            "-ExitCodePath", $"\"{exitCodePath}\""
        };
        if (allowZeroTests)
        {
            arguments.Add("-AllowZeroTests");
        }

        return await command.ExecuteAsync(arguments.ToArray());
    }
}
