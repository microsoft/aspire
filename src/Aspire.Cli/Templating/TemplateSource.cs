// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Templating;

internal sealed record TemplateSource(string Path, string? Repository, string? RepositoryRef)
{
    internal static string GetRepositoryRelativePath(string path, string basePath)
    {
        // Reject logical escapes before GetFullPath can clamp ".." at a volume root or
        // normalize a path such as "../project/templates" back into a directory named project.
        var depth = 0;
        foreach (var segment in path.Split(
            [System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == "..")
            {
                if (depth == 0)
                {
                    throw new TemplateDiscoveryException(CliExitCodes.InvalidCommand, Resources.NewCommandStrings.TemplateRepositoryPathInvalid);
                }
                depth--;
            }
            else if (segment != ".")
            {
                depth++;
            }
        }

        var relativePath = System.IO.Path.GetRelativePath(basePath, System.IO.Path.GetFullPath(path, basePath));
        if (System.IO.Path.IsPathRooted(path) || relativePath == ".." ||
            relativePath.StartsWith($"..{System.IO.Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new TemplateDiscoveryException(CliExitCodes.InvalidCommand, Resources.NewCommandStrings.TemplateRepositoryPathInvalid);
        }
        return relativePath;
    }
}
