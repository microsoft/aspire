// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Xunit;

namespace Aspire.Cli.EndToEnd.Tests.Helpers;

internal static class GitIgnoreAssertions
{
    public static void AssertContainsEntry(string projectRoot, string entry)
    {
        var gitIgnorePath = Path.Combine(projectRoot, ".gitignore");
        Assert.True(File.Exists(gitIgnorePath), $"Expected generated .gitignore at {gitIgnorePath}");

        var gitIgnoreLines = File.ReadAllLines(gitIgnorePath);
        Assert.Contains(entry, gitIgnoreLines);
    }
}
