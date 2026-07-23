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
        return await GetRootAsync(executionContext.WorkingDirectory, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<DirectoryInfo?> GetRootAsync(DirectoryInfo startDirectory, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(startDirectory);

        if (!startDirectory.Exists)
        {
            logger.LogDebug("Git repository discovery directory does not exist: {StartDirectory}", startDirectory.FullName);
            return null;
        }

        logger.LogDebug("Searching for Git repository root from directory: {StartDirectory}", startDirectory.FullName);

        var result = await RunGitAsync(
            startDirectory,
            "rev-parse",
            ["rev-parse", "--show-toplevel"],
            standardInput: null,
            exitCode => exitCode == 0,
            cancellationToken).ConfigureAwait(false);

        if (result is null)
        {
            return null;
        }

        if (result.ExitCode != 0)
        {
            logger.LogDebug(
                "git rev-parse returned non-zero exit code {ExitCode} from {StartDirectory}: {Error}",
                result.ExitCode,
                startDirectory.FullName,
                result.StandardError.Trim());
            return null;
        }

        var rootPath = result.StandardOutput.Trim();
        if (string.IsNullOrEmpty(rootPath))
        {
            logger.LogDebug("git rev-parse returned empty output from {StartDirectory}", startDirectory.FullName);
            return null;
        }

        var directoryInfo = new DirectoryInfo(rootPath);
        if (!directoryInfo.Exists)
        {
            logger.LogDebug("Git repository root path does not exist: {GitRoot}", rootPath);
            return null;
        }

        logger.LogDebug("Found Git repository root: {GitRoot}", directoryInfo.FullName);
        return directoryInfo;
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

        var repositoryRoot = await GetRootAsync(searchRoot, cancellationToken).ConfigureAwait(false);
        if (repositoryRoot is null)
        {
            return null;
        }

        return await GetIncludedFilesAsync(repositoryRoot, [searchRoot.FullName], cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlySet<string>?> GetIncludedFilesAsync(
        DirectoryInfo repositoryRoot,
        IReadOnlyList<string> searchPaths,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(repositoryRoot);
        ArgumentNullException.ThrowIfNull(searchPaths);

        if (!repositoryRoot.Exists)
        {
            logger.LogDebug("Explicit Git repository root does not exist: {RepositoryRoot}", repositoryRoot.FullName);
            return null;
        }

        if (searchPaths.Count == 0)
        {
            return new HashSet<string>(PathComparer);
        }

        var arguments = new List<string>
        {
            "ls-files",
            "--cached",
            "--others",
            "--exclude-standard",
            "-z"
        };

        var relativeSearchPaths = searchPaths
            .Select(path => GetRepositoryRelativePath(repositoryRoot.FullName, path))
            .Distinct(PathComparer)
            .ToArray();

        if (!relativeSearchPaths.Any(string.IsNullOrEmpty))
        {
            arguments.Add("--");
            arguments.AddRange(relativeSearchPaths.Select(ToLiteralPathspec));
        }

        logger.LogDebug(
            "Listing Git-included files under {PathCount} paths from repository root {RepositoryRoot}.",
            searchPaths.Count,
            repositoryRoot.FullName);

        var result = await RunGitAsync(
            repositoryRoot,
            "ls-files",
            arguments,
            standardInput: null,
            exitCode => exitCode == 0,
            cancellationToken).ConfigureAwait(false);

        if (result is null)
        {
            return null;
        }

        if (result.ExitCode != 0)
        {
            logger.LogDebug(
                "git ls-files returned non-zero exit code {ExitCode} from {RepositoryRoot}: {Error}",
                result.ExitCode,
                repositoryRoot.FullName,
                result.StandardError.Trim());
            return null;
        }

        var includedFiles = ParseAbsolutePaths(repositoryRoot.FullName, result.StandardOutput);
        logger.LogDebug(
            "git ls-files returned {Count} files for {PathCount} paths under {RepositoryRoot}.",
            includedFiles.Count,
            searchPaths.Count,
            repositoryRoot.FullName);
        return includedFiles;
    }

    /// <inheritdoc />
    public async Task<IReadOnlySet<string>?> GetIgnoredFilesAsync(
        DirectoryInfo repositoryRoot,
        IReadOnlyList<string> candidatePaths,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(repositoryRoot);
        ArgumentNullException.ThrowIfNull(candidatePaths);

        if (!repositoryRoot.Exists)
        {
            logger.LogDebug("Explicit Git repository root does not exist: {RepositoryRoot}", repositoryRoot.FullName);
            return null;
        }

        if (candidatePaths.Count == 0)
        {
            return new HashSet<string>(PathComparer);
        }

        var relativeCandidates = candidatePaths
            .Select(path => GetRepositoryRelativePath(repositoryRoot.FullName, path))
            .Distinct(PathComparer)
            .ToArray();

        // `git check-ignore --stdin -z` consumes and emits NUL-delimited repository-relative
        // paths, for example `.configgen/a b.json\0.pipelines/line\nbreak.yml\0`.
        // Exit code 1 is the documented success result when none of the paths are ignored.
        var standardInput = string.Join('\0', relativeCandidates) + '\0';
        var result = await RunGitAsync(
            repositoryRoot,
            "check-ignore",
            ["check-ignore", "--no-index", "-z", "--stdin"],
            standardInput,
            exitCode => exitCode is 0 or 1,
            cancellationToken).ConfigureAwait(false);

        if (result is null)
        {
            return null;
        }

        if (result.ExitCode is not (0 or 1))
        {
            logger.LogDebug(
                "git check-ignore returned unexpected exit code {ExitCode} from {RepositoryRoot}: {Error}",
                result.ExitCode,
                repositoryRoot.FullName,
                result.StandardError.Trim());
            return null;
        }

        return ParseAbsolutePaths(repositoryRoot.FullName, result.StandardOutput);
    }

    private StringComparer PathComparer =>
        environment.IsWindows() || environment.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    private HashSet<string> ParseAbsolutePaths(string repositoryRoot, string output)
    {
        var paths = new HashSet<string>(PathComparer);

        // Git's -z output is repository-relative and always uses '/' separators. The trailing
        // NUL produces an empty split entry, while embedded spaces and newlines remain unambiguous.
        foreach (var rawPath in output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var relativePath = Path.DirectorySeparatorChar == '/'
                ? rawPath
                : rawPath.Replace('/', Path.DirectorySeparatorChar);
            paths.Add(Path.GetFullPath(relativePath, repositoryRoot));
        }

        return paths;
    }

    private static string GetRepositoryRelativePath(string repositoryRoot, string path)
    {
        var fullPath = Path.GetFullPath(path);
        var relativePath = Path.GetRelativePath(repositoryRoot, fullPath);
        if (relativePath == ".." ||
            relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
            Path.IsPathRooted(relativePath))
        {
            throw new ArgumentException(
                $"Path '{fullPath}' is outside Git repository root '{repositoryRoot}'.",
                nameof(path));
        }

        if (relativePath == ".")
        {
            return string.Empty;
        }

        return relativePath.Replace(Path.DirectorySeparatorChar, '/');
    }

    private static string ToLiteralPathspec(string relativePath)
    {
        return $":(top,literal){relativePath}";
    }

    private async Task<GitProcessResult?> RunGitAsync(
        DirectoryInfo workingDirectory,
        string command,
        IReadOnlyList<string> arguments,
        string? standardInput,
        Func<int, bool> isExpectedExitCode,
        CancellationToken cancellationToken)
    {
        try
        {
            var startInfo = new ProcessStartInfo("git")
            {
                WorkingDirectory = workingDirectory.FullName,
                RedirectStandardInput = standardInput is not null,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = new Process { StartInfo = startInfo };
            using var activity = profilingTelemetry.StartGitCommand(command, startInfo.FileName, startInfo.ArgumentList, workingDirectory);

            process.Start();
            activity.SetProcessId(process.Id);
            using var cancellationRegistration = RegisterProcessKillOnCancellation(process, cancellationToken);

            // Read both streams concurrently to avoid deadlock when a pipe buffer fills. Standard
            // input is also written concurrently because check-ignore can emit while consuming.
            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            var inputTask = standardInput is null
                ? Task.CompletedTask
                : WriteStandardInputAsync(process, standardInput, cancellationToken);

            await Task.WhenAll(process.WaitForExitAsync(cancellationToken), inputTask).ConfigureAwait(false);

            var output = await outputTask.ConfigureAwait(false);
            var errorOutput = await errorTask.ConfigureAwait(false);
            activity.SetProcessExitCode(process.ExitCode);
            activity.SetGitOutputLengths(output.Length, errorOutput.Length);

            if (!isExpectedExitCode(process.ExitCode))
            {
                activity.SetError($"git {command} exited with code {process.ExitCode}.");
            }

            return new GitProcessResult(process.ExitCode, output, errorOutput);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Ambient discovery treats null as "git acceleration unavailable"; verification treats
            // it as a hard failure. The caller chooses based on the operation being performed.
            logger.LogDebug(ex, "Git is not installed or not found in PATH");
            return null;
        }
    }

    private static async Task WriteStandardInputAsync(
        Process process,
        string standardInput,
        CancellationToken cancellationToken)
    {
        await process.StandardInput.WriteAsync(standardInput.AsMemory(), cancellationToken).ConfigureAwait(false);
        await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
        process.StandardInput.Close();
    }

    private static CancellationTokenRegistration RegisterProcessKillOnCancellation(Process process, CancellationToken cancellationToken)
    {
        // Process.WaitForExitAsync(cancellationToken) cancels the wait but does not terminate the
        // child process. These are short-lived git commands owned by the CLI, so Ctrl+C should stop
        // the git process tree instead of leaving `git ls-files` walking a large repo after exit.
        if (!cancellationToken.CanBeCanceled)
        {
            return default;
        }

        return cancellationToken.Register(static state =>
        {
            var process = (Process)state!;
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
                // The process can exit between HasExited and Kill. Cancellation already won, so
                // cleanup is best effort.
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // Process termination can race with OS teardown or permission checks. Treat that
                // the same as an already-exited process rather than surfacing a secondary error.
            }
        }, process);
    }

    private sealed record GitProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
