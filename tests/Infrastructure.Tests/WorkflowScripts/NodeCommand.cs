// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Runtime.InteropServices;
using Aspire.TestUtilities;
using Xunit;

namespace Infrastructure.Tests;

/// <summary>
/// Executes Node.js scripts using <c>node</c>.
/// </summary>
public sealed class NodeCommand : IDisposable
{
    private readonly ITestOutputHelper _testOutput;
    private readonly string _label;
    private readonly string _msgPrefix;
    private TimeSpan? _timeout;

    public NodeCommand(ITestOutputHelper testOutput, string label = "")
    {
        _testOutput = testOutput;
        _label = label;
        _msgPrefix = string.IsNullOrEmpty(_label) ? string.Empty : $"[{_label}] ";
    }

    public Process? CurrentProcess { get; private set; }
    public Dictionary<string, string> Environment { get; } = [];
    public string? WorkingDirectory { get; private set; }

    public NodeCommand WithEnvironmentVariable(string key, string value)
    {
        Environment[key] = value;
        return this;
    }

    public NodeCommand WithTimeout(TimeSpan timeSpan)
    {
        _timeout = timeSpan;
        return this;
    }

    public NodeCommand WithWorkingDirectory(string dir)
    {
        WorkingDirectory = dir;
        return this;
    }

    public async Task<CommandResult> ExecuteScriptAsync(string scriptPath, params string[] args)
    {
        using CancellationTokenSource cts = new();
        if (_timeout is not null)
        {
            cts.CancelAfter((int)_timeout.Value.TotalMilliseconds);
        }

        try
        {
            return await ExecuteScriptAsyncInternal(scriptPath, args, cts.Token).ConfigureAwait(false);
        }
        catch (TaskCanceledException tce) when (cts.IsCancellationRequested)
        {
            throw new TaskCanceledException(
                $"Command execution timed out after {_timeout!.Value.TotalSeconds} secs: node {scriptPath}",
                tce);
        }
    }

    public void Dispose()
    {
        CurrentProcess?.CloseAndKillProcessIfRunning();
    }

    private async Task<CommandResult> ExecuteScriptAsyncInternal(string scriptPath, string[] args, CancellationToken token)
    {
        Stopwatch runTimeStopwatch = new();
        _testOutput.WriteLine($"{_msgPrefix}Executing - node {BuildDisplayArguments(scriptPath, args)} {WorkingDirectoryInfo()}");

        object outputLock = new();
        List<string> outputLines = [];

        CurrentProcess = CreateProcess(scriptPath, args);

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
        };

        try
        {
            runTimeStopwatch.Start();

            TaskCompletionSource exitedTcs = new();
            CurrentProcess.EnableRaisingEvents = true;
            CurrentProcess.Exited += (_, _) =>
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

                _testOutput.WriteLine("Killing");
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

    private Process CreateProcess(string scriptPath, string[] args)
    {
        ProcessStartInfo psi = new()
        {
            FileName = "node",
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            RedirectStandardInput = true,
            UseShellExecute = false
        };

        psi.ArgumentList.Add(scriptPath);
        foreach (string arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        AddEnvironmentVariablesTo(psi);
        AddWorkingDirectoryTo(psi);

        return new Process
        {
            StartInfo = psi,
            EnableRaisingEvents = true
        };
    }

    private void AddEnvironmentVariablesTo(ProcessStartInfo psi)
    {
        foreach ((string key, string value) in Environment)
        {
            _testOutput.WriteLine($"{_msgPrefix}\t[{key}] = {value}");
            psi.Environment[key] = value;
        }
    }

    private void AddWorkingDirectoryTo(ProcessStartInfo psi)
    {
        if (string.IsNullOrWhiteSpace(WorkingDirectory))
        {
            return;
        }

        if (!Directory.Exists(WorkingDirectory))
        {
            throw new DirectoryNotFoundException($"Working directory '{WorkingDirectory}' does not exist.");
        }

        psi.WorkingDirectory = WorkingDirectory;
    }

    private static string BuildDisplayArguments(string scriptPath, string[] args)
        => string.Join(" ", [QuoteForDisplay(scriptPath), .. args.Select(QuoteForDisplay)]);

    private static string QuoteForDisplay(string value)
        => value.Contains(' ') ? $"\"{value}\"" : value;

    private string WorkingDirectoryInfo()
        => WorkingDirectory is null ? string.Empty : $" in pwd {WorkingDirectory}";
}
