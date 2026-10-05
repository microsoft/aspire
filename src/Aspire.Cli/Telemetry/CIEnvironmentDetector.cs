// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Telemetry;

/// <summary>
/// Detects CI environments by checking for known CI system environment variables.
/// </summary>
internal sealed class CIEnvironmentDetector : ICIEnvironmentDetector
{
    private readonly IEnvironment _environment;

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

    public CIEnvironmentDetector(IEnvironment environment)
    {
        // Like dotnet, inspect the process environment rather than allowing CLI settings to override CI detection.
        _environment = environment;
    }

    /// <inheritdoc />
    public bool IsCIEnvironment()
    {
        foreach (var varName in s_booleanVars)
        {
            if (IsTrue(_environment.GetEnvironmentVariable(varName)))
            {
                return true;
            }
        }

        // AWS CodeBuild - both variables must be present
        if (!string.IsNullOrEmpty(_environment.GetEnvironmentVariable("CODEBUILD_BUILD_ID")) &&
            !string.IsNullOrEmpty(_environment.GetEnvironmentVariable("AWS_REGION")))
        {
            return true;
        }

        // Jenkins - both variables must be present
        if (!string.IsNullOrEmpty(_environment.GetEnvironmentVariable("BUILD_ID")) &&
            !string.IsNullOrEmpty(_environment.GetEnvironmentVariable("BUILD_URL")))
        {
            return true;
        }

        // Google Cloud Build - both variables must be present
        if (!string.IsNullOrEmpty(_environment.GetEnvironmentVariable("BUILD_ID")) &&
            !string.IsNullOrEmpty(_environment.GetEnvironmentVariable("PROJECT_ID")))
        {
            return true;
        }

        // Check presence-only variables - just need to be set (any non-empty value)
        foreach (var varName in s_presenceVars)
        {
            if (!string.IsNullOrEmpty(_environment.GetEnvironmentVariable(varName)))
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
        // Keep this separate from GetBool, whose broader numeric/whitespace parsing is used by CLI settings.
        // https://github.com/dotnet/sdk/blob/main/src/Cli/Microsoft.DotNet.Cli.CoreUtils/EnvironmentVariableParser.cs
        return value is "1" ||
            string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "on", StringComparison.OrdinalIgnoreCase);
    }
}
