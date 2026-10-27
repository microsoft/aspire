// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Templating;

internal sealed record TemplateSource(string Path, string? Repository, string? RepositoryRef)
{
    internal static string GetRepositoryRelativePath(string path, string basePath)
    {
        var relativePath = System.IO.Path.GetRelativePath(basePath, System.IO.Path.GetFullPath(path, basePath));
        if (System.IO.Path.IsPathRooted(path) || relativePath == ".." ||
            relativePath.StartsWith($"..{System.IO.Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new TemplateDiscoveryException(CliExitCodes.InvalidCommand, Resources.NewCommandStrings.TemplateRepositoryPathInvalid);
        }
        return relativePath;
    }
}
