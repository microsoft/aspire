// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ComponentModel;
using System.Text;
using Aspire.Hosting.Dcp.Process;

namespace Aspire.Hosting;

/// <summary>
/// Runs .NET CLI commands for Blazor projects.
/// </summary>
internal static class BlazorDotNetCliRunner
{
    public static async Task<BlazorDotNetCliResult> RunAsync(
        string projectPath,
        string command,
        IReadOnlyList<string> arguments,
        bool machineReadableOutput,
        CancellationToken cancellationToken)
    {
        var executablePath = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } dotnetHostPath
            ? dotnetHostPath
            : "dotnet";
        var argumentList = new List<string>(arguments.Count + 2)
        {
            command,
            projectPath
        };
        argumentList.AddRange(arguments);

        var standardOutput = new StringBuilder();
        var standardError = new StringBuilder();
        var processSpec = new ProcessSpec(executablePath)
        {
            WorkingDirectory = Path.GetDirectoryName(projectPath)!,
            ArgumentList = argumentList,
            ThrowOnNonZeroReturnCode = false,
            OnOutputData = line => standardOutput.AppendLine(line),
            OnErrorData = line => standardError.AppendLine(line)
        };
        if (machineReadableOutput)
        {
            // MSBuild queries emit JSON on stdout. Disable unrelated CLI messages that could
            // corrupt the machine-readable output before callers have a chance to parse it.
            processSpec.EnvironmentVariables["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
            processSpec.EnvironmentVariables["DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE"] = "1";
        }

        Task<ProcessResult> pendingResult;
        IAsyncDisposable processDisposable;
        try
        {
            (pendingResult, processDisposable) = ProcessUtil.Run(processSpec);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            return new(executablePath, false, -1, "", "", ex);
        }

        await using (processDisposable.ConfigureAwait(false))
        {
            var result = await pendingResult.WaitAsync(cancellationToken).ConfigureAwait(false);

            return new(
                executablePath,
                true,
                result.ExitCode,
                standardOutput.ToString(),
                standardError.ToString(),
                null);
        }
    }
}

internal readonly record struct BlazorDotNetCliResult(
    string Command,
    bool Started,
    int ExitCode,
    string StandardOutput,
    string StandardError,
    Exception? StartException);
