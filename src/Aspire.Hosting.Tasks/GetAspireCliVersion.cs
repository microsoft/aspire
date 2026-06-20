// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Build.Framework;

namespace Aspire.Hosting.Tasks;

/// <summary>
/// Gets the version of the Aspire CLI executable.
/// </summary>
public sealed class GetAspireCliVersion : Microsoft.Build.Utilities.Task
{
    /// <summary>
    /// Optional path to the Aspire CLI executable. When unset, the Aspire CLI is resolved from PATH.
    /// </summary>
    public string? AspireCliPath { get; set; }

    /// <summary>
    /// The resolved Aspire CLI version (e.g., "13.5.0").
    /// Empty string if the version cannot be determined.
    /// </summary>
    [Output]
    public string AspireCliVersion { get; set; } = string.Empty;

    public override bool Execute()
    {
        try
        {
            var version = GetCliVersion(AspireCliPath);
            AspireCliVersion = version ?? string.Empty;

            if (!string.IsNullOrEmpty(version))
            {
                Log.LogMessage(MessageImportance.Low, $"Resolved Aspire CLI version: {version}");
            }
            else
            {
                Log.LogMessage(MessageImportance.Low, "Could not determine Aspire CLI version.");
            }

            return true;
        }
        catch (Exception ex) when (IsVersionQueryException(ex))
        {
            Log.LogMessage(MessageImportance.Low, $"Error getting Aspire CLI version: {ex.Message}");
            return true;
        }
    }

    private static string? GetCliVersion(string? cliPath)
    {
        if (!string.IsNullOrWhiteSpace(cliPath) && !File.Exists(cliPath))
        {
            return null;
        }

        using var process = Process.Start(CreateStartInfo(cliPath));
        if (process is null)
        {
            return null;
        }

        // Read both streams concurrently to avoid deadlock when a pipe buffer fills.
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit(5000))
        {
            TryKill(process);
            TryWaitForExit(process);
            ObserveDrainTasks(outputTask, errorTask);
            return null;
        }

        var output = outputTask.GetAwaiter().GetResult().Trim();
        _ = errorTask.GetAwaiter().GetResult();

        if (process.ExitCode != 0)
        {
            return null;
        }

        // Aspire CLI version output is a bare informational version, for example:
        //   13.5.0-preview.1.26319.9+gabcdef
        // Strip the prerelease suffix because System.Version only accepts numeric version components.
        var prereleaseIndex = output.IndexOf('-');
        var version = prereleaseIndex >= 0 ? output.Substring(0, prereleaseIndex) : output;

        return Version.TryParse(version, out _) ? version : null;
    }

    private static ProcessStartInfo CreateStartInfo(string? cliPath)
    {
        if (string.IsNullOrWhiteSpace(cliPath))
        {
            return IsWindows()
                ? CreateStartInfo(GetCommandPromptPath(), "/C aspire --version")
                : CreateStartInfo("aspire", "--version");
        }

        return IsWindowsCommandScript(cliPath)
            ? CreateStartInfo(GetCommandPromptPath(), $"/D /C \"\"{cliPath}\" --version\"")
            : CreateStartInfo(cliPath, "--version");
    }

    private static ProcessStartInfo CreateStartInfo(string fileName, string arguments) => new()
    {
        FileName = fileName,
        Arguments = arguments,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true,
    };

    private static string GetCommandPromptPath()
    {
        var comSpec = Environment.GetEnvironmentVariable("ComSpec");

        return string.IsNullOrWhiteSpace(comSpec) ? "cmd" : comSpec;
    }

    private static bool IsWindowsCommandScript(string path)
    {
        var extension = Path.GetExtension(path);

        return IsWindows()
            && (string.Equals(extension, ".cmd", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".bat", StringComparison.OrdinalIgnoreCase));
    }

    private static void TryKill(Process process)
    {
        try
        {
#if NET5_0_OR_GREATER
            process.Kill(entireProcessTree: true);
#else
            process.Kill();
#endif
        }
        catch (Exception ex) when (IsVersionQueryException(ex))
        {
        }
    }

    private static void TryWaitForExit(Process process)
    {
        try
        {
            process.WaitForExit(1000);
        }
        catch (Exception ex) when (IsVersionQueryException(ex))
        {
        }
    }

    private static void ObserveDrainTasks(params Task<string>[] tasks)
    {
        try
        {
            Task.WaitAll(tasks, 1000);
        }
        catch (AggregateException ex) when (ex.InnerExceptions.All(IsVersionQueryException))
        {
        }
        catch (Exception ex) when (IsVersionQueryException(ex))
        {
        }

        foreach (var task in tasks)
        {
            ObserveDrainTask(task);
        }
    }

    private static void ObserveDrainTask(Task<string> task)
    {
        if (!task.IsCompleted)
        {
            _ = task.ContinueWith(
                static t => _ = t.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return;
        }

        try
        {
            _ = task.GetAwaiter().GetResult();
        }
        catch (Exception ex) when (IsVersionQueryException(ex))
        {
        }
    }

    private static bool IsWindows() => Path.DirectorySeparatorChar == '\\';

    private static bool IsVersionQueryException(Exception ex)
    {
        return ex is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException
            or PathTooLongException
            or System.Security.SecurityException
            or Win32Exception
            or InvalidOperationException;
    }
}
