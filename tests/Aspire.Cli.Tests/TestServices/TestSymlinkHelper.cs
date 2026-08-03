// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Tests.TestServices;

internal static class TestSymlinkHelper
{
    public static void TryCreateSymlink(string linkPath, string targetPath, bool isDirectory = true)
    {
        try
        {
            if (isDirectory)
            {
                Directory.CreateSymbolicLink(linkPath, targetPath);
            }
            else
            {
                File.CreateSymbolicLink(linkPath, targetPath);
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            // Creating symlinks requires additional privileges in some environments. Skip
            // cleanly rather than failing path-normalization tests for an environment reason.
            Assert.Skip($"Cannot create symbolic links in this environment: {ex.Message}");
        }
        catch (IOException ex)
        {
            Assert.Skip($"Symbolic link creation failed in this environment: {ex.Message}");
        }
    }
}
