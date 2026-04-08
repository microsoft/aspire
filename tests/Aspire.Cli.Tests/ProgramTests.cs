// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Utils;

namespace Aspire.Cli.Tests;

public class ProgramTests
{
    [Fact]
    public void ParseLogFileOption_ReturnsNull_WhenArgsAreNull()
    {
        var result = Program.ParseLogFileOption(null);

        Assert.Null(result);
    }

    [Fact]
    public void ParseLogFileOption_ReturnsValue_WhenOptionAppearsBeforeDelimiter()
    {
        var result = Program.ParseLogFileOption(["run", "--log-file", "cli.log", "--", "--log-file", "app.log"]);

        Assert.Equal("cli.log", result);
    }

    [Fact]
    public void ParseLogFileOption_IgnoresValue_WhenOptionAppearsAfterDelimiter()
    {
        var result = Program.ParseLogFileOption(["run", "--", "--log-file", "app.log"]);

        Assert.Null(result);
    }

    [Fact]
    public void GetInstallRootDirectory_FallsBackToHome_WhenProcessIsNotAspireCli()
    {
        // The test runner is not named "aspire", so it should fall back to ~/.aspire
        var result = Program.GetInstallRootDirectory();
        var expected = CliPathHelper.GetAspireHomeDirectory();

        Assert.Equal(expected, result.FullName);
    }

    [Fact]
    public void GetInstallRootDirectory_StableLayout_ReturnsParentOfBinDir()
    {
        if (OperatingSystem.IsWindows())
        {
            var result = Program.GetInstallRootDirectory(@"C:\Users\test\.aspire\bin\aspire.exe");
            Assert.Equal(@"C:\Users\test\.aspire", result.FullName);
        }
        else
        {
            var result = Program.GetInstallRootDirectory("/home/test/.aspire/bin/aspire");
            Assert.Equal("/home/test/.aspire", result.FullName);
        }
    }

    [Fact]
    public void GetInstallRootDirectory_DogfoodFlatLayout_ReturnsCliDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            var result = Program.GetInstallRootDirectory(@"C:\Users\test\.aspire\dogfood\pr-1234\aspire.exe");
            Assert.Equal(@"C:\Users\test\.aspire\dogfood\pr-1234", result.FullName);
        }
        else
        {
            var result = Program.GetInstallRootDirectory("/home/test/.aspire/dogfood/pr-1234/aspire");
            Assert.Equal("/home/test/.aspire/dogfood/pr-1234", result.FullName);
        }
    }

    [Fact]
    public void GetInstallRootDirectory_NullProcessPath_FallsBackToHome()
    {
        var result = Program.GetInstallRootDirectory(null);
        var expected = CliPathHelper.GetAspireHomeDirectory();

        Assert.Equal(expected, result.FullName);
    }

    [Fact]
    public void GetInstallRootDirectory_NonAspireProcess_FallsBackToHome()
    {
        var result = Program.GetInstallRootDirectory("/usr/local/share/dotnet/dotnet");
        var expected = CliPathHelper.GetAspireHomeDirectory();

        Assert.Equal(expected, result.FullName);
    }
}
