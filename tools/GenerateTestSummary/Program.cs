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

var rootCommand = new RootCommand
{
    dirPathOrTrxFilePathArgument,
    outputOption,
    combinedSummaryOption,
    urlOption
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

    if (combinedSummary && !string.IsNullOrEmpty(url))
    {
        Console.WriteLine("Error: --url option is not supported with --combined option.");
        return;
    }

    string report;
    if (combinedSummary)
    {
        report = TestSummaryGenerator.CreateCombinedTestSummaryReport(dirPathOrTrxFilePath);
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
