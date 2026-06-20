// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Projects;

internal static class AppHostProjectUtils
{
    internal static bool IsLikelyAppHost(FileInfo projectFile)
    {
        var fileNameSuggestsAppHost = projectFile.Name.EndsWith("AppHost.csproj", StringComparison.OrdinalIgnoreCase);
        var folderContainsAppHostCSharpFile = projectFile.Directory?
            .EnumerateFiles("*", SearchOption.TopDirectoryOnly)
            .Any(f => f.Name.Equals("AppHost.cs", StringComparison.OrdinalIgnoreCase)) ?? false;

        return fileNameSuggestsAppHost || folderContainsAppHostCSharpFile;
    }
}
