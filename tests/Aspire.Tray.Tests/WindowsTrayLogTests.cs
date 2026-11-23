// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Aspire.Tray.Tests.Helpers;

namespace Aspire.Tray.Tests;

public class WindowsTrayLogTests
{
    public static bool SupportsWindows => OperatingSystem.IsWindows();

    [Fact(Skip = "Uses Windows no-follow file handles.", SkipUnless = nameof(SupportsWindows))]
    [SupportedOSPlatform("windows")]
    public void CreatesMissingDirectoriesAndAppendsWithoutTruncating()
    {
        using var directory = new TestTrayStateDirectory();
        var path = Path.Combine(directory.UserProfilePath, "Aspire", "Tray");
        using (var first = WindowsTrayLog.OpenLog(path))
        {
            first.Write(Encoding.UTF8.GetBytes("first\n"));
        }
        using (var second = WindowsTrayLog.OpenLog(path))
        {
            second.Position = 0;
            second.Write(Encoding.UTF8.GetBytes("second\n"));
        }

        Assert.Equal("first\nsecond\n", File.ReadAllText(Path.Combine(path, "aspire-tray.log")));
    }

    [Theory(Skip = "Uses Windows junctions.", SkipUnless = nameof(SupportsWindows))]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [SupportedOSPlatform("windows")]
    public async Task JunctionAtAnyAncestorIsRejectedWithoutWritingTheTarget(int depth)
    {
        using var directory = new TestTrayStateDirectory();
        string[] components = ["profile", "Aspire", "Tray"];
        var root = Directory.CreateDirectory(directory.UserProfilePath).FullName;
        var target = Directory.CreateDirectory(directory.LegacyDirectory).FullName;
        var link = root;
        for (var i = 0; i <= depth; i++)
        {
            link = Path.Combine(link, components[i]);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        var targetLogDirectory = target;
        for (var i = depth + 1; i < components.Length; i++)
        {
            targetLogDirectory = Path.Combine(targetLogDirectory, components[i]);
        }
        Directory.CreateDirectory(targetLogDirectory);
        var sentinel = Path.Combine(targetLogDirectory, "aspire-tray.log");
        File.WriteAllText(sentinel, "untouched");
        var result = await TestNativeCommand.RunAsync(Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            "/d", "/c", "mklink", "/J", link, target);
        Assert.True(result.ExitCode == 0, $"{result.Output}\n{result.Error}");
        try
        {
            Assert.Throws<IOException>(() => WindowsTrayLog.OpenLog(Path.Combine(root, Path.Combine(components))));
            Assert.Equal("untouched", File.ReadAllText(sentinel));
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    [Fact(Skip = "Uses Windows file symbolic links.", SkipUnless = nameof(SupportsWindows))]
    [SupportedOSPlatform("windows")]
    public void SymbolicLinkLogIsRejectedWithoutWritingTheTarget()
    {
        using var directory = new TestTrayStateDirectory();
        var path = Directory.CreateDirectory(directory.UserProfilePath).FullName;
        var target = directory.CreateAppHost("target.txt");
        File.WriteAllText(target, "untouched");
        File.CreateSymbolicLink(Path.Combine(path, "aspire-tray.log"), target);

        Assert.Throws<IOException>(() => WindowsTrayLog.OpenLog(path));
        Assert.Equal("untouched", File.ReadAllText(target));
    }

    [Fact(Skip = "Uses Windows hard links.", SkipUnless = nameof(SupportsWindows))]
    [SupportedOSPlatform("windows")]
    public async Task HardLinkLogIsRejectedWithoutWritingTheTarget()
    {
        using var directory = new TestTrayStateDirectory();
        var path = Directory.CreateDirectory(directory.UserProfilePath).FullName;
        var target = directory.CreateAppHost("target.txt");
        File.WriteAllText(target, "untouched");
        var result = await TestNativeCommand.RunAsync(Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            "/d", "/c", "mklink", "/H", Path.Combine(path, "aspire-tray.log"), target);
        Assert.True(result.ExitCode == 0, $"{result.Output}\n{result.Error}");

        Assert.Throws<IOException>(() => WindowsTrayLog.OpenLog(path));
        Assert.Equal("untouched", File.ReadAllText(target));
    }

    [Fact(Skip = "Uses Windows directory sharing locks.", SkipUnless = nameof(SupportsWindows))]
    [SupportedOSPlatform("windows")]
    public void AncestorCannotBeRenamedWhileItIsBeingValidated()
    {
        using var directory = new TestTrayStateDirectory();
        var path = Directory.CreateDirectory(directory.UserProfilePath).FullName;
        using (var handle = WindowsTrayLog.OpenDirectory(path))
        {
            Assert.Throws<IOException>(() => Directory.Move(path, directory.LegacyDirectory));
        }

        Directory.Move(path, directory.LegacyDirectory);
        Assert.True(Directory.Exists(directory.LegacyDirectory));
    }

    [Fact(Skip = "Uses Windows directory sharing locks.", SkipUnless = nameof(SupportsWindows))]
    [SupportedOSPlatform("windows")]
    public void AncestorCannotBeOpenedForReparseMutationWhileItIsBeingValidated()
    {
        using var directory = new TestTrayStateDirectory();
        var path = Directory.CreateDirectory(directory.UserProfilePath).FullName;
        using (var handle = WindowsTrayLog.OpenDirectory(path))
        {
            using var mutation = TestWindowsFileInterop.OpenDirectoryForReparseMutation(path);
            var error = Marshal.GetLastPInvokeError();
            Assert.True(mutation.IsInvalid);
            Assert.Equal(32, error); // ERROR_SHARING_VIOLATION, not a permission failure.
        }

        using var allowed = TestWindowsFileInterop.OpenDirectoryForReparseMutation(path);
        Assert.False(allowed.IsInvalid);
    }

    [Fact(Skip = "Uses Windows file sharing locks.", SkipUnless = nameof(SupportsWindows))]
    [SupportedOSPlatform("windows")]
    public void OpenLogCannotBeReplacedAndContinuesWritingToItsValidatedFile()
    {
        using var directory = new TestTrayStateDirectory();
        var path = directory.UserProfilePath;
        var log = Path.Combine(path, "aspire-tray.log");
        using (var stream = WindowsTrayLog.OpenLog(path))
        {
            Assert.Throws<IOException>(() => File.Move(log, Path.Combine(path, "old.log")));
            stream.Write(Encoding.UTF8.GetBytes("original file\n"));
        }

        Assert.Equal("original file\n", File.ReadAllText(log));
    }
}
