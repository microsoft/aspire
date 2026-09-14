// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security;
using Microsoft.Win32;

namespace Aspire.Tray;

/// <summary>
/// Resolves supported folder applications without executing registry command templates.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed record FolderApplication(string Title, string Executable, bool Terminal)
{
    internal static FolderApplication[] Discover(Action<string> reportError)
    {
        List<FolderApplication> applications = [];
        foreach (var (name, title, terminal) in new[]
        {
            ("Code.exe", "Visual Studio Code", false),
            ("Code - Insiders.exe", "Visual Studio Code - Insiders", false),
            ("rider64.exe", "JetBrains Rider", false),
            ("sublime_text.exe", "Sublime Text", false),
            ("WindowsTerminal.exe", "Windows Terminal", true)
        })
        {
            var path = FindAppPath(name, reportError);
            if (path is not null)
            {
                applications.Add(new(title, path, terminal));
            }
        }
        // Store applications register execution aliases rather than App Paths. Accept only
        // this exact OS-managed alias under the user's WindowsApps directory, never PATH.
        var terminalAlias = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WindowsApps", "wt.exe");
        if (!applications.Any(application => application.Terminal) && File.Exists(terminalAlias))
        {
            applications.Add(new("Windows Terminal", terminalAlias, true));
        }
        var powerShell = FindAppPath("pwsh.exe", reportError) ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");
        if (File.Exists(powerShell))
        {
            applications.Add(new("PowerShell", powerShell, false));
        }
        return applications.ToArray();
    }

    private static string? FindAppPath(string name, Action<string> reportError)
    {
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using var root = RegistryKey.OpenBaseKey(hive, view);
                    using var key = root.OpenSubKey($@"Software\Microsoft\Windows\CurrentVersion\App Paths\{name}");
                    // The default App Paths value is an executable path, not a shell command:
                    // C:\Program Files\Microsoft VS Code\Code.exe (sometimes surrounded by quotes).
                    // https://learn.microsoft.com/windows/win32/shell/app-registration
                    if (key?.GetValue(null) is string value)
                    {
                        var path = value.Trim().Trim('"');
                        if (Path.IsPathFullyQualified(path) && path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(path))
                        {
                            return Path.GetFullPath(path);
                        }
                        reportError($"The registered path for {name} is not an existing absolute executable.");
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or ArgumentException)
                {
                    reportError($"Could not read the registered application {name}: {ex.Message}");
                }
            }
        }
        return null;
    }

    internal void Open(string directory)
        => Start(CreateFolderStartInfo(directory));

    internal ProcessStartInfo CreateFolderStartInfo(string directory)
    {
        directory = Path.GetFullPath(directory);
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException("The selected project folder no longer exists.");
        }
        var start = CreateStartInfo(Executable, directory);
        if (Terminal)
        {
            // Terminal parses ';' as a command separator even inside argv values.
            // Resolve '.' from WorkingDirectory instead of feeding paths into that grammar.
            start.ArgumentList.Add("-d");
            start.ArgumentList.Add(".");
        }
        else if (Title == "PowerShell")
        {
            // WorkingDirectory selects the folder without embedding it in PowerShell code.
            start.ArgumentList.Add("-NoLogo");
            start.ArgumentList.Add("-NoExit");
        }
        else
        {
            start.ArgumentList.Add(directory);
        }
        return start;
    }

    internal static void ShowInExplorer(string path)
    {
        path = TrayAppHostPath.RequireExistingFile(path);
        // Pass a shell item rather than Explorer's /select command grammar, which treats
        // commas and quotes specially. cidl=0 selects the absolute PIDL in its parent.
        // https://learn.microsoft.com/windows/win32/api/shlobj_core/nf-shlobj_core-shopenfolderandselectitems
        Marshal.ThrowExceptionForHR(NativeMethods.ParseDisplayName(path, 0, out var item, 0, out _));
        try
        {
            Marshal.ThrowExceptionForHR(NativeMethods.OpenFolderAndSelectItems(item, 0, 0, 0));
        }
        finally
        {
            Marshal.FreeCoTaskMem(item);
        }
    }

    private static ProcessStartInfo CreateStartInfo(string executable, string directory)
    {
        if (!Path.IsPathFullyQualified(executable) || !File.Exists(executable))
        {
            throw new FileNotFoundException("The selected application is no longer installed.", executable);
        }
        return new(executable) { UseShellExecute = false, WorkingDirectory = directory };
    }

    private static void Start(ProcessStartInfo start)
    {
        // The user owns the launched editor/terminal. Dispose only our process handle.
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Windows could not start the selected application.");
    }
}
