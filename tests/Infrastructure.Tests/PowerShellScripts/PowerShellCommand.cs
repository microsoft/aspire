// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Runtime.InteropServices;
using Xunit;

namespace Infrastructure.Tests;

/// <summary>
/// Executes PowerShell scripts using pwsh.
/// Based on the ToolCommand pattern from tests/Shared/TemplatesTesting/ToolCommand.cs.
/// </summary>
public sealed class PowerShellCommand : IDisposable
{
    private readonly string _scriptPath;
    private readonly ITestOutputHelper _testOutput;
    private readonly string _label;
    private TimeSpan? _timeout;
    private readonly string _msgPrefix;

    public Process? CurrentProcess { get; private set; }
    public Dictionary<string, string> Environment { get; } = new();
    public string? WorkingDirectory { get; set; }

    public event DataReceivedEventHandler? ErrorDataReceived;
    public event DataReceivedEventHandler? OutputDataReceived;

    public PowerShellCommand(string scriptPath, ITestOutputHelper testOutput, string label = "")
    {
        _scriptPath = scriptPath;
        _testOutput = testOutput;
        _label = label;
        _msgPrefix = string.IsNullOrEmpty(_label) ? string.Empty : $"[{_label}] ";
    }

    public PowerShellCommand WithWorkingDirectory(string dir)
    {
        WorkingDirectory = dir;
        return this;
    }

    public PowerShellCommand WithEnvironmentVariable(string key, string value)
    {
        Environment[key] = value;
        return this;
    }

    public PowerShellCommand WithTimeout(TimeSpan timeSpan)
    {
        _timeout = timeSpan;
        return this;
    }

    public async Task<CommandResult> ExecuteAsync(params string[] args)
    {
        CancellationTokenSource cts = new();
        if (_timeout is not null)
        {
            cts.CancelAfter((int)_timeout.Value.TotalMilliseconds);
        }

        try
        {
            return await ExecuteAsyncInternal(cts.Token, args);
        }
        catch (TaskCanceledException tce) when (cts.IsCancellationRequested)
        {
            throw new TaskCanceledException(
                $"Command execution timed out after {_timeout!.Value.TotalSeconds} secs: pwsh {_scriptPath}",
                tce);
        }
    }

    public void Dispose()
    {
        CurrentProcess?.CloseAndKillProcessIfRunning();
    }

    private async Task<CommandResult> ExecuteAsyncInternal(CancellationToken token, string[] args)
    {
        Stopwatch runTimeStopwatch = new();
        var fullArgs = BuildArguments(args);

        _testOutput.WriteLine($"{_msgPrefix}Executing - pwsh {fullArgs} {WorkingDirectoryInfo()}");

        object outputLock = new();
        var outputLines = new List<string>();

        CurrentProcess = CreateProcess(fullArgs);

        CurrentProcess.ErrorDataReceived += (s, e) =>
        {
            if (e.Data is null)
            {
                return;
            }

            lock (outputLock)
            {
                outputLines.Add(e.Data);
            }
            _testOutput.WriteLine($"{_msgPrefix}{e.Data}");
            ErrorDataReceived?.Invoke(s, e);
        };

        CurrentProcess.OutputDataReceived += (s, e) =>
        {
            if (e.Data is null)
            {
                return;
            }

            lock (outputLock)
            {
                outputLines.Add(e.Data);
            }
            _testOutput.WriteLine($"{_msgPrefix}{e.Data}");
            OutputDataReceived?.Invoke(s, e);
        };

        try
        {
            runTimeStopwatch.Start();

            TaskCompletionSource exitedTcs = new();
            CurrentProcess.EnableRaisingEvents = true;
            CurrentProcess.Exited += (s, a) =>
            {
                exitedTcs.SetResult();
                runTimeStopwatch.Stop();
            };

            CurrentProcess.Start();
            CurrentProcess.BeginOutputReadLine();
            CurrentProcess.BeginErrorReadLine();

            await exitedTcs.Task.WaitAsync(token).ConfigureAwait(false);

            _testOutput.WriteLine($"{_msgPrefix}Got the Exited event, waiting on WaitForExitAsync");
            var waitForExitTask = CurrentProcess.WaitForExitAsync(token);
            var completedTask = await Task.WhenAny(waitForExitTask, Task.Delay(TimeSpan.FromSeconds(5), token)).ConfigureAwait(false);
            if (completedTask != waitForExitTask)
            {
                _testOutput.WriteLine($"{_msgPrefix}Timed out waiting for it. Ignoring.");
            }

            _testOutput.WriteLine($"{_msgPrefix}Process ran for {runTimeStopwatch.Elapsed.TotalSeconds:F2} secs");

            return new CommandResult(
                CurrentProcess.StartInfo,
                CurrentProcess.ExitCode,
                GetFullOutput());
        }
        catch (Exception ex)
        {
            _testOutput.WriteLine($"Exception: {ex}");
            _testOutput.WriteLine($"output: {GetFullOutput()}");
            throw;
        }
        finally
        {
            if (!CurrentProcess.TryGetHasExited())
            {
                _testOutput.WriteLine($"{_msgPrefix}Process has been running for {runTimeStopwatch.Elapsed.TotalSeconds:F2} secs");
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    CurrentProcess.CloseMainWindow();
                }

                _testOutput.WriteLine($"Killing");
                CurrentProcess.Kill(entireProcessTree: true);
            }
            CurrentProcess.Dispose();
        }

        string GetFullOutput()
        {
            lock (outputLock)
            {
                return string.Join(System.Environment.NewLine, outputLines);
            }
        }
    }

    private string BuildArguments(string[] args)
    {
        var argsList = new List<string>
        {
            "-NoProfile",
            "-NonInteractive",
            "-ExecutionPolicy", "Bypass",
            "-File", $"\"{_scriptPath}\""
        };
        argsList.AddRange(args);
        return string.Join(" ", argsList);
    }

    private Process CreateProcess(string args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "pwsh",
            Arguments = args,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            RedirectStandardInput = true,
            UseShellExecute = false
        };

        AddEnvironmentVariablesTo(psi);
        AddWorkingDirectoryTo(psi);

        return new Process
        {
            StartInfo = psi,
            EnableRaisingEvents = true
        };
    }

    private string WorkingDirectoryInfo()
    {
        if (WorkingDirectory is null)
        {
            return "";
        }
        return $" in pwd {WorkingDirectory}";
    }

    private void AddEnvironmentVariablesTo(ProcessStartInfo psi)
    {
        foreach (var item in Environment)
        {
            _testOutput.WriteLine($"{_msgPrefix}\t[{item.Key}] = {item.Value}");
            psi.Environment[item.Key] = item.Value;
        }
    }

    private void AddWorkingDirectoryTo(ProcessStartInfo psi)
    {
        if (!string.IsNullOrWhiteSpace(WorkingDirectory))
        {
            if (!Directory.Exists(WorkingDirectory))
            {
                throw new DirectoryNotFoundException($"Working directory '{WorkingDirectory}' does not exist.");
            }
            psi.WorkingDirectory = WorkingDirectory;
        }
    }
}

/// <summary>
/// Result of a command execution.
/// </summary>
public readonly struct CommandResult
{
    public ProcessStartInfo StartInfo { get; }
    public int ExitCode { get; }
    public string Output { get; }

    public CommandResult(ProcessStartInfo startInfo, int exitCode, string output)
    {
        StartInfo = startInfo;
        ExitCode = exitCode;
        Output = output;
    }

    public CommandResult EnsureSuccessful(string messagePrefix = "")
        => EnsureExitCode(0, messagePrefix);

    public CommandResult EnsureExitCode(int expectedExitCode, string messagePrefix = "")
    {
        if (ExitCode != expectedExitCode)
        {
            var message = $"{messagePrefix} Expected {expectedExitCode} exit code but got {ExitCode}: {StartInfo.FileName} {StartInfo.Arguments}";
            if (!string.IsNullOrEmpty(Output))
            {
                message += $"{System.Environment.NewLine}Output:{System.Environment.NewLine}{Output}";
            }
            throw new CommandException(message, this);
        }
        return this;
    }
}

/// <summary>
/// Exception thrown when a command fails.
/// </summary>
public class CommandException : Exception
{
    public CommandResult Result { get; }

    public CommandException(string message, CommandResult result) : base(message)
    {
        Result = result;
    }
}

/// <summary>
/// Extension methods for Process handling.
/// </summary>
internal static class ProcessExtensions
{
    public static bool TryGetHasExited(this Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (InvalidOperationException ie) when (ie.Message.Contains("No process is associated with this object"))
        {
            return true;
        }
    }

    public static void CloseAndKillProcessIfRunning(this Process? process)
    {
        if (process is null || process.TryGetHasExited())
        {
            return;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            process.CloseMainWindow();
        }
        process.Kill(entireProcessTree: true);
        process.Dispose();
    }
}
