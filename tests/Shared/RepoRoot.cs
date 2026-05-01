// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

public static class TestRepoRoot
{
    public static DirectoryInfo Directory { get; } = FindRepoRoot();

    public static string Path => Directory.FullName;

    private static DirectoryInfo FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(System.IO.Path.Combine(directory.FullName, "Aspire.slnx")))
            {
                return directory;
            }
            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not find repository root (looking for Aspire.slnx).");
    }
}
