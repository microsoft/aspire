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
/// <param name="logger">The logger for diagnostic output.</param>
/// <param name="profilingTelemetry">The profiling telemetry service.</param>
internal sealed class GitRepository(CliExecutionContext executionContext, ILogger<GitRepository> logger, ProfilingTelemetry profilingTelemetry) : IGitRepository
{
    /// <inheritdoc />
    public async Task<DirectoryInfo?> GetRootAsync(CancellationToken cancellationToken)
    {
        logger.LogDebug("Searching for Git repository root from working directory: {WorkingDirectory}", executionContext.WorkingDirectory.FullName);

        try
        {
            var startInfo = new ProcessStartInfo("git")
            {
                WorkingDirectory = executionContext.WorkingDirectory.FullName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("rev-parse");
            startInfo.ArgumentList.Add("--show-toplevel");

            using var activity = profilingTelemetry.StartGitCommand("rev-parse", startInfo.ArgumentList.Count, executionContext.WorkingDirectory);

            var result = await Process.RunAndCaptureTextAsync(startInfo, cancellationToken).ConfigureAwait(false);
            activity.SetProcessId(result.ProcessId);
            activity.SetProcessExitCode(result.ExitStatus.ExitCode);
            activity.SetGitOutputLengths(result.StandardOutput.Length, result.StandardError.Length);

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
            var startInfo = new ProcessStartInfo("git")
            {
                WorkingDirectory = searchRoot.FullName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            // -z separates entries with NUL so paths containing newlines or spaces are unambiguous.
            // --cached: tracked files. --others: untracked files. --exclude-standard: respect .gitignore,
            // .git/info/exclude, and the user's global excludesfile. Submodule contents are not enumerated.
            startInfo.ArgumentList.Add("ls-files");
            startInfo.ArgumentList.Add("--cached");
            startInfo.ArgumentList.Add("--others");
            startInfo.ArgumentList.Add("--exclude-standard");
            startInfo.ArgumentList.Add("-z");

            using var activity = profilingTelemetry.StartGitCommand("ls-files", startInfo.ArgumentList.Count, searchRoot);

            var result = await Process.RunAndCaptureTextAsync(startInfo, cancellationToken).ConfigureAwait(false);
            activity.SetProcessId(result.ProcessId);
            activity.SetProcessExitCode(result.ExitStatus.ExitCode);
            activity.SetGitOutputLengths(result.StandardOutput.Length, result.StandardError.Length);

            if (result.ExitStatus.ExitCode != 0)
            {
                activity.SetError($"git ls-files exited with code {result.ExitStatus.ExitCode}.");
                logger.LogDebug("git ls-files returned non-zero exit code {ExitCode} from {SearchRoot}: {Error}", result.ExitStatus.ExitCode, searchRoot.FullName, result.StandardError.Trim());
                return null;
            }

            var pathComparer = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal;
            var includedFiles = new HashSet<string>(pathComparer);

            var rootFullName = searchRoot.FullName;
            foreach (var rawPath in result.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                // git always emits paths with '/' separators; normalize to the OS separator.
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
}
