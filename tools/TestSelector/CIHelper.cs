// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace TestSelector;

internal static class CIHelper
{
    /// <summary>
    /// Detects the CI environment based on environment variables.
    /// Returns "GitHub", "AzureDevOps", or "Local".
    /// </summary>
    public static string DetectCIEnvironment()
    {
        // GitHub Actions sets GITHUB_ACTIONS=true
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true")
        {
            return "GitHub";
        }

        // Azure DevOps sets TF_BUILD=True
        if (Environment.GetEnvironmentVariable("TF_BUILD") == "True")
        {
            return "AzureDevOps";
        }

        return "Local";
    }

    /// <summary>
    /// Writes an error message in a format appropriate for the CI environment.
    /// </summary>
    public static void WriteError(string environment, string message)
    {
        var formatted = environment switch
        {
            "GitHub" => $"::error::{message}",
            "AzureDevOps" => $"##vso[task.logissue type=error]{message}",
            _ => $"Error: {message}"
        };
        Console.Error.WriteLine(formatted);
    }

    /// <summary>
    /// Writes a warning message in a format appropriate for the CI environment.
    /// </summary>
    public static void WriteWarning(string environment, string message)
    {
        var formatted = environment switch
        {
            "GitHub" => $"::warning::{message}",
            "AzureDevOps" => $"##vso[task.logissue type=warning]{message}",
            _ => $"Warning: {message}"
        };
        Console.Error.WriteLine(formatted);
    }
}
