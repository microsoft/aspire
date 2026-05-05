// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using TestSelector;
using TestSelector.Models;

// Define CLI options
var solutionOption = new Option<string>("--solution", "-s") { Description = "Path to the solution file (.sln or .slnx)", Required = true };
var configOption = new Option<string?>("--config", "-c") { Description = "Path to the test selector configuration file (if not provided, category logic is skipped)" };
var fromOption = new Option<string?>("--from", "-f") { Description = "Git ref to compare from (e.g., origin/main). Required unless --changed-files is provided" };
var toOption = new Option<string?>("--to", "-t") { Description = "Git ref to compare to (default: HEAD)" };
var changedFilesOption = new Option<string?>("--changed-files") { Description = "Comma-separated list of changed files (bypasses git entirely)" };
var nonApplyingPathsOption = new Option<string?>("--non-applying-paths") { Description = "Comma-separated list of paths that should return a non-applying result when they are the only active changes" };
var outputOption = new Option<string?>("--output", "-o") { Description = "Output file path for the JSON result" };
var githubOutputOption = new Option<bool>("--github-output") { Description = "Output in GitHub Actions format" };
var verboseOption = new Option<bool>("--verbose", "-v") { Description = "Enable verbose output" };

var rootCommand = new RootCommand("Test selection tool for Aspire")
{
    solutionOption,
    configOption,
    fromOption,
    toOption,
    changedFilesOption,
    nonApplyingPathsOption,
    outputOption,
    githubOutputOption,
    verboseOption
};

rootCommand.SetAction(async result =>
{
    var solution = result.GetValue(solutionOption)!;
    var configPath = result.GetValue(configOption);
    var fromRef = result.GetValue(fromOption);
    var toRef = result.GetValue(toOption);
    var changedFilesStr = result.GetValue(changedFilesOption);
    var nonApplyingPathsStr = result.GetValue(nonApplyingPathsOption);
    var outputPath = result.GetValue(outputOption);
    var githubOutput = result.GetValue(githubOutputOption);
    var verbose = result.GetValue(verboseOption);

    var workingDir = Directory.GetCurrentDirectory();

    // Detect CI environment for appropriate output formatting
    var ciEnvironment = CIHelper.DetectCIEnvironment();

    // Validate: --from is required unless --changed-files is provided
    if (string.IsNullOrEmpty(changedFilesStr) && string.IsNullOrEmpty(fromRef))
    {
        CIHelper.WriteError(ciEnvironment, "--from is required when --changed-files is not provided");
        Environment.Exit(1);
        return;
    }

    if (verbose)
    {
        Console.WriteLine($"Working directory: {workingDir}");
        Console.WriteLine($"CI environment: {ciEnvironment}");
        Console.WriteLine($"Solution: {solution}");
        Console.WriteLine($"Config: {configPath ?? "(none - category logic skipped)"}");
        if (!string.IsNullOrEmpty(changedFilesStr))
        {
            Console.WriteLine("Changed files: (provided via --changed-files)");
        }
        else
        {
            Console.WriteLine($"From ref: {fromRef}");
            Console.WriteLine($"To ref: {toRef ?? "HEAD"}");
        }
    }

    try
    {
        // Load configuration (optional - if not provided, category logic is skipped)
        TestSelectorConfig? config = null;
        if (!string.IsNullOrEmpty(configPath))
        {
            var configFullPath = Path.IsPathRooted(configPath) ? configPath : Path.Combine(workingDir, configPath);
            if (!File.Exists(configFullPath))
            {
                CIHelper.WriteError(ciEnvironment, $"Configuration file not found: {configFullPath}");
                Environment.Exit(1);
                return;
            }

            config = TestSelectorConfig.LoadFromFile(configFullPath);
            if (verbose)
            {
                Console.WriteLine($"Loaded config with {config.IgnorePaths.Count} ignore patterns");
            }
        }
        else if (verbose)
        {
            Console.WriteLine("No config file provided - using dotnet-affected only (category logic skipped)");
        }

        // Get changed files
        List<string> changedFiles;
        if (!string.IsNullOrEmpty(changedFilesStr))
        {
            changedFiles = changedFilesStr.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(f => f.Trim())
                .ToList();

            if (verbose)
            {
                Console.WriteLine($"Using {changedFiles.Count} explicitly provided changed files");
            }
        }
        else
        {
            // fromRef is guaranteed non-null here due to earlier validation
            changedFiles = await GitHelper.GetGitChangedFilesAsync(fromRef!, toRef, workingDir).ConfigureAwait(false);

            if (verbose)
            {
                Console.WriteLine($"Found {changedFiles.Count} changed files from git diff");
            }
        }

        var nonApplyingPaths = string.IsNullOrWhiteSpace(nonApplyingPathsStr)
            ? []
            : nonApplyingPathsStr.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

        // Run the evaluation
        var evaluationResult = await TestEvaluator.EvaluateAsync(config, changedFiles, solution, fromRef, toRef, workingDir, ciEnvironment, verbose, nonApplyingPaths).ConfigureAwait(false);

        // For RunAll/CriticalPath results, populate NuGet-dependent tests (all of them)
        if (evaluationResult.RunAllTests && evaluationResult.NuGetDependentTests is null)
        {
            evaluationResult.NuGetDependentTests = TestEvaluator.PopulateAllNuGetDependentTests(workingDir);
        }

        // Output result
        WriteJsonResult(evaluationResult, workingDir, outputPath);

        if (githubOutput)
        {
            evaluationResult.WriteGitHubOutput();
        }
        else if (string.IsNullOrEmpty(outputPath))
        {
            Console.WriteLine(evaluationResult.ToJson());
        }
    }
    catch (Exception ex)
    {
        CIHelper.WriteError(ciEnvironment, $"Test selector failed: {ex.Message}");
        CIHelper.WriteWarning(ciEnvironment, "Test selector failed before it could determine a selective scope. IGNORING selective scope and expecting CI to run all tests as a conservative fallback.");
        var errorResult = TestSelectionResult.WithError(ex.Message);
        WriteJsonResult(errorResult, workingDir, outputPath);
        if (githubOutput)
        {
            errorResult.WriteGitHubOutput();
        }
        else
        {
            Console.WriteLine(errorResult.ToJson());
        }
        Environment.Exit(1);
    }
});

return await rootCommand.Parse(args).InvokeAsync().ConfigureAwait(false);

static void WriteJsonResult(TestSelectionResult result, string workingDir, string? outputPath)
{
    if (string.IsNullOrEmpty(outputPath))
    {
        return;
    }

    var outputFullPath = Path.IsPathRooted(outputPath) ? outputPath : Path.Combine(workingDir, outputPath);
    var outputDirectory = Path.GetDirectoryName(outputFullPath);
    if (!string.IsNullOrEmpty(outputDirectory))
    {
        Directory.CreateDirectory(outputDirectory);
    }

    File.WriteAllText(outputFullPath, result.ToJson());
    Console.WriteLine($"Result written to {outputFullPath}");
}
