// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace Infrastructure.Tests;

/// <summary>
/// End-to-end tests for the DownloadFailingJobLogs script.
/// </summary>
public sealed class DownloadFailingJobLogsToolTests : IClassFixture<DownloadFailingJobLogsFixture>, IDisposable
{
    private const long RunId = 123;
    private const long JobId = 456;
    private const long ArtifactId = 789;
    private const string Repository = "microsoft/aspire";
    private const string JobName = "Tests / Linux / Sample (Sample) / Sample (linux-latest)";
    private const string ArtifactName = "logs-Sample-linux-latest";
    private const string LogContent = """
        Failed Tests.Namespace.Type.Method(input: 1) [12 ms]
        Error Message: Expected 1 but found 2.
        System.InvalidOperationException: Expected 1 but found 2.
           at Tests.Namespace.Type.Method() in TestFile.cs:line 42
        """;

    private readonly TestTempDirectory _tempDirectory = new();
    private readonly DownloadFailingJobLogsFixture _fixture;
    private readonly ITestOutputHelper _output;

    public DownloadFailingJobLogsToolTests(DownloadFailingJobLogsFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    public void Dispose() => _tempDirectory.Dispose();

    [Fact]
    public async Task DownloadsLogsAndExtractsArtifactsFromFixtureRun()
    {
        var fixtureDirectory = Path.Combine(_tempDirectory.Path, "fixtures-success");
        Directory.CreateDirectory(fixtureDirectory);

        WriteJobsFixture(fixtureDirectory);
        WriteArtifactsFixture(fixtureDirectory, includeArtifact: true);
        WriteTextFixture(fixtureDirectory, $"repos/{Repository}/actions/jobs/{JobId}/logs", ".txt", LogContent);
        WriteArtifactFixture(fixtureDirectory);

        var result = await RunToolAsync(fixtureDirectory);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Found 1 failed jobs", result.Output, StringComparison.Ordinal);
        Assert.Contains("Saved job logs to: failed_job_0_Tests___Linux___Sample__Sample____Sample__linux-latest_.log", result.Output, StringComparison.Ordinal);
        Assert.Contains("Downloaded artifact to: artifact_0_Sample_linux-latest.zip", result.Output, StringComparison.Ordinal);
        Assert.Contains("Found 1 .trx file(s):", result.Output, StringComparison.Ordinal);

        var logPath = Path.Combine(_tempDirectory.Path, "failed_job_0_Tests___Linux___Sample__Sample____Sample__linux-latest_.log");
        Assert.True(File.Exists(logPath));
        Assert.Contains("Expected 1 but found 2.", File.ReadAllText(logPath), StringComparison.Ordinal);

        var extractedTrx = Path.Combine(_tempDirectory.Path, "artifact_0_Sample_linux-latest", "testresults", "Sample_net10.0.trx");
        Assert.True(File.Exists(extractedTrx));
    }

    [Fact]
    public async Task ReportsMissingArtifactWithoutFailing()
    {
        var fixtureDirectory = Path.Combine(_tempDirectory.Path, "fixtures-missing-artifact");
        Directory.CreateDirectory(fixtureDirectory);

        WriteJobsFixture(fixtureDirectory);
        WriteArtifactsFixture(fixtureDirectory, includeArtifact: false);
        WriteTextFixture(fixtureDirectory, $"repos/{Repository}/actions/jobs/{JobId}/logs", ".txt", LogContent);

        var result = await RunToolAsync(fixtureDirectory);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains($"Artifact '{ArtifactName}' not found for this run.", result.Output, StringComparison.Ordinal);
    }

    private async Task<ToolResult> RunToolAsync(string fixtureDirectory)
    {
        ProcessStartInfo processStartInfo = new()
        {
            FileName = _fixture.DotNetPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = _tempDirectory.Path
        };

        processStartInfo.ArgumentList.Add("run");
        processStartInfo.ArgumentList.Add(_fixture.ToolPath);
        processStartInfo.ArgumentList.Add("--");
        processStartInfo.ArgumentList.Add(RunId.ToString());
        processStartInfo.Environment["ASPIRE_FAILING_TEST_ISSUE_FIXTURE_DIR"] = fixtureDirectory;

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

    private static void WriteJobsFixture(string fixtureDirectory)
    {
        WriteJsonFixture(
            fixtureDirectory,
            $"repos/{Repository}/actions/runs/{RunId}/jobs?per_page=100&page=1",
            new
            {
                jobs = new[]
                {
                    new
                    {
                        id = JobId,
                        name = JobName,
                        html_url = $"https://github.com/{Repository}/actions/runs/{RunId}/job/{JobId}",
                        conclusion = "failure",
                        status = "completed"
                    }
                }
            });
    }

    private static void WriteArtifactsFixture(string fixtureDirectory, bool includeArtifact)
    {
        WriteJsonFixture(
            fixtureDirectory,
            $"repos/{Repository}/actions/runs/{RunId}/artifacts?per_page=100&page=1",
            new
            {
                artifacts = includeArtifact
                    ? new[]
                    {
                        new
                        {
                            id = ArtifactId,
                            name = ArtifactName
                        }
                    }
                    : Array.Empty<object>()
            });
    }

    private static void WriteArtifactFixture(string fixtureDirectory)
    {
        var zipPath = GetFixturePath(fixtureDirectory, $"repos/{Repository}/actions/artifacts/{ArtifactId}/zip", ".zip");
        TestTrxBuilder.CreateArtifactZip(
            zipPath,
            "testresults/Sample_net10.0.trx",
            new TestTrxCase(
                CanonicalTestName: "Tests.Namespace.Type.Method",
                DisplayName: "Tests.Namespace.Type.Method(input: 1)",
                Outcome: "Failed",
                ErrorMessage: "Expected 1 but found 2.",
                StackTrace: "at Tests.Namespace.Type.Method() in TestFile.cs:line 42",
                StdOut: "stdout line 1"));
    }

    private static void WriteJsonFixture<T>(string fixtureDirectory, string endpoint, T payload)
        => WriteTextFixture(fixtureDirectory, endpoint, ".json", JsonSerializer.Serialize(payload));

    private static void WriteTextFixture(string fixtureDirectory, string endpoint, string extension, string content)
    {
        var path = GetFixturePath(fixtureDirectory, endpoint, extension);
        File.WriteAllText(path, content);
    }

    private static string GetFixturePath(string fixtureDirectory, string endpoint, string extension)
    {
        var fileName = Regex.Replace(endpoint, @"[^A-Za-z0-9._-]+", "_").Trim('_');
        return Path.Combine(fixtureDirectory, $"{fileName}{extension}");
    }

    private sealed record ToolResult(int ExitCode, string Output, string Error);
}
