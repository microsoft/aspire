// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Runtime.InteropServices;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Dcp.Process;

internal static partial class ProcessUtil
{
    #region Native Methods

    [LibraryImport("libc", SetLastError = true, EntryPoint = "kill")]
    private static partial int sys_kill(int pid, int sig);

    #endregion

    private static readonly TimeSpan s_processExitTimeout = TimeSpan.FromSeconds(5);

    public static (Task<ProcessResult>, IAsyncDisposable) Run(ProcessSpec processSpec)
    {
        if (processSpec.Arguments is not null && processSpec.ArgumentList is not null)
        {
            throw new InvalidOperationException($"Specify either {nameof(ProcessSpec.Arguments)} or {nameof(ProcessSpec.ArgumentList)}, not both.");
        }

        var retainedOutputLineCount = processSpec.RetainedOutputLineCount ?? (processSpec.ThrowOnNonZeroReturnCode ? ProcessSpec.DefaultRetainedOutputLineCount : 0);
        ArgumentOutOfRangeException.ThrowIfNegative(retainedOutputLineCount);

        ProcessOutputCapture? outputCapture = retainedOutputLineCount > 0
            ? new(retainedOutputLineCount)
            : null;

        var resolvedExecutablePath = processSpec.ResolveExecutablePath
            ? PathLookupHelper.ResolveExecutablePath(processSpec.ExecutablePath, processSpec.EnvironmentVariables)
            : processSpec.ExecutablePath;

        var process = new System.Diagnostics.Process()
        {
            StartInfo =
            {
                FileName = resolvedExecutablePath,
                WorkingDirectory = processSpec.WorkingDirectory ?? string.Empty,
                Arguments = processSpec.Arguments ?? string.Empty,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = processSpec.StandardInputContent != null,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            },
            EnableRaisingEvents = true
        };

        if (processSpec.ArgumentList is not null)
        {
            foreach (var argument in processSpec.ArgumentList)
            {
                process.StartInfo.ArgumentList.Add(argument);
            }
        }

        if (!processSpec.InheritEnv)
        {
            process.StartInfo.Environment.Clear();
        }

        foreach (var (key, value) in processSpec.EnvironmentVariables)
        {
            process.StartInfo.Environment[key] = value;
        }

        // Use a reset event to prevent output processing and exited events from running until OnStart is complete.
        // OnStart might have logic that sets up data structures that then are used by these events.
        var startupComplete = new ManualResetEventSlim(false);

        // Note: even though the child process has exited, its children may be alive and still producing output.
        // See https://github.com/dotnet/runtime/issues/29232#issuecomment-1451584094 for how this might affect waiting for process exit.
        // We are going to discard that (grandchild) output by checking process.HasExited.

        var stdoutComplete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stderrComplete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        process.OutputDataReceived += (_, e) =>
        {
            startupComplete.Wait();

            if (e.Data is null)
            {
                stdoutComplete.TrySetResult();
                return;
            }

            if (string.IsNullOrEmpty(e.Data))
            {
                return;
            }

            outputCapture?.Add(e.Data);
            processSpec.OnOutputData?.Invoke(e.Data);
        };

        process.ErrorDataReceived += (_, e) =>
        {
            startupComplete.Wait();

            if (e.Data is null)
            {
                stderrComplete.TrySetResult();
                return;
            }

            if (string.IsNullOrEmpty(e.Data))
            {
                return;
            }

            outputCapture?.Add(e.Data);
            processSpec.OnErrorData?.Invoke(e.Data);
        };

        var processLifetimeTcs = new TaskCompletionSource<ProcessResult>();

        try
        {
#if ASPIRE_EVENTSOURCE
            AspireEventSource.Instance.ProcessLaunchStart(processSpec.ExecutablePath, FormatProcessArguments(processSpec));
#endif

            process.Start();
            processSpec.OnStart?.Invoke(process.Id);
            startupComplete.Set();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            // Write standard input after output reads are active so processes can write before reading input.
            if (processSpec.StandardInputContent != null)
            {
                var writer = process.StandardInput;
                writer.WriteLine(processSpec.StandardInputContent);
                writer.Flush();
                writer.Close();
            }

            _ = Task.Run(async () =>
            {
                startupComplete.Wait();

                try
                {
                    await process.WaitForExitAsync().ConfigureAwait(false);
                    await Task.WhenAll(stdoutComplete.Task, stderrComplete.Task).ConfigureAwait(false);

                    processSpec.OnStop?.Invoke(process.ExitCode);

                    if (processSpec.ThrowOnNonZeroReturnCode && process.ExitCode != 0)
                    {
                        var message = $"Command {processSpec.ExecutablePath} {FormatProcessArguments(processSpec)} returned non-zero exit code {process.ExitCode}";

                        if (outputCapture?.TotalLineCount > 0)
                        {
                            message = $"{message}{Environment.NewLine}{outputCapture.GetFormattedOutput()}";
                        }

                        processLifetimeTcs.TrySetException(new InvalidOperationException(message));
                    }
                    else
                    {
                        processLifetimeTcs.TrySetResult(CreateProcessResult(process.ExitCode, outputCapture));
                    }
                }
                catch (Exception ex)
                {
                    processLifetimeTcs.TrySetException(ex);
                }
            });
        }
        finally
        {
            startupComplete.Set(); // Allow output/error/exit handlers to start processing data.
#if ASPIRE_EVENTSOURCE
            AspireEventSource.Instance.ProcessLaunchStop(processSpec.ExecutablePath, FormatProcessArguments(processSpec));
#endif
        }

        return (processLifetimeTcs.Task, new ProcessDisposable(process, processLifetimeTcs.Task, processSpec.KillEntireProcessTree));
    }

    private static string FormatProcessArguments(ProcessSpec processSpec)
    {
        return processSpec.Arguments ?? string.Join(" ", processSpec.ArgumentList ?? []);
    }

    private static ProcessResult CreateProcessResult(int exitCode, ProcessOutputCapture? outputCapture)
    {
        if (outputCapture is null)
        {
            return new ProcessResult(exitCode);
        }

        return new ProcessResult(exitCode, outputCapture.ToArray(), outputCapture.TotalLineCount);
    }

    private sealed class ProcessDisposable : IAsyncDisposable
    {
        private readonly System.Diagnostics.Process _process;
        private readonly Task _processLifetimeTask;
        private readonly bool _entireProcessTree;

        public ProcessDisposable(System.Diagnostics.Process process, Task processLifetimeTask, bool entireProcessTree)
        {
            _process = process;
            _processLifetimeTask = processLifetimeTask;
            _entireProcessTree = entireProcessTree;
        }

        public async ValueTask DisposeAsync()
        {
            if (_process.HasExited)
            {
                return; // nothing to do
            }

            if (OperatingSystem.IsWindows())
            {
                if (!_process.CloseMainWindow())
                {
                    _process.Kill(_entireProcessTree);
                }
            }
            else
            {
                sys_kill(_process.Id, sig: 2); // SIGINT
            }

            try
            {
                await _processLifetimeTask.WaitAsync(s_processExitTimeout).ConfigureAwait(false);
            }
            catch (TimeoutException) when (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                await _processLifetimeTask.WaitAsync(s_processExitTimeout).ConfigureAwait(false);
            }

            if (!_process.HasExited)
            {
                // Always try to kill the entire process tree here if all of the above has failed.
                _process.Kill(entireProcessTree: true);
            }
        }
    }
}
