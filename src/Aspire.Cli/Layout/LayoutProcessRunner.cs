// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Shared;

namespace Aspire.Cli.Layout;

/// <summary>
/// Helper to detect the current runtime identifier.
/// Delegates to shared BundleDiscovery for consistent behavior.
/// </summary>
internal static class RuntimeIdentifierHelper
{
    /// <summary>
    /// Gets the current platform's runtime identifier.
    /// </summary>
    public static string GetCurrent() => BundleDiscovery.GetCurrentRuntimeIdentifier();

    /// <summary>
    /// Gets the archive extension for the current platform.
    /// </summary>
    public static string GetArchiveExtension() => BundleDiscovery.GetArchiveExtension();
}

/// <summary>
/// Utilities for running processes using layout tools.
/// All layout tools are self-contained executables — no muxer needed.
/// </summary>
internal static class LayoutProcessRunner
{
    /// <summary>
    /// Runs a tool and captures output. The tool is always run directly as a native executable.
    /// </summary>
    public static async Task<(int ExitCode, string Output, string Error)> RunAsync(
        string toolPath,
        IEnumerable<string> arguments,
        string? workingDirectory = null,
        IDictionary<string, string>? environmentVariables = null,
        CancellationToken ct = default)
    {
        using var process = CreateProcess(toolPath, arguments, workingDirectory, environmentVariables, redirectOutput: true);

        process.Start();

        var outputTask = process.StandardOutput.ReadToEndAsync(ct);
        var errorTask = process.StandardError.ReadToEndAsync(ct);

        await process.WaitForExitAsync(ct);

        return (process.ExitCode, await outputTask, await errorTask);
    }

    /// <summary>
    /// Starts a process without waiting for it to exit.
    /// Returns the Process object for the caller to manage.
    /// </summary>
    public static Process Start(
        string toolPath,
        IEnumerable<string> arguments,
        string? workingDirectory = null,
        IDictionary<string, string>? environmentVariables = null,
        bool redirectOutput = false)
    {
        var process = CreateProcess(toolPath, arguments, workingDirectory, environmentVariables, redirectOutput);
        process.Start();
        return process;
    }

    /// <summary>
    /// Creates a configured Process for running a bundle tool.
    /// Tools are always self-contained executables — run directly.
    /// </summary>
    private static Process CreateProcess(
        string toolPath,
        IEnumerable<string> arguments,
        string? workingDirectory,
        IDictionary<string, string>? environmentVariables,
        bool redirectOutput)
    {
        var process = new Process();

        process.StartInfo.UseShellExecute = false;
        process.StartInfo.CreateNoWindow = true;
        process.StartInfo.FileName = toolPath;

        if (redirectOutput)
        {
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;
        }

        // Add custom environment variables
        if (environmentVariables is not null)
        {
            foreach (var (key, value) in environmentVariables)
            {
                process.StartInfo.Environment[key] = value;
            }
        }

        if (workingDirectory is not null)
        {
            process.StartInfo.WorkingDirectory = workingDirectory;
        }

        // Add arguments
        foreach (var arg in arguments)
        {
            process.StartInfo.ArgumentList.Add(arg);
        }

        return process;
    }
}
