// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.DotNet.XUnitExtensions;

namespace Aspire.Cli.Scripts.Tests;

/// <summary>
/// Skips tests if the GitHub CLI (gh) is not available.
/// PR-related tests require gh CLI to query GitHub API and download artifacts.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public class RequiresGHCliAttribute : Attribute, ITraitAttribute
{
    public IReadOnlyCollection<KeyValuePair<string, string>> GetTraits()
    {
        // Check if gh CLI is available on PATH
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var pathSeparator = OperatingSystem.IsWindows() ? ';' : ':';
        var paths = pathEnv.Split(pathSeparator, StringSplitOptions.RemoveEmptyEntries);
        
        var ghExecutable = OperatingSystem.IsWindows() ? "gh.exe" : "gh";
        var ghFound = paths.Any(p =>
        {
            try
            {
                var fullPath = Path.Combine(p, ghExecutable);
                return File.Exists(fullPath);
            }
            catch
            {
                return false;
            }
        });

        if (!ghFound)
        {
            return [new KeyValuePair<string, string>(XunitConstants.Category, "failing")];
        }

        return [];
    }
}
