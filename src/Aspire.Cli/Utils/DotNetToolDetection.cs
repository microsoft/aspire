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
        return GetDotNetToolUpdateCommand() is not null;
    }

    internal static bool IsRunningAsDotNetTool(string? processPath)
    {
        return GetDotNetToolUpdateCommand(processPath) is not null;
    }

    internal static string? GetDotNetToolUpdateCommand()
    {
        return GetDotNetToolUpdateCommand(s_processPathOverride.Value ?? Environment.ProcessPath);
    }

    internal static string? GetDotNetToolUpdateCommand(string? processPath)
    {
        if (string.IsNullOrWhiteSpace(processPath))
        {
            return null;
        }

        var parts = processPath
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (IsGlobalDotNetToolShimPath(parts))
        {
            return GetGlobalDotNetToolUpdateCommand();
        }

        var storeIndex = GetDotNetToolStorePackagePathIndex(parts);
        if (storeIndex is not null)
        {
            if (IsGlobalDotNetToolStorePath(parts, storeIndex.Value))
            {
                return GetGlobalDotNetToolUpdateCommand();
            }

            if (TryGetToolPathFromStorePath(processPath, out var toolPath))
            {
                return GetToolPathDotNetToolUpdateCommand(toolPath);
            }

            return null;
        }

        if (HasSiblingDotNetToolStore(processPath) &&
            TryGetProcessDirectory(processPath, out var processDirectory))
        {
            return GetToolPathDotNetToolUpdateCommand(processDirectory);
        }

        return null;
    }

    private static bool ContainsDotNetToolStorePackagePath(string[] parts)
    {
        return GetDotNetToolStorePackagePathIndex(parts) is not null;
    }

    private static int? GetDotNetToolStorePackagePathIndex(string[] parts)
    {
        for (var i = 0; i < parts.Length; i++)
        {
            if (!string.Equals(parts[i], ".store", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (IsDotNetToolStorePackagePath(parts, i))
            {
                return i;
            }
        }

        return null;
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

    private static bool IsGlobalDotNetToolStorePath(string[] parts, int storeIndex)
    {
        return storeIndex >= 2 &&
            string.Equals(parts[storeIndex - 2], ".dotnet", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(parts[storeIndex - 1], "tools", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDotNetToolStorePackagePath(string[] parts, int storeIndex)
    {
        const int minimumStoreLayoutPartCount = 9;

        if (parts.Length - storeIndex < minimumStoreLayoutPartCount)
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
        var executable = parts[^1];

        return string.Equals(toolPackageId, "aspire.cli", StringComparison.OrdinalIgnoreCase)
            && IsAspireCliPackageId(implementationPackageId, toolRid)
            && string.Equals(toolPackageVersion, implementationPackageVersion, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(toolPackageVersion)
            && string.Equals(toolsSegment, "tools", StringComparison.OrdinalIgnoreCase)
            && IsSupportedToolTargetFramework(targetFramework)
            && IsSupportedToolRuntimeIdentifier(toolRid)
            && IsAspireExecutable(executable);
    }

    private static bool HasSiblingDotNetToolStore(string processPath)
    {
        if (!IsAspireExecutable(Path.GetFileName(processPath)))
        {
            return false;
        }

        var processDirectory = Path.GetDirectoryName(processPath);
        if (string.IsNullOrEmpty(processDirectory))
        {
            return false;
        }

        var storeDirectory = Path.Combine(processDirectory, ".store");
        if (!Directory.Exists(storeDirectory))
        {
            return false;
        }

        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = true
        };

        foreach (var candidatePath in Directory.EnumerateFiles(storeDirectory, "*", options))
        {
            if (!IsAspireExecutable(Path.GetFileName(candidatePath)))
            {
                continue;
            }

            var candidateParts = candidatePath
                .Replace('\\', '/')
                .Split('/', StringSplitOptions.RemoveEmptyEntries);

            if (ContainsDotNetToolStorePackagePath(candidateParts))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryGetProcessDirectory(string processPath, out string toolPath)
    {
        var processDirectory = Path.GetDirectoryName(NormalizeDirectorySeparators(processPath));
        if (string.IsNullOrEmpty(processDirectory))
        {
            toolPath = string.Empty;
            return false;
        }

        toolPath = processDirectory;
        return true;
    }

    private static bool TryGetToolPathFromStorePath(string processPath, out string toolPath)
    {
        var currentDirectory = Path.GetDirectoryName(NormalizeDirectorySeparators(processPath));
        while (!string.IsNullOrEmpty(currentDirectory))
        {
            if (string.Equals(Path.GetFileName(currentDirectory), ".store", StringComparison.OrdinalIgnoreCase))
            {
                var parentDirectory = Path.GetDirectoryName(currentDirectory);
                if (!string.IsNullOrEmpty(parentDirectory))
                {
                    toolPath = parentDirectory;
                    return true;
                }

                break;
            }

            currentDirectory = Path.GetDirectoryName(currentDirectory);
        }

        toolPath = string.Empty;
        return false;
    }

    private static string NormalizeDirectorySeparators(string path)
    {
        return path.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
    }

    private static string GetGlobalDotNetToolUpdateCommand()
    {
        return "dotnet tool update -g Aspire.Cli";
    }

    private static string GetToolPathDotNetToolUpdateCommand(string toolPath)
    {
        return $"dotnet tool update --tool-path {QuoteCommandArgument(toolPath)} Aspire.Cli";
    }

    private static string QuoteCommandArgument(string argument)
    {
        return argument.Any(char.IsWhiteSpace) || argument.Contains('"', StringComparison.Ordinal)
            ? $"\"{argument.Replace("\"", "\\\"", StringComparison.Ordinal)}\""
            : argument;
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
