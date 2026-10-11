// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using AnalyzeCiFailure.Validation;

namespace AnalyzeCiFailure;

internal static class Program
{
    private const string Usage = "Usage: AnalyzeCiFailure validate [--scripts-dir <directory>]";

    public static async Task<int> Main(string[] args)
    {
        if (args is not ["validate", .. var options])
        {
            await Console.Error.WriteLineAsync(Usage);
            return 2;
        }

        var workingDirectory = Directory.GetCurrentDirectory();

        // The publication job runs from the repository root, where the persistence and comment
        // helpers live under .github/workflows. Tests run from a scratch workspace and pass the
        // repository's scripts directory explicitly.
        var scriptsDirectory = Path.Combine(workingDirectory, ".github", "workflows");
        if (options is ["--scripts-dir", var scriptsDirectoryOption])
        {
            scriptsDirectory = scriptsDirectoryOption;
        }
        else if (options.Length != 0)
        {
            await Console.Error.WriteLineAsync(Usage);
            return 2;
        }

        // The analysis JSON and cause files ship in the `ci-analysis-output` artifact, which the
        // caller's `download-analysis` step unpacks. They are not under the working directory's
        // trusted data, so the location must be supplied rather than derived.
        var analysisDirectory = Environment.GetEnvironmentVariable("ANALYSIS_DIR");
        if (string.IsNullOrEmpty(analysisDirectory))
        {
            Console.WriteLine("::error::ANALYSIS_DIR is required (download-analysis step missing?)");
            return 1;
        }

        var paths = new ValidationPaths(
            Path.GetFullPath(analysisDirectory, workingDirectory),
            workingDirectory,
            Path.GetFullPath(scriptsDirectory, workingDirectory));

        return await AnalysisValidator.RunAsync(paths, Console.Out);
    }
}
