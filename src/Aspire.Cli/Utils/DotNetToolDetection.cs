// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Utils;

/// <summary>
/// Detects whether the Aspire CLI is running as a .NET tool installation.
/// </summary>
internal static class DotNetToolDetection
{
    /// <summary>
    /// Determines whether the CLI binary at the given process path was installed as a .NET tool.
    /// </summary>
    /// <param name="processPath">The value of <see cref="Environment.ProcessPath"/>, or <see langword="null"/> if unavailable.</param>
    /// <returns>
    /// <see langword="true"/> if the process path indicates a .NET tool installation;
    /// <see langword="false"/> if it indicates a standalone native binary or the path cannot be determined.
    /// </returns>
    /// <remarks>
    /// Detection covers two .NET tool runner modes:
    /// <list type="bullet">
    /// <item><b>Managed tools</b> (Runner="dotnet"): the dotnet host runs the tool DLL, so
    ///   <paramref name="processPath"/> is the <c>dotnet</c> executable.</item>
    /// <item><b>NativeAOT tools</b> (Runner="executable"): the binary executes directly from the
    ///   <c>.store</c> directory, e.g.
    ///   <c>~/.dotnet/tools/.store/aspire.cli/10.0.0/osx-arm64/tools/net10.0/osx-arm64/aspire</c>.</item>
    /// </list>
    /// </remarks>
    internal static bool IsRunningAsDotNetTool(string? processPath)
    {
        if (string.IsNullOrEmpty(processPath))
        {
            return false;
        }

        // Managed tools: ProcessPath is "dotnet" or "dotnet.exe"
        var fileName = Path.GetFileNameWithoutExtension(processPath);
        if (string.Equals(fileName, "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // NativeAOT tools: ProcessPath is inside a .store/ directory hierarchy.
        // The .store directory is created by the dotnet tool install SDK infrastructure.
        var directory = Path.GetDirectoryName(processPath);
        while (!string.IsNullOrEmpty(directory))
        {
            if (string.Equals(Path.GetFileName(directory), ".store", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var parent = Path.GetDirectoryName(directory);
            if (parent == directory)
            {
                break;
            }

            directory = parent;
        }

        return false;
    }
}
