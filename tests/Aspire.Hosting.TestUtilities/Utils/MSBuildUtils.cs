// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Tests;

public class MSBuildUtils
{
    public static string GetRepoRoot()
    {
        string directory = AppContext.BaseDirectory;

        // To support git worktrees, check for either a directory or a file named ".git"
        while (directory != null && !Directory.Exists(Path.Combine(directory, ".git")) && !File.Exists(Path.Combine(directory, ".git")))
        {
            directory = Directory.GetParent(directory)!.FullName;
        }

        return directory!;
    }
}
