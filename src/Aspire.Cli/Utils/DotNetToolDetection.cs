// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Utils;

/// <summary>
/// Detects whether the Aspire CLI is running from a NativeAOT .NET tool installation.
/// </summary>
internal static class DotNetToolDetection
{
    private static readonly AsyncLocal<string?> s_processPathOverride = new();
    private static readonly string[] s_toolPackageRuntimeIdentifiers =
    [
        "win-x64",
        "win-arm64",
        "linux-x64",
        "linux-arm64",
        "linux-musl-x64",
        "osx-x64",
        "osx-arm64"
    ];

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

        if (IsGlobalDotNetToolShimPath(parts))
        {
            return true;
        }

        for (var i = 0; i < parts.Length; i++)
        {
            if (!string.Equals(parts[i], ".store", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (IsDotNetToolStorePackagePath(parts, i))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsGlobalDotNetToolShimPath(string[] parts)
    {
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts.Length - i == 3 &&
                string.Equals(parts[i], ".dotnet", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(parts[i + 1], "tools", StringComparison.OrdinalIgnoreCase) &&
                IsAspireExecutable(parts[i + 2]))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsDotNetToolStorePackagePath(string[] parts, int storeIndex)
    {
        const int storeLayoutPartCount = 9;

        if (parts.Length - storeIndex != storeLayoutPartCount)
        {
            return false;
        }

        var toolPackageId = parts[storeIndex + 1];
        var toolPackageVersion = parts[storeIndex + 2];
        var implementationPackageId = parts[storeIndex + 3];
        var implementationPackageVersion = parts[storeIndex + 4];
        var toolsSegment = parts[storeIndex + 5];
        var targetFramework = parts[storeIndex + 6];
        var toolRid = parts[storeIndex + 7];
        var executable = parts[storeIndex + 8];

        return string.Equals(toolPackageId, "aspire.cli", StringComparison.OrdinalIgnoreCase)
            && IsAspireCliPackageId(implementationPackageId, toolRid)
            && string.Equals(toolPackageVersion, implementationPackageVersion, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(toolPackageVersion)
            && string.Equals(toolsSegment, "tools", StringComparison.OrdinalIgnoreCase)
            && IsSupportedToolTargetFramework(targetFramework)
            && IsSupportedToolRuntimeIdentifier(toolRid)
            && IsAspireExecutable(executable);
    }

    private static bool IsAspireCliPackageId(string packageId, string toolRid)
    {
        if (string.Equals(packageId, "aspire.cli", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        const string ridSpecificPackagePrefix = "aspire.cli.";
        if (!packageId.StartsWith(ridSpecificPackagePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var packageRuntimeIdentifier = packageId[ridSpecificPackagePrefix.Length..];
        return IsSupportedToolRuntimeIdentifier(packageRuntimeIdentifier) &&
            string.Equals(packageRuntimeIdentifier, toolRid, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSupportedToolTargetFramework(string targetFramework)
    {
        return string.Equals(targetFramework, "any", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(targetFramework, "net10.0", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSupportedToolRuntimeIdentifier(string runtimeIdentifier)
    {
        return s_toolPackageRuntimeIdentifiers.Contains(runtimeIdentifier, StringComparer.OrdinalIgnoreCase) ||
            string.Equals(runtimeIdentifier, "any", StringComparison.OrdinalIgnoreCase);
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
