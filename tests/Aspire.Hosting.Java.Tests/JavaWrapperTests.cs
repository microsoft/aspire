// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Shared;

namespace Aspire.Hosting.Java.Tests;

// JavaWrapper is compiled into both Aspire.Hosting.Java and Aspire.Cli; its rules are tested here once.
public class JavaWrapperTests
{
    [Fact]
    public void Find_UsesAnAncestorWrapperAtTheBuildRoot()
    {
        using var root = new TempJavaBuildRootDirectory();
        var module = root.CreateModule("catalog");

        var wrapperPath = JavaWrapper.Find(module, "mvnw", JavaWrapperTool.Maven);

        Assert.Equal(Path.Combine(root.RootPath, "mvnw"), wrapperPath);
    }

    [Fact]
    public void Find_PrefersTheWrapperBesideTheProject()
    {
        using var root = new TempJavaBuildRootDirectory();
        var module = root.CreateModule("catalog");
        File.WriteAllText(Path.Combine(module, "mvnw"), "");

        var wrapperPath = JavaWrapper.Find(module, "mvnw", JavaWrapperTool.Maven);

        Assert.Equal(Path.Combine(module, "mvnw"), wrapperPath);
    }

    [Fact]
    public void Find_IgnoresAnAncestorWrapperThatIsNotAtABuildRoot()
    {
        // A wrapper higher up the filesystem that is not beside a pom.xml belongs to something else.
        using var root = new TempJavaBuildRootDirectory();
        File.Delete(Path.Combine(root.RootPath, "pom.xml"));
        var module = Directory.CreateDirectory(Path.Combine(root.RootPath, "catalog")).FullName;

        Assert.Null(JavaWrapper.Find(module, "mvnw", JavaWrapperTool.Maven));
    }

    [Fact]
    public void Find_RecognisesAGradleSettingsFileAsTheBuildRoot()
    {
        using var root = new TempJavaBuildRootDirectory();
        File.Delete(Path.Combine(root.RootPath, "pom.xml"));
        File.Delete(Path.Combine(root.RootPath, "mvnw"));
        File.WriteAllText(Path.Combine(root.RootPath, "settings.gradle.kts"), "");
        File.WriteAllText(Path.Combine(root.RootPath, "gradlew"), "");
        var module = Directory.CreateDirectory(Path.Combine(root.RootPath, "api")).FullName;

        var wrapperPath = JavaWrapper.Find(module, "gradlew", JavaWrapperTool.Gradle);

        Assert.Equal(Path.Combine(root.RootPath, "gradlew"), wrapperPath);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Find_DoesNotCrossACheckoutBoundary(bool gitIsAFile)
    {
        // A submodule or nested clone pins its own build tool version; the outer repository's wrapper is
        // a different project's. A worktree or submodule records .git as a file rather than a directory.
        using var root = new TempJavaBuildRootDirectory();
        var module = root.CreateModule("inner");
        var gitPath = Path.Combine(module, ".git");
        if (gitIsAFile)
        {
            File.WriteAllText(gitPath, "gitdir: ../.git/modules/inner");
        }
        else
        {
            Directory.CreateDirectory(gitPath);
        }

        Assert.Null(JavaWrapper.Find(module, "mvnw", JavaWrapperTool.Maven));
    }

    [Fact]
    [SkipOnPlatform(TestPlatforms.Windows, "UnixFileMode does not describe Windows ACLs")]
    public void Find_IgnoresAnAncestorWrapperInAWorldWritableDirectory()
    {
        // On a shared machine a project under a world-writable directory such as /tmp would otherwise
        // execute an mvnw another user planted beside a pom.xml.
        using var root = new TempJavaBuildRootDirectory();
        var module = root.CreateModule("catalog");
        // CA1416 does not understand SkipOnPlatform, which already keeps this off Windows.
#pragma warning disable CA1416
        File.SetUnixFileMode(
            root.RootPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute);
#pragma warning restore CA1416

        Assert.Null(JavaWrapper.Find(module, "mvnw", JavaWrapperTool.Maven));
    }

    [Fact]
    [SkipOnPlatform(TestPlatforms.Windows, "UnixFileMode does not describe Windows ACLs")]
    public void Find_UsesAnAncestorWrapperInAGroupWritableDirectory()
    {
        // Deliberately still trusted. Distributions that enable user private groups give every user a
        // group of their own and a umask of 002, so an ordinary `git clone` on Ubuntu produces mode
        // 775 directories. Rejecting group-writable would therefore stop resolving the aggregator
        // wrapper for a large share of Linux checkouts - a hard build failure - to defend a case that
        // needs a genuinely shared group. Distinguishing the two needs the directory's owner and
        // group membership, which .NET does not expose portably.
        using var root = new TempJavaBuildRootDirectory();
        var module = root.CreateModule("catalog");
#pragma warning disable CA1416
        File.SetUnixFileMode(
            root.RootPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
#pragma warning restore CA1416

        var wrapperPath = JavaWrapper.Find(module, "mvnw", JavaWrapperTool.Maven);

        Assert.Equal(Path.Combine(root.RootPath, "mvnw"), wrapperPath);
    }

    [Fact]
    [SkipOnPlatform(TestPlatforms.Windows, "UnixFileMode does not describe Windows ACLs")]
    public void Find_IgnoresAnAncestorWrapperThatIsItselfWorldWritable()
    {
        // Rewriting a file in place needs write permission on the file, not on its directory, so a
        // safe build root still hands out an attacker-controlled script when the wrapper itself is
        // world-writable.
        using var root = new TempJavaBuildRootDirectory();
        var module = root.CreateModule("catalog");
#pragma warning disable CA1416
        File.SetUnixFileMode(
            Path.Combine(root.RootPath, "mvnw"),
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute);
#pragma warning restore CA1416

        Assert.Null(JavaWrapper.Find(module, "mvnw", JavaWrapperTool.Maven));
    }

    [Theory]
    [InlineData("mvnw.cmd")]
    [InlineData("gradlew.bat")]
    public void GetInvocation_OnWindowsRunsTheWrapperThroughCall(string wrapperName)
    {
        // cmd strips the first and last quote on the line when the first token is quoted, so a wrapper
        // path containing a space would be mangled if it led. "call" keeps a quote off the front.
        var workingDirectory = Path.Combine(Path.GetTempPath(), "repo", "services", "api");
        var wrapperPath = Path.Combine(Path.GetTempPath(), "repo", "build tools", wrapperName);

        var (command, leadingArgs) = JavaWrapper.GetInvocation(wrapperPath, workingDirectory, isWindows: true);

        Assert.Equal(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe", command);
        Assert.Equal(["/c", "call", Path.Combine("..", "..", "build tools", wrapperName)], leadingArgs);
    }

    [Fact]
    public void GetInvocation_OnWindowsPrefixesASiblingWrapperWithCurrentDirectory()
    {
        // A bare "mvnw.cmd" is resolved by name, which fails when NoDefaultCurrentDirectoryInExePath=1.
        var workingDirectory = Path.Combine(Path.GetTempPath(), "repo");
        var wrapperPath = Path.Combine(workingDirectory, "mvnw.cmd");

        var (_, leadingArgs) = JavaWrapper.GetInvocation(wrapperPath, workingDirectory, isWindows: true);

        Assert.Equal(["/c", "call", $".{Path.DirectorySeparatorChar}mvnw.cmd"], leadingArgs);
    }

    [Fact]
    public void GetInvocation_OnWindowsLeavesASubdirectoryWrapperUnprefixed()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "repo");
        var wrapperPath = Path.Combine(workingDirectory, "tools", "mvnw.cmd");

        var (_, leadingArgs) = JavaWrapper.GetInvocation(wrapperPath, workingDirectory, isWindows: true);

        Assert.Equal(["/c", "call", Path.Combine("tools", "mvnw.cmd")], leadingArgs);
    }

    [Fact]
    [SkipOnPlatform(TestPlatforms.Linux | TestPlatforms.OSX | TestPlatforms.FreeBSD, "Drive letters only exist on Windows.")]
    public void GetInvocation_OnWindowsKeepsAWrapperOnAnotherDriveAbsolute()
    {
        var (_, leadingArgs) = JavaWrapper.GetInvocation(@"Z:\tools\mvnw.cmd", @"C:\repo", isWindows: true);

        Assert.Equal(["/c", "call", @"Z:\tools\mvnw.cmd"], leadingArgs);
    }

    [Fact]
    public void GetInvocation_OnUnixRunsTheWrapperThroughShWithItsFullPath()
    {
        // A wrapper checked out on Windows arrives without its executable bit, so it is run by sh
        // rather than executed. The path stays absolute because no shell resolves it.
        var wrapperPath = Path.Combine(Path.GetTempPath(), "repo", "mvnw");

        var (command, leadingArgs) = JavaWrapper.GetInvocation(
            wrapperPath, Path.Combine(Path.GetTempPath(), "repo", "services", "api"), isWindows: false);

        Assert.Equal("sh", command);
        Assert.Equal([wrapperPath], leadingArgs);
    }
}
