// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;

namespace Aspire.Tray;

/// <summary>
/// Known folder-capable applications, launched with arguments rather than shell commands.
/// </summary>
internal sealed record LinuxFolderApplication(string Title, string Executable, IReadOnlyList<string> DirectoryArguments)
{
    internal static IReadOnlyList<LinuxFolderApplication> Discover(string searchPath)
    {
        var directories = searchPath.Split(Path.PathSeparator).Where(Path.IsPathFullyQualified).ToArray();
        List<LinuxFolderApplication> applications = [];
        foreach (var (command, title, arguments) in s_applications)
        {
            var executable = directories.Select(directory => Path.Combine(directory, command))
                .FirstOrDefault(path => File.Exists(path) && (!OperatingSystem.IsLinux()
                    || (File.GetUnixFileMode(path) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0));
            if (executable is not null)
            {
                applications.Add(new(title, executable, arguments));
            }
        }
        return applications;
    }

    internal ProcessStartInfo CreateStartInfo(string directory)
    {
        directory = Path.GetFullPath(directory);
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException("The selected AppHost folder no longer exists.");
        }
        var start = new ProcessStartInfo(Executable)
        {
            UseShellExecute = false,
            WorkingDirectory = directory
        };
        foreach (var argument in DirectoryArguments)
        {
            start.ArgumentList.Add(argument);
        }
        // Some terminals require --working-directory=DIR. In either form DIR is
        // one argv value, never shell source or a terminal's "execute command".
        if (start.ArgumentList.Count > 0 && start.ArgumentList[^1].EndsWith('='))
        {
            start.ArgumentList[^1] += directory;
        }
        else
        {
            start.ArgumentList.Add(directory);
        }
        return start;
    }

    internal void Open(string directory)
    {
        using var process = Process.Start(CreateStartInfo(directory))
            ?? throw new InvalidOperationException($"Could not open {Title}.");
    }

    private static readonly (string Command, string Title, string[] Arguments)[] s_applications =
    [
        ("code", "Visual Studio Code", ["--"]),
        ("code-insiders", "Visual Studio Code - Insiders", ["--"]),
        ("codium", "VSCodium", ["--"]),
        ("rider", "Rider", []),
        ("subl", "Sublime Text", ["--"]),
        // Terminals can forward to an existing server, so WorkingDirectory alone
        // is insufficient. Supply the terminal's explicit directory option.
        // https://man.archlinux.org/man/gnome-terminal.1.en
        // https://sw.kovidgoyal.net/kitty/invocation/
        ("gnome-terminal", "GNOME Terminal", ["--working-directory"]),
        ("kgx", "GNOME Console", ["--working-directory"]),
        ("ptyxis", "Ptyxis", ["--new-window", "--working-directory"]),
        ("konsole", "Konsole", ["--workdir"]),
        ("xfce4-terminal", "Xfce Terminal", ["--working-directory"]),
        ("kitty", "Kitty", ["--directory"]),
        ("alacritty", "Alacritty", ["--working-directory"]),
        ("foot", "Foot", ["--working-directory"]),
        ("wezterm", "WezTerm", ["start", "--cwd"]),
        ("ghostty", "Ghostty", ["--working-directory="])
    ];
}
