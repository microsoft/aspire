// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using System.CommandLine;
using Aspire.TestTools;

// Usage: dotnet tools run GenerateTestSummary --dirPathOrTrxFilePath <path> [--output <output>] [--combined]
// Generate a summary report from trx files.
// And write to $GITHUB_STEP_SUMMARY if running in GitHub Actions.

var dirPathOrTrxFilePathArgument = new Argument<string>("dirPathOrTrxFilePath");
var outputOption = new Option<string>("--output", "-o") { Description = "Output file path" };
var combinedSummaryOption = new Option<bool>("--combined", "-c") { Description = "Generate combined summary report" };
var urlOption = new Option<string>("--url", "-u") { Description = "URL for test links" };
var nugetBuildMinutesOption = new Option<double>("--nuget-build-minutes") { Description = "Wall-clock minutes for the NuGet package build step" };
var cliBuildMinutesOption = new Option<double>("--cli-build-minutes") { Description = "Wall-clock minutes for the CLI native archive build step" };
var testDepMapOption = new Option<string>("--test-dep-map") { Description = "Path to JSON file mapping test shortnames to dependency buckets" };

var rootCommand = new RootCommand
{
    dirPathOrTrxFilePathArgument,
    outputOption,
    combinedSummaryOption,
    urlOption,
    nugetBuildMinutesOption,
    cliBuildMinutesOption,
    testDepMapOption
};

rootCommand.SetAction(result =>
{
    var dirPathOrTrxFilePath = result.GetValue<string>(dirPathOrTrxFilePathArgument);
    if (string.IsNullOrEmpty(dirPathOrTrxFilePath))
    {
        Console.WriteLine("Error: Please provide a directory path with trx files or a trx file path.");
        return;
    }

    var combinedSummary = result.GetValue<bool>(combinedSummaryOption);
    var url = result.GetValue<string>(urlOption);
    var nugetBuildMinutes = result.GetValue<double>(nugetBuildMinutesOption);
    var cliBuildMinutes = result.GetValue<double>(cliBuildMinutesOption);
    var testDepMapPath = result.GetValue<string>(testDepMapOption);

    if (combinedSummary && !string.IsNullOrEmpty(url))
    {
        Console.WriteLine("Error: --url option is not supported with --combined option.");
        return;
    }

    // Load test dependency map if provided
    Dictionary<string, string>? testDepMap = null;
    if (!string.IsNullOrEmpty(testDepMapPath))
    {
        if (!File.Exists(testDepMapPath))
        {
            Console.WriteLine($"Error: Test dependency map file not found: {testDepMapPath}");
            return;
        }

        try
        {
            testDepMap = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(
                File.ReadAllText(testDepMapPath));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: Failed to parse test dependency map: {ex.Message}");
            return;
        }
    }

    var infraTiming = new InfraTimingInfo(nugetBuildMinutes, cliBuildMinutes, testDepMap);

    string report;
    if (combinedSummary)
    {
        report = TestSummaryGenerator.CreateCombinedTestSummaryReport(dirPathOrTrxFilePath, infraTiming);
    }
    else
    {
        var reportBuilder = new StringBuilder();
        if (Directory.Exists(dirPathOrTrxFilePath))
        {
            var trxFiles = Directory.EnumerateFiles(dirPathOrTrxFilePath, "*.trx", SearchOption.AllDirectories).ToList();
            if (trxFiles.Count == 0)
            {
                Console.WriteLine($"Warning: No trx files found in directory: {dirPathOrTrxFilePath}");
            }
            else
            {
                foreach (var trxFile in trxFiles)
                {
                    TestSummaryGenerator.CreateSingleTestSummaryReport(trxFile, reportBuilder, url);
                }
            }
        }
        else
        {
            TestSummaryGenerator.CreateSingleTestSummaryReport(dirPathOrTrxFilePath, reportBuilder, url);
        }

        report = reportBuilder.ToString();
    }

    if (report.Length == 0)
    {
        Console.WriteLine("No test results found.");
        return;
    }

    var outputFilePath = result.GetValue<string>(outputOption);
    if (outputFilePath is not null)
    {
        File.WriteAllText(outputFilePath, report);
        Console.WriteLine($"Report written to {outputFilePath}");
    }

    if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true"
        && Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY") is string summaryPath)
    {
        // GitHub Actions limits $GITHUB_STEP_SUMMARY to 1024 KB.
        // Truncate to stay safely under the limit.
        const int maxSummaryBytes = 900 * 1024; // 900 KB with headroom
        var summaryToWrite = report;
        if (Encoding.UTF8.GetByteCount(report) > maxSummaryBytes)
        {
            Console.WriteLine($"Report size ({Encoding.UTF8.GetByteCount(report) / 1024}KB) exceeds GitHub step summary limit. Truncating.");
            // Find a safe truncation point within the byte limit
            var truncated = report.AsSpan();
            while (Encoding.UTF8.GetByteCount(truncated) > maxSummaryBytes)
            {
                truncated = truncated[..(truncated.Length - 1024)];
            }
            summaryToWrite = string.Concat(truncated, "\n\n⚠️ *Summary truncated — output exceeded GitHub step summary size limit.*\n");
        }
        Console.WriteLine($"Detected GitHub Actions environment. Writing to {summaryPath}");
        File.WriteAllText(summaryPath, summaryToWrite);
    }

    Console.WriteLine(report);
});

return rootCommand.Parse(args).Invoke();

/// <summary>
/// Holds infrastructure build timing and test dependency classification data.
/// </summary>
/// <param name="NugetBuildMinutes">Wall-clock minutes for the NuGet package build step.</param>
/// <param name="CliBuildMinutes">Wall-clock minutes for the CLI native archive build step.</param>
/// <param name="TestDepMap">Maps test shortnames to dependency buckets (no_nugets, requires_nugets, requires_cli_archive).</param>
internal sealed record InfraTimingInfo(double NugetBuildMinutes, double CliBuildMinutes, Dictionary<string, string>? TestDepMap)
{
    public bool HasData => TestDepMap is not null && TestDepMap.Count > 0 && (NugetBuildMinutes > 0 || CliBuildMinutes > 0);

    public double GetInfraCostMinutes(string depBucket) => depBucket switch
    {
        "requires_cli_archive" => NugetBuildMinutes + CliBuildMinutes,
        "requires_nugets" => NugetBuildMinutes,
        _ => 0
    };
}
