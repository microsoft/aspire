// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.Configuration;
using Spectre.Console;

namespace Aspire.Cli.Utils;

/// <summary>
/// Provides information about the CLI host environment capabilities.
/// </summary>
internal interface ICliHostEnvironment
{
    /// <summary>
    /// Gets whether the host supports interactive input (e.g., prompts, user input).
    /// </summary>
    bool SupportsInteractiveInput { get; }

    /// <summary>
    /// Gets whether the host supports interactive output (e.g., spinners, progress bars).
    /// </summary>
    bool SupportsInteractiveOutput { get; }

    /// <summary>
    /// Gets whether the host supports colors and ANSI codes.
    /// </summary>
    bool SupportsAnsi { get; }
}

/// <summary>
/// Default implementation that detects CLI host environment capabilities from configuration.
/// </summary>
internal sealed class CliHostEnvironment : ICliHostEnvironment
{
    // Common CI environment variables
    // https://github.com/watson/ci-info/blob/master/vendors.json
    private static readonly string[] s_ciEnvironmentVariables =
    [
        "CI", // Generic CI indicator
        "GITHUB_ACTIONS",
        "AZURE_PIPELINES",
        "TF_BUILD", // Azure Pipelines alternative
        "JENKINS_URL",
        "GITLAB_CI",
        "CIRCLECI",
        "TRAVIS",
        "BUILDKITE",
        "APPVEYOR",
        "TEAMCITY_VERSION",
        "BITBUCKET_BUILD_NUMBER",
        "CODEBUILD_BUILD_ID", // AWS CodeBuild
    ];

    /// <summary>
    /// Gets whether the host supports interactive input (e.g., prompts, user input).
    /// </summary>
    public bool SupportsInteractiveInput { get; }

    /// <summary>
    /// Gets whether the host supports interactive output (e.g., spinners, progress bars).
    /// </summary>
    public bool SupportsInteractiveOutput { get; }

    /// <summary>
    /// Gets whether the host supports colors and ANSI codes.
    /// </summary>
    public bool SupportsAnsi { get; }

    public CliHostEnvironment(IConfiguration configuration, bool nonInteractive)
    {
        // If --non-interactive is explicitly set, disable interactive input and output.
        // This takes precedence over all other settings including ASPIRE_PLAYGROUND.
        if (nonInteractive)
        {
            SupportsInteractiveInput = false;
            SupportsInteractiveOutput = false;
            SupportsAnsi = DetectAnsiSupport(configuration);
        }
        // Check if ASPIRE_PLAYGROUND is set to force interactive mode
        else if (IsPlaygroundMode(configuration))
        {
            SupportsInteractiveInput = true;
            SupportsInteractiveOutput = true;
            SupportsAnsi = true;
        }
        else
        {
            SupportsInteractiveInput = DetectInteractiveInput(configuration);
            SupportsInteractiveOutput = DetectInteractiveOutput(configuration);
            SupportsAnsi = DetectAnsiSupport(configuration);
        }
    }

    private static bool DetectAnsiSupport(IConfiguration configuration)
    {
        if (!TryDetectAnsiSupportConfiguration(configuration, out var supportsAnsi))
        {
            // If there is no explicit configuration to enable or disable ANSI support, attempt to detect it.
            // This is required because some terminals don't support ANSI output, e.g. https://github.com/microsoft/aspire/issues/13737
            var capabilities = AnsiCapabilities.Create(TextWriter.Null);
            supportsAnsi = capabilities.Ansi;
        }

        return supportsAnsi;
    }

    private static bool DetectInteractiveInput(IConfiguration configuration)
    {
        // Check if explicitly disabled via configuration
        var nonInteractive = configuration["ASPIRE_NON_INTERACTIVE"];
        if (!string.IsNullOrEmpty(nonInteractive) &&
            (nonInteractive.Equals("true", StringComparison.OrdinalIgnoreCase) ||
             nonInteractive.Equals("1", StringComparison.Ordinal)))
        {
            return false;
        }

        // Check if running in CI environment (no interactive input possible)
        if (IsCI(configuration))
        {
            return false;
        }

        return true;
    }

    private static bool DetectInteractiveOutput(IConfiguration configuration)
    {
        // Check if explicitly disabled via configuration
        var nonInteractive = configuration["ASPIRE_NON_INTERACTIVE"];
        if (!string.IsNullOrEmpty(nonInteractive) &&
            (nonInteractive.Equals("true", StringComparison.OrdinalIgnoreCase) ||
             nonInteractive.Equals("1", StringComparison.Ordinal)))
        {
            return false;
        }

        // Check if running in CI environment (spinners pollute logs)
        if (IsCI(configuration))
        {
            return false;
        }

        // Verify the console handles are valid. Returning false here is safe —
        // all consumers gracefully degrade to plain text output (no spinners,
        // no banner, no progress bars) so the command still works.
        return HasValidConsoleHandles();
    }

    private static bool HasValidConsoleHandles()
    {
        // On Windows, processes spawned without a console (e.g., via PowerShell's
        // Invoke-Expression) have invalid output handles. Probing CursorVisible
        // is a reliable way to detect this — it fails fast if there's no console,
        // and it exercises the same handle that interactive UI components need.
        if (OperatingSystem.IsWindows())
        {
            try
            {
                _ = Console.CursorVisible;
            }
            catch (IOException)
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryDetectAnsiSupportConfiguration(IConfiguration configuration, out bool supportsAnsi)
    {
        // Check for ASPIRE_ANSI_PASS_THRU to force ANSI even when redirected
        if (IsAnsiPassThruEnabled(configuration))
        {
            supportsAnsi = true;
            return true;
        }

        // Check for NO_COLOR to explicitly disable ANSI output.
        // If neither override is set, return false to let the caller fall back to Spectre detection.
        var noColor = configuration["NO_COLOR"];
        if (!string.IsNullOrEmpty(noColor))
        {
            supportsAnsi = false;
            return true;
        }

        supportsAnsi = default;
        return false;
    }

    private static bool IsAnsiPassThruEnabled(IConfiguration configuration)
    {
        var ansiPassThru = configuration["ASPIRE_ANSI_PASS_THRU"];
        return !string.IsNullOrEmpty(ansiPassThru) &&
               (ansiPassThru.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                ansiPassThru.Equals("1", StringComparison.Ordinal));
    }

    private static bool IsCI(IConfiguration configuration)
    {
        foreach (var envVar in s_ciEnvironmentVariables)
        {
            var value = configuration[envVar];
            if (!string.IsNullOrEmpty(value))
            {
                // For CI variable, only return true if it's "true" or "1"
                if (envVar == "CI")
                {
                    return value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                           value.Equals("1", StringComparison.Ordinal);
                }
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Gets whether the ASPIRE_PLAYGROUND environment variable is set to force interactive mode.
    /// </summary>
    internal static bool IsPlaygroundMode(IConfiguration configuration)
    {
        var playgroundMode = configuration["ASPIRE_PLAYGROUND"];
        return !string.IsNullOrEmpty(playgroundMode) &&
               playgroundMode.Equals("true", StringComparison.OrdinalIgnoreCase);
    }
}
