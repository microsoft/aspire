// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.Configuration;

namespace Aspire.Cli.Telemetry;

/// <summary>
/// Detects CI environments by checking for known CI system environment variables.
/// </summary>
internal sealed class CIEnvironmentDetector : ICIEnvironmentDetector
{
    private readonly IConfiguration _configuration;

    /// <summary>
    /// Boolean environment variables that indicate a CI environment when set to "true" or "1".
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

    public CIEnvironmentDetector(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    /// <inheritdoc />
    public bool IsCIEnvironment()
    {
        // Check boolean environment variables - must be set to "true" or "1"
        foreach (var varName in s_booleanVars)
        {
            if (_configuration.GetBool(varName, defaultValue: false))
            {
                return true;
            }
        }

        // AWS CodeBuild - both variables must be present
        if (!string.IsNullOrEmpty(_configuration["CODEBUILD_BUILD_ID"]) &&
            !string.IsNullOrEmpty(_configuration["AWS_REGION"]))
        {
            return true;
        }

        // Jenkins - both variables must be present
        if (!string.IsNullOrEmpty(_configuration["BUILD_ID"]) &&
            !string.IsNullOrEmpty(_configuration["BUILD_URL"]))
        {
            return true;
        }

        // Google Cloud Build - both variables must be present
        if (!string.IsNullOrEmpty(_configuration["BUILD_ID"]) &&
            !string.IsNullOrEmpty(_configuration["PROJECT_ID"]))
        {
            return true;
        }

        // Check presence-only variables - just need to be set (any non-empty value)
        foreach (var varName in s_presenceVars)
        {
            if (!string.IsNullOrEmpty(_configuration[varName]))
            {
                return true;
            }
        }

        return false;
    }
}
