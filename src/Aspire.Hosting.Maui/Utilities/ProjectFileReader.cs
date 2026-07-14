// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace Aspire.Hosting.Maui.Utilities;

/// <summary>
/// Provides utilities for reading and parsing MAUI project files.
/// </summary>
internal static class ProjectFileReader
{
    // Cache results per project path to avoid repeated MSBuild invocations
    private static readonly ConcurrentDictionary<string, Dictionary<string, string?>> s_projectCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the target framework matching the specified platform from the project file.
    /// </summary>
    /// <param name="projectPath">The path to the project file to parse.</param>
    /// <param name="platformIdentifier">The platform identifier to search for (e.g., "windows", "android", "ios", "maccatalyst").</param>
    /// <returns>The matching TFM if found, otherwise null.</returns>
    /// <remarks>
    /// This method uses MSBuild to evaluate the project and retrieve TargetFramework and TargetFrameworks properties.
    /// It searches for a target framework containing the specified platform identifier (case-insensitive) and returns the first match.
    /// Results are cached per project path to avoid repeated MSBuild evaluations.
    /// </remarks>
    public static string? GetPlatformTargetFramework(string projectPath, string platformIdentifier)
    {
        // Get or create cache entry for this project
        var platformCache = s_projectCache.GetOrAdd(projectPath, _ => new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase));
        
        // Check cache first
        lock (platformCache)
        {
            if (platformCache.TryGetValue(platformIdentifier, out var cachedTfm))
            {
                return cachedTfm;
            }
        }

        // Not cached, evaluate the project
        var tfm = EvaluateProjectForPlatform(projectPath, platformIdentifier);
        
        // Cache the result
        lock (platformCache)
        {
            platformCache[platformIdentifier] = tfm;
        }
        
        return tfm;
    }

    private static string? EvaluateProjectForPlatform(string projectPath, string platformIdentifier)
    {
        try
        {
            // Use dotnet msbuild to get both TargetFramework and TargetFrameworks properties
            var startInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"msbuild \"{projectPath}\" -getProperty:{KnownMauiMSBuildProperties.TargetFramework},{KnownMauiMSBuildProperties.TargetFrameworks} -nologo",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();

            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
            {
                return null;
            }

            // Parse the JSON output from msbuild -getProperty
            var jsonDoc = JsonDocument.Parse(output);
            var properties = jsonDoc.RootElement.GetProperty("Properties");

            // Check both TargetFramework and TargetFrameworks properties
            var targetFrameworksValue = string.Empty;
            
            if (properties.TryGetProperty(KnownMauiMSBuildProperties.TargetFrameworks, out var targetFrameworks))
            {
                targetFrameworksValue = targetFrameworks.GetString() ?? string.Empty;
            }
            
            if (string.IsNullOrWhiteSpace(targetFrameworksValue) && 
                properties.TryGetProperty(KnownMauiMSBuildProperties.TargetFramework, out var targetFramework))
            {
                targetFrameworksValue = targetFramework.GetString() ?? string.Empty;
            }

            if (string.IsNullOrWhiteSpace(targetFrameworksValue))
            {
                return null;
            }

            // Split by semicolon and find the first TFM containing the platform identifier
            var platformTfm = targetFrameworksValue.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault(tfm => tfm.Contains($"-{platformIdentifier}", StringComparison.OrdinalIgnoreCase));

            return platformTfm;
        }
        catch
        {
            // If we can't evaluate the project, return null to indicate unknown
            return null;
        }
    }
}
