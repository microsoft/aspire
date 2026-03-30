// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Pipelines.GitHubActions;

/// <summary>
/// Helper for executing Git commands from the hosting extension.
/// </summary>
internal static class GitHelper
{
    /// <summary>
    /// Checks whether the given directory is inside a Git work tree.
    /// </summary>
    public static async Task<bool> IsGitRepoAsync(string directory, ILogger logger, CancellationToken ct = default)
    {
        var (exitCode, _) = await RunGitAsync(directory, "rev-parse --is-inside-work-tree", logger, ct).ConfigureAwait(false);
        return exitCode == 0;
    }

    /// <summary>
    /// Gets the root directory of the Git repository containing <paramref name="directory"/>.
    /// </summary>
    public static async Task<string?> GetRepoRootAsync(string directory, ILogger logger, CancellationToken ct = default)
    {
        var (exitCode, output) = await RunGitAsync(directory, "rev-parse --show-toplevel", logger, ct).ConfigureAwait(false);
        return exitCode == 0 ? output.Trim() : null;
    }

    /// <summary>
    /// Gets the URL of the named Git remote, or <c>null</c> if none exists.
    /// </summary>
    public static async Task<string?> GetRemoteUrlAsync(string directory, ILogger logger, string remote = "origin", CancellationToken ct = default)
    {
        var (exitCode, output) = await RunGitAsync(directory, $"remote get-url {remote}", logger, ct).ConfigureAwait(false);
        return exitCode == 0 ? output.Trim() : null;
    }

    /// <summary>
    /// Initializes a new Git repository in the given directory.
    /// </summary>
    public static async Task<bool> InitAsync(string directory, ILogger logger, CancellationToken ct = default)
    {
        var (exitCode, _) = await RunGitAsync(directory, "init", logger, ct).ConfigureAwait(false);
        return exitCode == 0;
    }

    /// <summary>
    /// Adds a remote to the Git repository.
    /// </summary>
    public static async Task<bool> AddRemoteAsync(string directory, string url, ILogger logger, string remote = "origin", CancellationToken ct = default)
    {
        var (exitCode, _) = await RunGitAsync(directory, $"remote add {remote} {url}", logger, ct).ConfigureAwait(false);
        return exitCode == 0;
    }

    /// <summary>
    /// Stages all files and creates a commit.
    /// </summary>
    public static async Task<bool> AddAllAndCommitAsync(string directory, string message, ILogger logger, CancellationToken ct = default)
    {
        var (addExit, _) = await RunGitAsync(directory, "add .", logger, ct).ConfigureAwait(false);
        if (addExit != 0)
        {
            return false;
        }

        var (commitExit, _) = await RunGitAsync(directory, $"commit -m \"{message}\"", logger, ct).ConfigureAwait(false);
        return commitExit == 0;
    }

    /// <summary>
    /// Pushes the current branch to the remote.
    /// </summary>
    public static async Task<bool> PushAsync(string directory, ILogger logger, string remote = "origin", string branch = "main", CancellationToken ct = default)
    {
        var (exitCode, _) = await RunGitAsync(directory, $"push -u {remote} {branch}", logger, ct).ConfigureAwait(false);
        return exitCode == 0;
    }

    /// <summary>
    /// Gets the current branch name.
    /// </summary>
    public static async Task<string?> GetCurrentBranchAsync(string directory, ILogger logger, CancellationToken ct = default)
    {
        var (exitCode, output) = await RunGitAsync(directory, "branch --show-current", logger, ct).ConfigureAwait(false);
        return exitCode == 0 ? output.Trim() : null;
    }

    private static async Task<(int ExitCode, string Output)> RunGitAsync(string workingDirectory, string arguments, ILogger logger, CancellationToken ct)
    {
        try
        {
            var startInfo = new ProcessStartInfo("git", arguments)
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = startInfo };
            process.Start();

            var outputTask = process.StandardOutput.ReadToEndAsync(ct);
            var errorTask = process.StandardError.ReadToEndAsync(ct);

            await process.WaitForExitAsync(ct).ConfigureAwait(false);

            var output = await outputTask.ConfigureAwait(false);
            var error = await errorTask.ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                logger.LogDebug("git {Arguments} exited with code {ExitCode}: {Error}", arguments, process.ExitCode, error.Trim());
            }

            return (process.ExitCode, output);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            logger.LogDebug(ex, "Git is not installed or not found in PATH");
            return (-1, string.Empty);
        }
    }
}
