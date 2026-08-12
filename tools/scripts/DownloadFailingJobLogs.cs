#:project ../Aspire.TestTools/Aspire.TestTools.csproj

// Downloads logs and artifacts for failed jobs from a GitHub Actions workflow run.
// Usage: dotnet run DownloadFailingJobLogs.cs <run-id>

using System.Globalization;
using System.IO.Compression;
using System.Text.RegularExpressions;
using Aspire.TestTools;

if (args.Length == 0)
{
    Console.WriteLine("Usage: dotnet run -- <run-id>");
    Console.WriteLine("Example: dotnet run -- 19846215629");
    return;
}

if (!long.TryParse(args[0], NumberStyles.None, CultureInfo.InvariantCulture, out var runId))
{
    Console.WriteLine($"Invalid run id '{args[0]}'.");
    return;
}

var repo = "microsoft/aspire";
var cancellationToken = CancellationToken.None;

Console.WriteLine($"Finding failed jobs for run {runId}...");

var jobs = await GitHubActionsApi.ListJobsAsync(repo, runId, runAttempt: null, cancellationToken).ConfigureAwait(false);
Console.WriteLine($"Found {jobs.Count} total jobs");

var failedJobs = jobs
    .Where(static job => string.Equals(job.Conclusion, "failure", StringComparison.OrdinalIgnoreCase))
    .ToList();

Console.WriteLine($"Found {failedJobs.Count} failed jobs");

if (failedJobs.Count == 0)
{
    Console.WriteLine("No failed jobs found!");
    return;
}

var artifacts = await GitHubActionsApi.ListArtifactsAsync(repo, runId, cancellationToken).ConfigureAwait(false);
Console.WriteLine($"Found {artifacts.Count} total artifacts");

var logsDownloaded = 0;
for (var counter = 0; counter < failedJobs.Count; counter++)
{
    var job = failedJobs[counter];

    Console.WriteLine($"\n=== Failed Job {counter + 1}/{failedJobs.Count} ===");
    Console.WriteLine($"Name: {job.Name}");
    Console.WriteLine($"ID: {job.Id}");
    Console.WriteLine($"URL: {job.HtmlUrl}");

    Console.WriteLine("Downloading job logs...");
    try
    {
        var logs = await GitHubActionsApi.DownloadJobLogAsync(repo, job.Id, cancellationToken).ConfigureAwait(false);
        var safeName = Regex.Replace(job.Name ?? $"job_{job.Id}", @"[^a-zA-Z0-9_-]", "_");
        var filename = $"failed_job_{counter}_{safeName}.log";
        await File.WriteAllTextAsync(filename, logs, cancellationToken).ConfigureAwait(false);
        Console.WriteLine($"Saved job logs to: {filename} ({logs.Length} characters)");
        logsDownloaded++;

        Console.WriteLine("\nSearching for test failures in job logs...");
        var failedTestPattern = @"Failed\s+(.+?)\s*\[";
        var errorPattern = @"Error Message:\s*(.+?)(?:\r?\n|$)";
        var exceptionPattern = @"(System\.\w+Exception:.+?)(?:\r?\n   at|\r?\n\r?\n|$)";

        var failedTests = Regex.Matches(logs, failedTestPattern)
            .Select(match => match.Groups[1].Value.Trim())
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var errors = Regex.Matches(logs, errorPattern)
            .Select(match => match.Groups[1].Value.Trim())
            .Where(static error => !string.IsNullOrWhiteSpace(error))
            .ToList();

        foreach (Match match in Regex.Matches(logs, exceptionPattern, RegexOptions.Singleline))
        {
            var exception = match.Groups[1].Value.Trim();
            if (!errors.Contains(exception, StringComparer.Ordinal))
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
    catch (Exception ex)
    {
        Console.WriteLine($"Error downloading job logs: {ex.Message}");
    }

    var artifactMatch = Regex.Match(job.Name ?? string.Empty, @".*\(([^)]+)\)\s*/\s*\S+\s+\(([^)]+)\)");
    if (!artifactMatch.Success)
    {
        Console.WriteLine("\nCould not parse job name to determine artifact name.");
        continue;
    }

    var testShortName = artifactMatch.Groups[1].Value.Trim();
    var os = artifactMatch.Groups[2].Value.Trim();
    var artifactName = $"logs-{testShortName}-{os}";

    Console.WriteLine($"\nAttempting to download artifact: {artifactName}");

    var artifact = artifacts.FirstOrDefault(candidate => string.Equals(candidate.Name, artifactName, StringComparison.Ordinal));
    if (artifact is null)
    {
        Console.WriteLine($"Artifact '{artifactName}' not found for this run.");
        continue;
    }

    Console.WriteLine($"Found artifact ID: {artifact.Id}");

    var artifactZip = $"artifact_{counter}_{testShortName}_{os}.zip";
    try
    {
        await GitHubActionsApi.DownloadArtifactZipAsync(repo, artifact.Id, artifactZip, cancellationToken).ConfigureAwait(false);
        Console.WriteLine($"Downloaded artifact to: {artifactZip}");

        var extractDir = $"artifact_{counter}_{testShortName}_{os}";
        try
        {
            if (Directory.Exists(extractDir))
            {
                Directory.Delete(extractDir, recursive: true);
            }

            ValidateZipEntries(artifactZip, extractDir);
            ZipFile.ExtractToDirectory(artifactZip, extractDir, overwriteFiles: true);
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
    catch (Exception ex)
    {
        Console.WriteLine($"Error downloading artifact: {ex.Message}");
    }
}

Console.WriteLine("\n=== Summary ===");
Console.WriteLine($"Total jobs: {jobs.Count}");
Console.WriteLine($"Failed jobs: {failedJobs.Count}");
Console.WriteLine($"Logs downloaded: {logsDownloaded}");
Console.WriteLine("\nAll logs saved in current directory with pattern: failed_job_*.log");

static void ValidateZipEntries(string zipPath, string extractDirectory)
{
    var fullExtractPath = Path.GetFullPath(extractDirectory) + Path.DirectorySeparatorChar;
    using var archive = ZipFile.OpenRead(zipPath);
    foreach (var entry in archive.Entries)
    {
        var destinationPath = Path.GetFullPath(Path.Combine(extractDirectory, entry.FullName));
        if (!destinationPath.StartsWith(fullExtractPath, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Zip entry '{entry.FullName}' would extract outside the target directory.");
        }
    }
}
