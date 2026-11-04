// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Shared.Telemetry;

/// <summary>
/// Detects CI environments by checking for known CI system environment variables.
/// </summary>
internal static class CIEnvironmentDetector
{
    /// <summary>
    /// Boolean environment variables that indicate a CI environment when set to "true", "1", "yes", or "on".
    /// </summary>
    private static readonly string[] s_booleanVars =
    [
        "TF_BUILD",        // Azure Pipelines
        "GITHUB_ACTIONS",  // GitHub Actions
        "APPVEYOR",        // AppVeyor
        "CI",              // General CI flag (supported by AzDo, GitHub, GitLab, AppVeyor, Travis CI, CircleCI)
        "TRAVIS",          // Travis CI
        "CIRCLECI"         // CircleCI
    ];

    /// <summary>
    /// Environment variables that indicate a CI environment when present with any non-empty value.
    /// </summary>
    private static readonly string[] s_presenceVars =
    [
        "TEAMCITY_VERSION",  // TeamCity
        "JB_SPACE_API_URL"   // JetBrains Space
    ];

    public static bool IsCIEnvironment(Func<string, string?> getEnvironmentVariable)
    {
        foreach (var varName in s_booleanVars)
        {
            if (IsTrue(getEnvironmentVariable(varName)))
            {
                return true;
            }
        }

        // AWS CodeBuild - both variables must be present
        if (!string.IsNullOrEmpty(getEnvironmentVariable("CODEBUILD_BUILD_ID")) &&
            !string.IsNullOrEmpty(getEnvironmentVariable("AWS_REGION")))
        {
            return true;
        }

        // Jenkins - both variables must be present
        if (!string.IsNullOrEmpty(getEnvironmentVariable("BUILD_ID")) &&
            !string.IsNullOrEmpty(getEnvironmentVariable("BUILD_URL")))
        {
            return true;
        }

        // Google Cloud Build - both variables must be present
        if (!string.IsNullOrEmpty(getEnvironmentVariable("BUILD_ID")) &&
            !string.IsNullOrEmpty(getEnvironmentVariable("PROJECT_ID")))
        {
            return true;
        }

        // Check presence-only variables - just need to be set (any non-empty value)
        foreach (var varName in s_presenceVars)
        {
            if (!string.IsNullOrEmpty(getEnvironmentVariable(varName)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsTrue(string? value)
    {
        // Match dotnet's EnvironmentVariableParser.ParseBool(value, defaultValue: false):
        // "YES" is true, but " true ", "01", and other nonzero integers are false.
        // Keep this separate from the CLI's GetBool, whose broader numeric/whitespace parsing is used by settings.
        // https://github.com/dotnet/sdk/blob/main/src/Cli/Microsoft.DotNet.Cli.CoreUtils/EnvironmentVariableParser.cs
        return value is "1" ||
            string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "on", StringComparison.OrdinalIgnoreCase);
    }
}
