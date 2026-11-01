// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.TestUtilities;
using Xunit;

namespace Infrastructure.Tests;

public sealed class CiResourceMonitorTests : IDisposable
{
    private const string CsvHeader = "TimestampUtc,CpuPercent,ProcessorQueue,AvailableMB,CommittedPercent,CommittedGB,CommitLimitGB,PagesPerSec,PageFilePercent,DiskQueueLength,DiskPercentTime,SystemDriveFreeGB,WorkspaceDriveFreeGB,ProcessCount";

    private readonly TemporaryWorkspace _workspace;
    private readonly string _scriptPath;
    private readonly string _outputDirectory;
    private readonly string _summaryPath;
    private readonly ITestOutputHelper _output;

    public CiResourceMonitorTests(ITestOutputHelper output)
    {
        _output = output;
        _workspace = TemporaryWorkspace.Create(output);
        _scriptPath = Path.Combine(RepoRoot.Path, "eng", "scripts", "ci-resource-monitor.ps1");
        _outputDirectory = Path.Combine(_workspace.Path, "resource-monitor");
        _summaryPath = Path.Combine(_workspace.Path, "step-summary.md");
    }

    public void Dispose() => _workspace.Dispose();

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task StopSummarizesPeaksMinimumsAndSampleGaps()
    {
        Directory.CreateDirectory(_outputDirectory);
        File.WriteAllLines(Path.Combine(_outputDirectory, "resources.csv"),
        [
            CsvHeader,
            "2026-11-01T00:00:00.0000000Z,40.0,1,9000,30.0,5.00,20.00,10,2.0,0.10,5.0,30.0,140.0,120",
            "2026-11-01T00:00:20.0000000Z,97.5,12,512,91.5,18.30,20.00,900,64.0,7.20,100.0,1.5,139.0,180",
            // A 100 s gap against a 20 s interval: the box stalled between samples.
            "2026-11-01T00:02:00.0000000Z,60.0,2,4000,50.0,10.00,20.00,50,10.0,0.50,20.0,28.0,138.0,150",
        ]);

        var result = await RunScript("Stop");

        result.EnsureSuccessful();
        var summary = File.ReadAllText(_summaryPath);
        Assert.Contains("Samples: 3", summary);
        Assert.Contains("largest gap between samples 100 s", summary);
        Assert.Contains("| CPU % | 65.8 | max 97.5 (1 samples >= 90%) |", summary);
        Assert.Contains("| Processor queue length | 5.0 | max 12.0 |", summary);
        Assert.Contains("| Available memory MB | 4504.0 | min 512.0 |", summary);
        Assert.Contains("| Commit charge % | 57.2 | max 91.5 (limit 20.0 GB) |", summary);
        Assert.Contains("| Page file usage % | 25.3 | max 64.0 |", summary);
        Assert.Contains("| Disk queue length | 2.6 | max 7.2 |", summary);
        Assert.Contains("| System drive free GB | 19.8 | min 1.5 |", summary);
        Assert.Contains("max 97.5", result.Output);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task StopWithoutSamplesSucceedsWithoutSummary()
    {
        var result = await RunScript("Stop");

        result.EnsureSuccessful();
        Assert.Contains("Resource monitor produced no samples.", result.Output);
        Assert.False(File.Exists(_summaryPath));
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task StartRecordsSamplesInBackgroundAndStopEndsTheSampler()
    {
        var startResult = await RunScript("Start", "-IntervalSeconds", "5");

        startResult.EnsureSuccessful();
        var pidFile = Path.Combine(_outputDirectory, "sampler.pid");
        Assert.True(File.Exists(pidFile), "Start should record the sampler pid.");
        var samplerPid = int.Parse(File.ReadAllText(pidFile).Trim(), System.Globalization.CultureInfo.InvariantCulture);

        try
        {
            var csvPath = Path.Combine(_outputDirectory, "resources.csv");
            var processesPath = Path.Combine(_outputDirectory, "processes.log");
            var deadline = Stopwatch.StartNew();
            while (!(File.Exists(processesPath) && ReadLinesShared(csvPath).Length >= 2))
            {
                Assert.True(deadline.Elapsed < TimeSpan.FromSeconds(60), "The background sampler did not write a sample.");
                await Task.Delay(250);
            }

            var stopResult = await RunScript("Stop");

            stopResult.EnsureSuccessful();
            Assert.Contains("### Windows runner resource monitor", stopResult.Output);
            Assert.False(File.Exists(pidFile));
            Assert.True(IsExited(samplerPid), "Stop should end the sampler process.");
        }
        finally
        {
            KillIfRunning(samplerPid);
        }
    }

    private async Task<CommandResult> RunScript(string mode, params string[] additionalArgs)
    {
        using var cmd = new PowerShellCommand(_scriptPath, _output)
            .WithTimeout(TimeSpan.FromMinutes(2))
            .WithEnvironmentVariable("GITHUB_STEP_SUMMARY", _summaryPath);

        return await cmd.ExecuteAsync(["-Mode", mode, "-OutputDirectory", $"\"{_outputDirectory}\"", .. additionalArgs]);
    }

    private static string[] ReadLinesShared(string path)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    private static bool IsExited(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.WaitForExit(TimeSpan.FromSeconds(10));
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    private static void KillIfRunning(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            process.Kill(entireProcessTree: true);
        }
        catch (ArgumentException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }
}
