// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Utils;

internal static class ProjectPathResolver
{
    /// <summary>
    /// Resolves a user-supplied project path. A directory containing exactly one <c>.csproj</c> resolves
    /// to that project file; anything else is returned unchanged.
    /// </summary>
    /// <remarks>
    /// Unresolvable paths are returned unchanged so callers can report a contextual error at their
    /// validation boundary.
    /// </remarks>
    public static string ResolveProjectPath(string path) =>
        ResolveProjectPath(path, includeAllDotnetProjectTypes: false);

    /// <summary>
    /// Resolves directories using either the legacy C#-only policy or all supported .NET project types.
    /// </summary>
    public static string ResolveProjectPath(string path, bool includeAllDotnetProjectTypes)
    {
        if (Directory.Exists(path))
        {
            var projectFiles = Directory.EnumerateFiles(path, "*", new EnumerationOptions
                {
                    MatchCasing = MatchCasing.CaseInsensitive,
                    RecurseSubdirectories = false,
                    IgnoreInaccessible = true
                })
                .Where(file => includeAllDotnetProjectTypes
                    ? IsSupportedDotnetProjectFile(file)
                    : file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                .Take(2)
                .ToArray();

            if (projectFiles.Length != 1)
            {
                return path;
            }

            return Path.GetFullPath(projectFiles[0]);
        }

        return path;
    }

    /// <summary>
    /// Determines whether a path names a supported C#, F#, or Visual Basic project file, not a file-based app.
    /// </summary>
    public static bool IsSupportedDotnetProjectFile(string path) =>
        path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".vbproj", StringComparison.OrdinalIgnoreCase);
}
