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

            if (result.ExitStatus.ExitCode != 0)
            {
                activity.SetError($"git rev-parse exited with code {result.ExitStatus.ExitCode}.");
                logger.LogDebug("Git command returned non-zero exit code {ExitCode}: {Error}", result.ExitStatus.ExitCode, result.StandardError.Trim());
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

            if (result.ExitStatus.ExitCode != 0)
            {
                activity.SetError($"git ls-files exited with code {result.ExitStatus.ExitCode}.");
                logger.LogDebug("git ls-files returned non-zero exit code {ExitCode} from {SearchRoot}: {Error}", result.ExitStatus.ExitCode, searchRoot.FullName, result.StandardError.Trim());
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

    private static async Task<ProcessTextOutput> RunGitAsync(ProcessStartInfo startInfo, ProfilingTelemetry.ActivityScope activity, CancellationToken cancellationToken)
    {
        // RunAndCaptureTextAsync drains stdout and stderr concurrently, and kills git when
        // cancellationToken fires so Ctrl+C doesn't leave `git ls-files` walking a large repo.
        var result = await Process.RunAndCaptureTextAsync(startInfo, cancellationToken).ConfigureAwait(false);
        activity.SetProcessId(result.ProcessId);
        activity.SetProcessExitCode(result.ExitStatus.ExitCode);
        activity.SetGitOutputLengths(result.StandardOutput.Length, result.StandardError.Length);
        return result;
    }
}
