// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using System.Text.Json;
using System.CommandLine;
using Aspire.TestTools;

// Usage: dotnet tools run GenerateTestSummary --dirPathOrTrxFilePath <path> [--output <output>] [--combined]
// Generate a summary report from trx files.
// And write to $GITHUB_STEP_SUMMARY if running in GitHub Actions.

var dirPathOrTrxFilePathArgument = new Argument<string>("dirPathOrTrxFilePath");
var outputOption = new Option<string>("--output", "-o") { Description = "Output file path" };
var combinedSummaryOption = new Option<bool>("--combined", "-c") { Description = "Generate combined summary report" };
var urlOption = new Option<string>("--url", "-u") { Description = "URL for test links" };
// Emits a machine-readable list of failed test names instead of the markdown
// report. Consumed by the outerloop failure-issue reporter
// (.github/workflows/report-specialized-test-failures.js) to decide whether a
// failed run was a test failure (non-empty) or an infrastructure break (empty).
var failedTestsJsonOption = new Option<string>("--failed-tests-json") { Description = "Write distinct failed test names as JSON to this path and exit" };

var rootCommand = new RootCommand
{
    dirPathOrTrxFilePathArgument,
    outputOption,
    combinedSummaryOption,
    urlOption,
    failedTestsJsonOption
};

rootCommand.SetAction(result =>
{
    var dirPathOrTrxFilePath = result.GetValue<string>(dirPathOrTrxFilePathArgument);
    if (string.IsNullOrEmpty(dirPathOrTrxFilePath))
    {
        Console.WriteLine("Error: Please provide a directory path with trx files or a trx file path.");
        return;
    }

    var failedTestsJsonPath = result.GetValue<string>(failedTestsJsonOption);
    if (!string.IsNullOrEmpty(failedTestsJsonPath))
    {
        WriteFailedTestsJson(dirPathOrTrxFilePath, failedTestsJsonPath);
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

// Collects distinct failed test names across every .trx under the given path and
// writes them as JSON: { "failedTests": [...], "count": N, "extractionFailed": bool }.
// "Failed", "Error", "Timeout", and "Aborted" all count as failures;
// "Passed"/"NotExecuted" are ignored. The list is sorted for stable, diff-friendly
// output.
//
// extractionFailed is true when at least one .trx could not be read AND no failures
// were collected from the ones that did. In that case a "zero failures" result
// cannot be trusted — an unreadable .trx may have held the failures — so the
// reporter treats the red run as a test failure (not infra) rather than silently
// dropping the failing-test signal. This includes the partial case: if some .trx
// read cleanly with zero failures but another could not be read, the result is
// still flagged. extractionFailed is false only when every .trx read without error
// (any failure count, including zero, is then trustworthy) or when at least one
// failure was collected.
//
// Uses GetDetailedTestResultsFromTrx, NOT GetTestResultsFromTrx: the latter calls
// TimeSpan.Parse on the UnitTestResult startTime/endTime attributes, which real
// MTP `--report-trx` files emit as ISO-8601 DateTimeOffset (e.g.
// "2026-06-08T18:34:22.1234567+00:00"). TimeSpan.Parse throws FormatException on
// those, which would make this path silently skip every real .trx. The detailed
// reader does not parse timestamps and also resolves the fully-qualified
// CanonicalName from the TRX TestDefinitions.
static void WriteFailedTestsJson(string dirPathOrTrxFilePath, string outputPath)
{
    var trxFiles = Directory.Exists(dirPathOrTrxFilePath)
        ? Directory.EnumerateFiles(dirPathOrTrxFilePath, "*.trx", SearchOption.AllDirectories).ToList()
        : [dirPathOrTrxFilePath];

    // Mirror the failed-outcome set used by the sibling failing-test tooling
    // (tools/CreateFailingTestIssue/FailingTestIssueCommand.cs) so an aborted test
    // is not silently dropped — a red run with only aborted results would otherwise
    // report zero failures and be misfiled as infrastructure.
    var failedOutcomes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Failed", "Error", "Timeout", "Aborted" };
    var failedTests = new SortedSet<string>(StringComparer.Ordinal);
    var readErrors = 0;

    foreach (var trxFile in trxFiles)
    {
        IList<DetailedTestResult> results;
        try
        {
            results = TrxReader.GetDetailedTestResultsFromTrx(trxFile, result => failedOutcomes.Contains(result.Outcome));
        }
        catch (Exception ex)
        {
            // A single malformed/partial .trx must not lose the failures recorded
            // in the others — log and keep scanning. If every .trx fails this way
            // (readErrors > 0 with zero collected failures), extractionFailed below
            // flags the result as untrustworthy.
            readErrors++;
            Console.WriteLine($"Warning: could not read '{trxFile}': {ex.Message}");
            continue;
        }

        foreach (var testResult in results)
        {
            failedTests.Add(testResult.CanonicalName);
        }
    }

    var extractionFailed = readErrors > 0 && failedTests.Count == 0;
    var payload = new { failedTests = failedTests.ToArray(), count = failedTests.Count, extractionFailed };
    File.WriteAllText(outputPath, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"Wrote {failedTests.Count} failed test name(s) to {outputPath} (extractionFailed={extractionFailed}).");
}
