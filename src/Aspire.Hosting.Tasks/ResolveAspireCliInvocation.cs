// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace Aspire.Hosting.Tasks;

/// <summary>
/// Selects the command used to delegate an AppHost launch to the Aspire CLI.
/// </summary>
public sealed class ResolveAspireCliInvocation : Microsoft.Build.Utilities.Task
{
    /// <summary>
    /// Gets or sets an explicitly configured Aspire CLI path.
    /// </summary>
    public string? AspireCliPath { get; set; }

    /// <summary>
    /// Gets or sets the requested Aspire CLI invocation mode.
    /// </summary>
    public string? AspireCliInvocationMode { get; set; }

    /// <summary>
    /// Gets or sets the <c>PATH</c> value used to locate commands.
    /// </summary>
    public string? PathEnvironmentVariable { get; set; }

    /// <summary>
    /// Gets the selected invocation mode.
    /// </summary>
    [Output]
    public string? ResolvedInvocationMode { get; set; }

    /// <summary>
    /// Gets the Aspire CLI command found on <c>PATH</c>.
    /// </summary>
    [Output]
    public string? ResolvedAspireCliPath { get; set; }

    /// <summary>
    /// Gets the DNX command found on <c>PATH</c>.
    /// </summary>
    [Output]
    public string? ResolvedDnxPath { get; set; }

    /// <summary>
    /// Gets the executable that hosts the selected DNX command.
    /// </summary>
    [Output]
    public string? ResolvedDnxHostPath { get; set; }

    /// <summary>
    /// Gets the arguments that select DNX on <see cref="ResolvedDnxHostPath"/>.
    /// </summary>
    [Output]
    public string? ResolvedDnxHostArguments { get; set; }

    /// <summary>
    /// Gets the individual arguments that select DNX on <see cref="ResolvedDnxHostPath"/>.
    /// </summary>
    [Output]
    public ITaskItem[] ResolvedDnxHostArgumentItems { get; set; } = [];

    public override bool Execute()
    {
        if (!string.IsNullOrWhiteSpace(AspireCliPath))
        {
            ResolvedInvocationMode = "Aspire";
            ResolvedAspireCliPath = AspireCliPath;
            return true;
        }

        var forceDnx =
            string.Equals(AspireCliInvocationMode, "Dnx", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(AspireCliInvocationMode, "DnxPinned", StringComparison.OrdinalIgnoreCase);
        if (!forceDnx)
        {
            ResolvedAspireCliPath = CommandPathResolver.ResolveFromPath("aspire", PathEnvironmentVariable);
            if (ResolvedAspireCliPath is not null)
            {
                ResolvedInvocationMode = "Aspire";
            }
        }

        foreach (var dnxPath in CommandPathResolver.EnumerateFromPath("dnx", PathEnvironmentVariable))
        {
            if (TryResolveDnxHost(dnxPath))
            {
                ResolvedDnxPath = dnxPath;
                break;
            }

            Log.LogMessage(MessageImportance.Low, "DNX command '{0}' could not be mapped to an executable host.", dnxPath);
        }

        if (forceDnx || (ResolvedInvocationMode is null && ResolvedDnxPath is not null))
        {
            // Keep DNX selected when it was explicitly requested but unavailable. The run
            // preflight emits the actionable diagnostic, while ordinary builds remain valid.
            ResolvedInvocationMode = "Dnx";
        }

        return true;
    }

    private bool TryResolveDnxHost(string dnxPath)
    {
        if (!IsWindowsCommandShim(dnxPath))
        {
            ResolvedDnxHostPath = dnxPath;
            return true;
        }

        var dotnetRoot = Path.GetDirectoryName(dnxPath);
        if (string.IsNullOrEmpty(dotnetRoot))
        {
            return false;
        }

        var dotnetPath = Path.Combine(dotnetRoot, "dotnet.exe");
        if (!File.Exists(dotnetPath) || !TryGetLatestSdkVersion(dotnetPath, out var sdkVersion))
        {
            return false;
        }

        var sdkPath = Path.Combine(dotnetRoot, "sdk", sdkVersion!, "dotnet.dll");
        if (!File.Exists(sdkPath))
        {
            return false;
        }

        // The SDK's dnx.cmd intentionally uses `dotnet exec <sdk>\dotnet.dll dnx` so a
        // global.json that selects an older SDK cannot hide the DNX command. Preserve that
        // behavior while bypassing cmd.exe, which would reinterpret forwarded AppHost arguments.
        ResolvedDnxHostPath = dotnetPath;
        ResolvedDnxHostArguments = $"exec \"{sdkPath}\" dnx";
        ResolvedDnxHostArgumentItems =
        [
            new TaskItem("exec"),
            new TaskItem(sdkPath),
            new TaskItem("dnx")
        ];
        return true;
    }

    private bool TryGetLatestSdkVersion(string dotnetPath, out string? sdkVersion)
    {
        var startInfo = new ProcessStartInfo(dotnetPath, "--list-sdks")
        {
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                sdkVersion = null;
                return false;
            }

            // Drain both redirected streams concurrently so a full pipe cannot block the process.
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(milliseconds: 5000))
            {
                try
                {
                    process.Kill();
                    process.WaitForExit();
                }
                catch (InvalidOperationException)
                {
                    // The process exited between the timeout and termination attempt.
                }

                Log.LogMessage(MessageImportance.Low, "'{0} --list-sdks' timed out after 5 seconds.", dotnetPath);
                sdkVersion = null;
                return false;
            }

            var output = outputTask.GetAwaiter().GetResult();
            var error = errorTask.GetAwaiter().GetResult();
            if (process.ExitCode != 0)
            {
                Log.LogMessage(MessageImportance.Low, "'{0} --list-sdks' failed with exit code {1}: {2}", dotnetPath, process.ExitCode, error);
                sdkVersion = null;
                return false;
            }

            // `dotnet --list-sdks` is ordered from oldest to newest and emits:
            //   10.0.100 [C:\Program Files\dotnet\sdk]
            var lastLine = output
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .LastOrDefault();
            var separatorIndex = lastLine?.IndexOf(' ') ?? -1;
            sdkVersion = separatorIndex > 0 ? lastLine!.Substring(0, separatorIndex) : null;
            return sdkVersion is not null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            Log.LogMessage(MessageImportance.Low, "Failed to inspect SDKs through '{0}': {1}", dotnetPath, ex.Message);
            sdkVersion = null;
            return false;
        }
    }

    private static bool IsWindowsCommandShim(string path)
    {
        return Path.DirectorySeparatorChar == '\\'
            && (path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".bat", StringComparison.OrdinalIgnoreCase));
    }
}
