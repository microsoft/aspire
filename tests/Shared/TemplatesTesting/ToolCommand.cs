// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Runtime.InteropServices;
using Aspire.TestUtilities;
using Xunit;

namespace Aspire.Templates.Tests;

public class ToolCommand : IDisposable
{
    private readonly string _label;
    private TimeSpan? _timeout;
    private readonly string _msgPrefix;
    protected ITestOutputHelper _testOutput;

    protected string _command;

    public Process? CurrentProcess { get; private set; }

    public Dictionary<string, string> Environment { get; } = new Dictionary<string, string>();

    public event DataReceivedEventHandler? ErrorDataReceived;

    public event DataReceivedEventHandler? OutputDataReceived;

    public string? WorkingDirectory { get; set; }

    public ToolCommand(string command, ITestOutputHelper testOutput, string label="")
    {
        _command = command;
        _testOutput = testOutput;
        _label = label;
        _msgPrefix = string.IsNullOrEmpty(_label) ? string.Empty : $"[{_label}] ";
    }

    public ToolCommand WithWorkingDirectory(string dir)
    {
        WorkingDirectory = dir;
        return this;
    }

    public ToolCommand WithEnvironmentVariable(string key, string value)
    {
        Environment[key] = value;
        return this;
    }

    public ToolCommand WithEnvironmentVariables(IDictionary<string, string>? extraEnvVars)
    {
        if (extraEnvVars != null)
        {
            foreach ((string key, string value) in extraEnvVars)
            {
                Environment[key] = value;
            }
        }

        return this;
    }

    public ToolCommand WithOutputDataReceived(Action<string?> handler)
    {
        OutputDataReceived += (_, args) => handler(args.Data);
        return this;
    }

    public ToolCommand WithErrorDataReceived(Action<string?> handler)
    {
        ErrorDataReceived += (_, args) => handler(args.Data);
        return this;
    }

    public ToolCommand WithTimeout(TimeSpan timeSpan)
    {
        _timeout = timeSpan;
        return this;
    }

    public virtual async Task<CommandResult> ExecuteAsync(params string[] args)
    {
        var resolvedCommand = _command;
        string fullArgs = GetFullArgs(args);
        CancellationTokenSource cts = new();
        if (_timeout is not null)
        {
            cts.CancelAfter((int)_timeout.Value.TotalMilliseconds);
        }
        try
        {
            return await ExecuteAsyncInternal(resolvedCommand, fullArgs, cts.Token);
        }
        catch (TaskCanceledException tce) when (cts.IsCancellationRequested)
        {
            throw new TaskCanceledException($"Command execution timed out after {_timeout!.Value.TotalSeconds} secs: '{resolvedCommand} {fullArgs}'", tce);
        }
    }

    public virtual void Dispose()
    {
        CurrentProcess?.CloseAndKillProcessIfRunning();
    }

    protected virtual string GetFullArgs(params string[] args) => string.Join(" ", args);

    private async Task<CommandResult> ExecuteAsyncInternal(string executable, string args, CancellationToken token)
    {
        Stopwatch runTimeStopwatch = new();
        _testOutput.WriteLine($"{_msgPrefix}Executing - {executable} {args} {WorkingDirectoryInfo()}");
        object outputLock = new();
        var outputLines = new List<string>();
        CurrentProcess = CreateProcess(executable, args);
        CurrentProcess.ErrorDataReceived += (s, e) =>
        {
            if (e.Data == null)
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
            if (e.Data == null)
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

            // Start
            CurrentProcess.Start();
            CurrentProcess.BeginOutputReadLine();
            CurrentProcess.BeginErrorReadLine();
            await exitedTcs.Task.WaitAsync(token).ConfigureAwait(false);

            // Exited, wait for output to complete now
            _testOutput.WriteLine($"{_msgPrefix}Got the Exited event, and waiting on WaitForExitAsync now");
            var waitForExitTask = CurrentProcess.WaitForExitAsync(token);
            var completedTask = await Task.WhenAny(waitForExitTask, Task.Delay(TimeSpan.FromSeconds(5), token)).ConfigureAwait(false);
            if (completedTask != waitForExitTask)
            {
                _testOutput.WriteLine($"{_msgPrefix}Timed out waiting for it. Ignoring.");
            }

            _testOutput.WriteLine($"{_msgPrefix}Process ran for {runTimeStopwatch.Elapsed.TotalSeconds} secs");

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
                _testOutput.WriteLine($"{_msgPrefix}Process has been running for {runTimeStopwatch.Elapsed.TotalSeconds} secs");
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

    private Process CreateProcess(string executable, string args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = executable,
            Arguments = args,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            RedirectStandardInput = true,
            UseShellExecute = false
        };

        psi.Environment["DOTNET_MULTILEVEL_LOOKUP"] = "0";
        psi.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";

        // runtime repo sets this, which interferes with the tests
        psi.EnvironmentVariables.Remove("MSBuildSDKsPath");

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
        if (WorkingDirectory == null)
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
