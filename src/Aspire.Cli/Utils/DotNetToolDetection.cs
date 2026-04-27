// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Utils;

/// <summary>
/// Detects whether the Aspire CLI is running from a NativeAOT .NET tool installation.
/// </summary>
internal static class DotNetToolDetection
{
    private static readonly AsyncLocal<string?> s_processPathOverride = new();

    internal static bool IsRunningAsDotNetTool()
    {
        return IsRunningAsDotNetTool(s_processPathOverride.Value ?? Environment.ProcessPath);
    }

    internal static bool IsRunningAsDotNetTool(string? processPath)
    {
        if (string.IsNullOrWhiteSpace(processPath))
        {
            return false;
        }

        var parts = processPath
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries);

        for (var i = 0; i < parts.Length; i++)
        {
            if (!string.Equals(parts[i], ".store", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (parts.Length - i != 8)
            {
                continue;
            }

            var packageId = parts[i + 1];
            var version = parts[i + 2];
            var packageRid = parts[i + 3];
            var toolsSegment = parts[i + 4];
            var targetFramework = parts[i + 5];
            var toolRid = parts[i + 6];
            var executable = parts[i + 7];

            return string.Equals(packageId, "aspire.cli", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(version)
                && !string.IsNullOrWhiteSpace(packageRid)
                && string.Equals(packageRid, toolRid, StringComparison.OrdinalIgnoreCase)
                && string.Equals(toolsSegment, "tools", StringComparison.OrdinalIgnoreCase)
                && string.Equals(targetFramework, "net10.0", StringComparison.OrdinalIgnoreCase)
                && IsAspireExecutable(executable);
        }

        return false;
    }

    internal static IDisposable UseProcessPathForTesting(string? processPath)
    {
        var previousValue = s_processPathOverride.Value;
        s_processPathOverride.Value = processPath;
        return new ProcessPathOverrideScope(previousValue);
    }

    private static bool IsAspireExecutable(string executable)
    {
        return string.Equals(executable, "aspire", StringComparison.OrdinalIgnoreCase)
            || string.Equals(executable, "aspire.exe", StringComparison.OrdinalIgnoreCase);
    }

    private sealed class ProcessPathOverrideScope(string? previousValue) : IDisposable
    {
        public void Dispose()
        {
            s_processPathOverride.Value = previousValue;
        }
    }
}
