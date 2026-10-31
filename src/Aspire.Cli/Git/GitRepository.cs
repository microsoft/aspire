// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Cli.Telemetry;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Git;

/// <summary>
/// Provides Git repository operations.
/// </summary>
/// <param name="executionContext">The CLI execution context providing the working directory.</param>
/// <param name="environment">The environment abstraction for OS detection.</param>
/// <param name="logger">The logger for diagnostic output.</param>
/// <param name="profilingTelemetry">The profiling telemetry service.</param>
internal sealed class GitRepository(CliExecutionContext executionContext, IEnvironment environment, ILogger<GitRepository> logger, ProfilingTelemetry profilingTelemetry) : IGitRepository
{
    /// <inheritdoc />
    public async Task<DirectoryInfo?> GetRootAsync(CancellationToken cancellationToken)
    {
        logger.LogDebug("Searching for Git repository root from working directory: {WorkingDirectory}", executionContext.WorkingDirectory.FullName);

        try
        {
            var startInfo = CreateGitStartInfo(executionContext.WorkingDirectory, ["rev-parse", "--show-toplevel"]);

            using var activity = profilingTelemetry.StartGitCommand("rev-parse", startInfo.FileName, startInfo.ArgumentList, executionContext.WorkingDirectory);
            var result = await RunGitAsync(startInfo, activity, cancellationToken).ConfigureAwait(false);

            if (result.ExitCode != 0)
            {
                activity.SetError($"git rev-parse exited with code {result.ExitCode}.");
                logger.LogDebug("Git command returned non-zero exit code {ExitCode}: {Error}", result.ExitCode, result.StandardError.Trim());
                return null;
            }

            if (result.StandardOutput is null)
            {
                return null;
            }

            var rootPath = result.StandardOutput.Trim();

            if (string.IsNullOrEmpty(rootPath))
            {
                logger.LogDebug("Git command returned empty output");
                return null;
            }

            var directoryInfo = new DirectoryInfo(rootPath);
            if (directoryInfo.Exists)
            {
                logger.LogDebug("Found Git repository root: {GitRoot}", directoryInfo.FullName);
                return directoryInfo;
            }

            logger.LogDebug("Git repository root path does not exist: {GitRoot}", rootPath);
            return null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Missing git is not fatal for callers. Ambient discovery treats null as
            // "git acceleration unavailable" and falls back to the filesystem walker.
            logger.LogDebug(ex, "Git is not installed or not found in PATH");
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlySet<string>?> GetIncludedFilesAsync(DirectoryInfo searchRoot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(searchRoot);

        if (!searchRoot.Exists)
        {
            logger.LogDebug("Search root does not exist: {SearchRoot}", searchRoot.FullName);
            return null;
        }

        logger.LogDebug("Listing git-included files under: {SearchRoot}", searchRoot.FullName);

        try
        {
            // -z separates entries with NUL so paths containing newlines or spaces are unambiguous.
            // --cached: tracked files. --others: untracked files. --exclude-standard: respect .gitignore,
            // .git/info/exclude, and the user's global excludesfile. Submodule contents are not enumerated.
            var startInfo = CreateGitStartInfo(searchRoot, ["ls-files", "--cached", "--others", "--exclude-standard", "-z"]);

            using var activity = profilingTelemetry.StartGitCommand("ls-files", startInfo.FileName, startInfo.ArgumentList, searchRoot);
            var result = await RunGitAsync(startInfo, activity, cancellationToken).ConfigureAwait(false);

            if (result.ExitCode != 0)
            {
                activity.SetError($"git ls-files exited with code {result.ExitCode}.");
                logger.LogDebug("git ls-files returned non-zero exit code {ExitCode} from {SearchRoot}: {Error}", result.ExitCode, searchRoot.FullName, result.StandardError.Trim());
                return null;
            }

            if (result.StandardOutput is null)
            {
                // Output may be incomplete, so fall back to the filesystem walker rather than
                // treating a partial file list as authoritative.
                return null;
            }

            var pathComparer = environment.IsWindows() || environment.IsMacOS()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal;
            var includedFiles = new HashSet<string>(pathComparer);

            var rootFullName = searchRoot.FullName;

            // `git ls-files -z` emits NUL-delimited paths relative to searchRoot, for example:
            // `src/AppHost/AppHost.csproj\0playground/apphost.ts\0`. Git always uses '/' as the
            // separator in this output, and the trailing NUL produces an empty split entry.
            foreach (var rawPath in result.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                var relativePath = Path.DirectorySeparatorChar == '/'
                    ? rawPath
                    : rawPath.Replace('/', Path.DirectorySeparatorChar);

                var absolutePath = Path.GetFullPath(Path.Combine(rootFullName, relativePath));
                includedFiles.Add(absolutePath);
            }

            logger.LogDebug("git ls-files returned {Count} files under {SearchRoot}", includedFiles.Count, searchRoot.FullName);
            return includedFiles;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Missing git is not fatal for callers. Ambient discovery treats null as
            // "git acceleration unavailable" and falls back to the filesystem walker.
            logger.LogDebug(ex, "Git is not installed or not found in PATH");
            return null;
        }
    }

    private static ProcessStartInfo CreateGitStartInfo(DirectoryInfo workingDirectory, string[] arguments) =>
        new("git", arguments)
        {
            WorkingDirectory = workingDirectory.FullName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

    private async Task<GitProcessOutput> RunGitAsync(ProcessStartInfo startInfo, ProfilingTelemetry.ActivityScope activity, CancellationToken cancellationToken)
    {
        // Don't use Process.RunAndCaptureTextAsync: it waits for EOF on stdout and stderr before
        // waiting for exit, with no bound. A process that inherited git's pipe handles (a hook
        // or helper git spawned, or an unrelated process spawned concurrently that inherited the
        // handles) can keep the pipes open long after git exits, which hangs the caller forever.
        // Wait for git to exit instead, and then bound the time spent draining its output.
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Failed to start '{startInfo.FileName}'.");
        activity.SetProcessId(process.Id);

        using var readCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var stdoutTask = process.StandardOutput.ReadToEndAsync(readCts.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(readCts.Token);

        try
        {
            try
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Kill git so Ctrl+C doesn't leave `git ls-files` walking a large repo.
                TryKill(process);
                throw;
            }

            var exitCode = process.ExitCode;
            activity.SetProcessExitCode(exitCode);

            try
            {
                await Task.WhenAll(stdoutTask, stderrTask).WaitAsync(s_postExitOutputDrainTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                logger.LogDebug("git {Arguments} exited with code {ExitCode} but its output pipes were still open after {Timeout}s; another process inherited them.", string.Join(' ', startInfo.ArgumentList), exitCode, s_postExitOutputDrainTimeout.TotalSeconds);
            }

            // git has exited, so stdout is complete once it reaches EOF. If only stderr is held
            // open, the stdout result is still trustworthy. If stdout never reached EOF the output
            // may be truncated, so report it as unavailable and let callers fall back.
            if (!stdoutTask.IsCompletedSuccessfully)
            {
                activity.SetError("git output pipe was not closed after the process exited.");
                return new GitProcessOutput(exitCode, StandardOutput: null, StandardError: string.Empty);
            }

            var standardOutput = stdoutTask.Result;
            var standardError = stderrTask.IsCompletedSuccessfully ? stderrTask.Result : string.Empty;
            activity.SetGitOutputLengths(standardOutput.Length, standardError.Length);
            return new GitProcessOutput(exitCode, standardOutput, standardError);
        }
        finally
        {
            // Stop any read that's still blocked on a pipe held open by another process.
            readCts.Cancel();
            ObserveFault(stdoutTask);
            ObserveFault(stderrTask);
        }
    }

    private void TryKill(Process process)
    {
        try
        {
            process.Kill();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            logger.LogDebug(ex, "Could not kill git process.");
        }
    }

    private static void ObserveFault(Task task) =>
        _ = task.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    // git normally closes its pipes when it exits, so the drain completes immediately. This only
    // bounds how long we wait when another process is holding them open.
    private static readonly TimeSpan s_postExitOutputDrainTimeout = TimeSpan.FromSeconds(2);

    private readonly record struct GitProcessOutput(int ExitCode, string? StandardOutput, string StandardError);
}
