// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Uninstall;

namespace Aspire.Cli.Tests.Uninstall;

public class CliCleanupServiceTests
{
    [Theory]
    [InlineData("/usr/local/bin/aspire", "/usr/local/bin/aspire", true)]
    [InlineData("/usr/local/bin/aspire", "/usr/local/bin", true)]
    [InlineData("/usr/local/bin/aspire", "/usr/local", true)]
    [InlineData("/usr/local/binx/aspire", "/usr/local/bin", false)] // prefix match without separator must not count
    [InlineData("/usr/local/aspire", "/usr/local/bin", false)]
    [InlineData("/usr/local/bin", "/usr/local/bin/aspire", false)] // parent is not "under" child
    public void IsPathUnderTarget_ReturnsExpected_ForUnixPaths(string path, string target, bool expected)
    {
        // The helper underpins the running-CLI safety check: if the current
        // process path resolves under a cleanup target, the cleanup operation
        // must refuse to delete that target so the live binary is not removed.
        Assert.Equal(expected, CliCleanupService.IsPathUnderTarget(path, target));
    }

    [Theory]
    [InlineData(@"C:\Users\Foo\.aspire\bin\aspire.exe", @"C:\Users\Foo\.aspire\bin", true)]
    [InlineData(@"C:\Users\Foo\.aspire\bin\aspire.exe", @"c:\users\foo\.aspire\bin", true)] // Windows is case-insensitive
    [InlineData(@"C:\Users\Foo\.aspire\binx\aspire.exe", @"C:\Users\Foo\.aspire\bin", false)]
    public void IsPathUnderTarget_HandlesWindowsCaseAndSeparators(string path, string target, bool expected)
    {
        if (!OperatingSystem.IsWindows())
        {
            // The case-insensitive comparison branch is only exercised on Windows.
            return;
        }

        Assert.Equal(expected, CliCleanupService.IsPathUnderTarget(path, target));
    }
}
