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
    /// Ambiguous or missing project files are deliberately passed through rather than throwing, so the
    /// error surfaces later as a resource start failure with a message naming the resource.
    /// </remarks>
    public static string ResolveProjectPath(string path)
    {
        if (Directory.Exists(path))
        {
            var projectFiles = Directory.GetFiles(path, "*.csproj", new EnumerationOptions
            {
                MatchCasing = MatchCasing.CaseInsensitive,
                RecurseSubdirectories = false,
                IgnoreInaccessible = true
            });

            if (projectFiles.Length != 1)
            {
                return path;
            }

            return Path.GetFullPath(projectFiles[0]);
        }

        return path;
    }
}
