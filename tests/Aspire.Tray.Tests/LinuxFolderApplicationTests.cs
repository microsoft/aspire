// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Runtime.Versioning;
using Aspire.Tray.Tests.Helpers;

namespace Aspire.Tray.Tests;

public class LinuxFolderApplicationTests
{
    public static bool SupportsLinux => OperatingSystem.IsLinux();

    [Theory]
    [InlineData("code", "Visual Studio Code", "", "--")]
    [InlineData("code-insiders", "Visual Studio Code - Insiders", "", "--")]
    [InlineData("codium", "VSCodium", "", "--")]
    [InlineData("rider", "Rider", "", "")]
    [InlineData("subl", "Sublime Text", "", "--")]
    [InlineData("gnome-terminal", "GNOME Terminal", "", "--working-directory")]
    [InlineData("kgx", "GNOME Console", "", "--working-directory")]
    [InlineData("ptyxis", "Ptyxis", "--new-window", "--working-directory")]
    [InlineData("konsole", "Konsole", "", "--workdir")]
    [InlineData("xfce4-terminal", "Xfce Terminal", "", "--working-directory")]
    [InlineData("kitty", "Kitty", "", "--directory")]
    [InlineData("alacritty", "Alacritty", "", "--working-directory")]
    [InlineData("foot", "Foot", "", "--working-directory")]
    [InlineData("wezterm", "WezTerm", "start", "--cwd")]
    [InlineData("ghostty", "Ghostty", "", "--working-directory=")]
    public void DiscoveredApplicationsPassTheFolderAsData(string command, string title, string prefix, string option)
    {
        using var directory = new TestTrayStateDirectory();
        var executable = directory.CreateAppHost($"bin/{command}");
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        }
        var folder = Path.GetDirectoryName(directory.CreateAppHost("project 'quotes' & $var; %20 #/apphost.cs"))!;

        var application = Assert.Single(LinuxFolderApplication.Discover(Path.GetDirectoryName(executable)!));
        var start = application.CreateStartInfo(folder);

        Assert.Equal(title, application.Title);
        Assert.Equal(executable, start.FileName);
        Assert.Equal(folder, start.WorkingDirectory);
        Assert.False(start.UseShellExecute);
        List<string> expected = [];
        if (prefix.Length > 0)
        {
            expected.Add(prefix);
        }
        if (option.EndsWith('='))
        {
            expected.Add(option + folder);
        }
        else
        {
            if (option.Length > 0)
            {
                expected.Add(option);
            }
            expected.Add(folder);
        }
        Assert.Equal(expected, start.ArgumentList);
        Assert.Throws<DirectoryNotFoundException>(() => application.CreateStartInfo(Path.Combine(folder, "missing")));
    }

    [Fact(Skip = "Uses Linux executable permissions.", SkipUnless = nameof(SupportsLinux))]
    [SupportedOSPlatform("linux")]
    public void DiscoveryIgnoresRelativeAndNonExecutableEntriesAndKeepsPathPriority()
    {
        using var directory = new TestTrayStateDirectory();
        var first = directory.CreateAppHost("first/code");
        var ignored = directory.CreateAppHost("first/konsole");
        var second = directory.CreateAppHost("second/code");
        var terminal = directory.CreateAppHost("second/konsole");
        foreach (var executable in new[] { first, second, terminal })
        {
            File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        }
        File.SetUnixFileMode(ignored, UnixFileMode.UserRead);
        var path = string.Join(Path.PathSeparator, "", ".", "relative", Path.GetDirectoryName(first), Path.GetDirectoryName(second));

        var applications = LinuxFolderApplication.Discover(path);

        Assert.Equal([first, terminal], applications.Select(application => application.Executable));
        Assert.Empty(LinuxFolderApplication.Discover(""));
    }

    [Fact(Skip = "Runs an isolated Unix executable.", SkipUnless = nameof(SupportsLinux))]
    [SupportedOSPlatform("linux")]
    public async Task TerminalLaunchPreservesSpecialCharactersWithoutShellExpansion()
    {
        using var directory = new TestTrayStateDirectory();
        var executable = directory.CreateAppHost("bin/gnome-terminal");
        File.WriteAllText(executable, "#!/bin/sh\nprintf '%s\\n' \"$PWD\" \"$@\" > arguments.txt\n");
        File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var folder = Path.GetDirectoryName(directory.CreateAppHost("project $(echo expanded); 'quotes' & %20 #/apphost.cs"))!;
        var application = Assert.Single(LinuxFolderApplication.Discover(Path.GetDirectoryName(executable)!));

        using var process = Process.Start(application.CreateStartInfo(folder))!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await process.WaitForExitAsync(timeout.Token);

        Assert.Equal(0, process.ExitCode);
        Assert.Equal([folder, "--working-directory", folder], File.ReadAllLines(Path.Combine(folder, "arguments.txt")));
    }
}
