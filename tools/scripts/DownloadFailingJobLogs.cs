// Downloads logs and artifacts for failed jobs from a GitHub Actions workflow run.
// Usage: dotnet run DownloadFailingJobLogs.cs <run-id>

using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;

if (args.Length == 0)
{
    Console.WriteLine("Usage: dotnet run -- <run-id>");
    Console.WriteLine("Example: dotnet run -- 19846215629");
    return;
}

var runId = args[0];
var repo = "microsoft/aspire";

Console.WriteLine($"Finding failed jobs for run {runId}...");

// Fetch all jobs for this run (paginated, 100 per page)
var jobs = new List<JsonElement>();
int jobsPage = 1;
bool hasMoreJobPages = true;

while (hasMoreJobPages)
{
    var (success, jobsJson) = await RunGhAsync($"api repos/{repo}/actions/runs/{runId}/jobs?page={jobsPage}&per_page=100");
    if (!success)
    {
        Console.WriteLine($"Error getting jobs page {jobsPage}: {jobsJson}");
        return;
    }

    using var jobsDoc = JsonDocument.Parse(jobsJson);
    if (jobsDoc.RootElement.TryGetProperty("jobs", out var jobsArray))
    {
        var jobsOnPage = jobsArray.GetArrayLength();
        if (jobsOnPage == 0)
        {
            hasMoreJobPages = false;
        }

        foreach (var job in jobsArray.EnumerateArray())
        {
            jobs.Add(job.Clone());
        }

        // If we got fewer than 100 jobs, this is the last page
        if (jobsOnPage < 100)
        {
            hasMoreJobPages = false;
        }
    }
    else
    {
        hasMoreJobPages = false;
    }

    jobsPage++;
}

Console.WriteLine($"Found {jobs.Count} total jobs");

// Filter failed jobs
var failedJobs = jobs.Where(j =>
{
    var conclusion = j.GetProperty("conclusion").GetString();
    return conclusion == "failure";
}).ToList();

Console.WriteLine($"Found {failedJobs.Count} failed jobs");

if (failedJobs.Count == 0)
{
    Console.WriteLine("No failed jobs found!");
    return;
}

// Download logs and artifacts for each failed job
int counter = 0;
foreach (var job in failedJobs)
{
    var jobId = job.GetProperty("id").GetInt64();
    var jobName = job.GetProperty("name").GetString();
    var jobUrl = job.GetProperty("html_url").GetString();

    Console.WriteLine($"\n=== Failed Job {counter + 1}/{failedJobs.Count} ===");
    Console.WriteLine($"Name: {jobName}");
    Console.WriteLine($"ID: {jobId}");
    Console.WriteLine($"URL: {jobUrl}");

    // Download job logs
    Console.WriteLine("Downloading job logs...");
    var (logsSuccess, logs) = await RunGhAsync($"api repos/{repo}/actions/jobs/{jobId}/logs");

    if (logsSuccess)
    {
        // Save logs to file
        var safeName = Regex.Replace(jobName ?? $"job_{jobId}", @"[^a-zA-Z0-9_-]", "_");
        var filename = $"failed_job_{counter}_{safeName}.log";
        File.WriteAllText(filename, logs);
        Console.WriteLine($"Saved job logs to: {filename} ({logs.Length} characters)");

        // Parse logs for test failures and exceptions
        Console.WriteLine("\nSearching for test failures in job logs...");
        var failedTestPattern = @"Failed\s+(.+?)\s*\[";
        var errorPattern = @"Error Message:\s*(.+?)(?:\r?\n|$)";
        var exceptionPattern = @"(System\.\w+Exception:.+?)(?:\r?\n   at|\r?\n\r?\n|$)";

        var failedTests = new HashSet<string>();
        var errors = new List<string>();

        foreach (Match match in Regex.Matches(logs, failedTestPattern))
        {
            failedTests.Add(match.Groups[1].Value.Trim());
        }

        foreach (Match match in Regex.Matches(logs, errorPattern))
        {
            errors.Add(match.Groups[1].Value.Trim());
        }

        foreach (Match match in Regex.Matches(logs, exceptionPattern, RegexOptions.Singleline))
        {
            var exception = match.Groups[1].Value.Trim();
            if (!errors.Contains(exception))
            {
                errors.Add(exception);
            }
        }

        if (failedTests.Count > 0)
        {
            Console.WriteLine($"\nFailed tests ({failedTests.Count}):");
            foreach (var test in failedTests.Take(5))
            {
                Console.WriteLine($"  - {test}");
            }
            if (failedTests.Count > 5)
            {
                Console.WriteLine($"  ... and {failedTests.Count - 5} more");
            }
        }

        if (errors.Count > 0)
        {
            Console.WriteLine($"\nErrors found ({errors.Count}):");
            foreach (var error in errors.Take(3))
            {
                var displayError = error.Length > 200 ? string.Concat(error.AsSpan(0, 200), "...") : error;
                Console.WriteLine($"  - {displayError}");
            }
            if (errors.Count > 3)
            {
                Console.WriteLine($"  ... and {errors.Count - 3} more");
            }
        }
    }
    else
    {
        Console.WriteLine($"Error downloading job logs: {logs}");
    }

    // Try to find and download the test artifact based on job name pattern
    // e.g. "Tests / Integrations macos (Hosting.Azure) / Hosting.Azure (macos-latest)" -> "logs-Hosting.Azure-macos-latest"
    var artifactMatch = Regex.Match(jobName ?? "", @".*\(([^)]+)\)\s*/\s*\S+\s+\(([^)]+)\)");
    if (artifactMatch.Success)
    {
        var testShortName = artifactMatch.Groups[1].Value.Trim();
        var os = artifactMatch.Groups[2].Value.Trim();
        var artifactName = $"logs-{testShortName}-{os}";

        Console.WriteLine($"\nAttempting to download artifact: {artifactName}");

        // Search for matching artifact (paginated, 100 per page)
        string? artifactId = null;
        var allArtifactNames = new List<string>();
        int page = 1;
        bool hasMorePages = true;

        while (hasMorePages)
        {
            var (artifactsSuccess, artifactsJson) = await RunGhAsync($"api repos/{repo}/actions/runs/{runId}/artifacts?page={page}&per_page=100");
            if (!artifactsSuccess)
            {
                Console.WriteLine($"Error getting artifacts page {page}: {artifactsJson}");
                break;
            }

            using var artifactsDoc = JsonDocument.Parse(artifactsJson);
            if (artifactsDoc.RootElement.TryGetProperty("artifacts", out var artifactsArray))
            {
                var artifactsOnPage = artifactsArray.GetArrayLength();
                if (artifactsOnPage == 0)
                {
                    hasMorePages = false;
                }

                foreach (var artifact in artifactsArray.EnumerateArray())
                {
                    if (artifact.TryGetProperty("name", out var nameProperty))
                    {
                        var name = nameProperty.GetString();
                        allArtifactNames.Add(name ?? "null");

                        if (name == artifactName && artifact.TryGetProperty("id", out var idProperty))
                        {
                            artifactId = idProperty.GetInt64().ToString(CultureInfo.InvariantCulture);
                        }
                    }
                }

                // If we got fewer than 100 artifacts, this is the last page
                if (artifactsOnPage < 100)
                {
                    hasMorePages = false;
                }
            }
            else
            {
                hasMorePages = false;
            }

            page++;
        }

        Console.WriteLine($"Found {allArtifactNames.Count} total artifacts");

        if (artifactId != null)
        {
            Console.WriteLine($"Found artifact ID: {artifactId}");

            // Download artifact
            var artifactZip = $"artifact_{counter}_{testShortName}_{os}.zip";
            var downloadSuccess = await DownloadGhFileAsync($"api repos/{repo}/actions/artifacts/{artifactId}/zip", artifactZip);

            if (downloadSuccess)
            {
                Console.WriteLine($"Downloaded artifact to: {artifactZip}");

                // Extract and look for .trx test result files
                var extractDir = $"artifact_{counter}_{testShortName}_{os}";
                try
                {
                    if (Directory.Exists(extractDir))
                    {
                        Directory.Delete(extractDir, true);
                    }

                    ZipFile.ExtractToDirectory(artifactZip, extractDir);
                    Console.WriteLine($"Extracted artifact to: {extractDir}");

                    var trxFiles = Directory.GetFiles(extractDir, "*.trx", SearchOption.AllDirectories);
                    if (trxFiles.Length > 0)
                    {
                        Console.WriteLine($"\nFound {trxFiles.Length} .trx file(s):");
                        foreach (var trxFile in trxFiles)
                        {
                            Console.WriteLine($"  - {trxFile}");
                        }
                    }
                    else
                    {
                        Console.WriteLine("\nNo .trx files found in artifact.");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error extracting artifact: {ex.Message}");
                }
            }
            else
            {
                Console.WriteLine("Error downloading artifact");
            }
        }
        else
        {
            Console.WriteLine($"Artifact '{artifactName}' not found for this run.");
        }
    }
    else
    {
        Console.WriteLine("\nCould not parse job name to determine artifact name.");
    }

    counter++;
}

Console.WriteLine($"\n=== Summary ===");
Console.WriteLine($"Total jobs: {jobs.Count}");
Console.WriteLine($"Failed jobs: {failedJobs.Count}");
Console.WriteLine($"Logs downloaded: {counter}");
Console.WriteLine($"\nAll logs saved in current directory with pattern: failed_job_*.log");

// Runs gh CLI and returns the text output
async Task<(bool Success, string Output)> RunGhAsync(string arguments)
{
    using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
    {
        FileName = "gh",
        Arguments = arguments,
        RedirectStandardOutput = true,
        UseShellExecute = false
    });

    if (process is null)
    {
        return (false, "Failed to start process");
    }

    var output = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
    await process.WaitForExitAsync().ConfigureAwait(false);

    return (process.ExitCode == 0, output);
}

// Runs gh CLI and streams binary output to a file
async Task<bool> DownloadGhFileAsync(string arguments, string outputPath)
{
    using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
    {
        FileName = "gh",
        Arguments = arguments,
        RedirectStandardOutput = true,
        UseShellExecute = false
    });

    if (process is null)
    {
        return false;
    }

    using (var fileStream = File.Create(outputPath))
    {
        await process.StandardOutput.BaseStream.CopyToAsync(fileStream).ConfigureAwait(false);
    }
    await process.WaitForExitAsync().ConfigureAwait(false);

    return process.ExitCode == 0;
}
