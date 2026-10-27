// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Text;
using Aspire.Cli.DotNet;
using Aspire.Cli.Utils;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Git;

internal interface IGitCommandRunner
{
    Task<string> RunAsync(DirectoryInfo workingDirectory, string[] arguments, GitCommandMode mode, CancellationToken cancellationToken);
}

internal sealed class GitCommandRunner(ICliHostEnvironment hostEnvironment, ILogger<GitCommandRunner> logger) : IGitCommandRunner
{
    private static readonly HashSet<string> s_repositoryEnvironmentVariables = new(StringComparer.OrdinalIgnoreCase)
    {
        "GIT_DIR", "GIT_WORK_TREE", "GIT_INDEX_FILE", "GIT_OBJECT_DIRECTORY", "GIT_ALTERNATE_OBJECT_DIRECTORIES"
    };

    private static readonly HashSet<string> s_worktreeEnvironmentVariables = new(
    [
        "GIT_COMMON_DIR", "GIT_NAMESPACE", "GIT_SHALLOW_FILE",
        .. Acquisition.IdentityResolver.IdentityEnvVarNames,
        .. ProcessExecutionFactory.InvocationScopedEnvVarNames
    ], StringComparer.OrdinalIgnoreCase);

    public async Task<string> RunAsync(DirectoryInfo workingDirectory, string[] arguments, GitCommandMode mode, CancellationToken cancellationToken)
    {
        var startInfo = CreateStartInfo(workingDirectory.FullName, arguments, mode);
        var command = arguments[0].StartsWith("--git-dir=", StringComparison.Ordinal) ? arguments[1] : arguments[0];
        using var scope = logger.BeginScope("Git {Operation} ({OperationId})", command, Guid.NewGuid());
        return await RunProcessAsync(startInfo, captureCancellationOutput: command is "fetch" or "ls-remote" && mode != GitCommandMode.Captured && startInfo.RedirectStandardOutput,
            mode, cancellationToken);
    }

    internal ProcessStartInfo CreateStartInfo(string directory, string[] arguments, GitCommandMode mode)
    {
        var interactive = mode == GitCommandMode.Interactive && hostEnvironment.SupportsInteractiveInput;
        var command = arguments[0].StartsWith("--git-dir=", StringComparison.Ordinal) ? arguments[1] : arguments[0];
        var interactiveFetch = command == "fetch" && interactive;
        // Ref resolution needs stdout for object IDs, but authentication instructions on
        // stderr must remain visible before an interactive credential prompt completes.
        var interactiveRemote = interactive && command is "fetch" or "ls-remote";
        var startInfo = new ProcessStartInfo("git", ["--literal-pathspecs", .. arguments])
        {
            WorkingDirectory = directory,
            RedirectStandardInput = mode == GitCommandMode.Captured,
            // Fetch prompts need not end in a newline, and Git must control password echo.
            RedirectStandardOutput = !interactiveFetch,
            RedirectStandardError = !interactiveRemote,
            StandardOutputEncoding = interactiveFetch ? null : Encoding.UTF8,
            StandardErrorEncoding = interactiveRemote ? null : Encoding.UTF8,
            CreateNoWindow = !interactive
        };
        startInfo.Environment["GIT_TERMINAL_PROMPT"] = interactive ? "1" : "0";
        if (!interactive && mode != GitCommandMode.Captured)
        {
            // GIT_TERMINAL_PROMPT does not govern GCM UI. Preserve configured SSH launchers.
            // https://github.com/git-ecosystem/git-credential-manager/blob/release/docs/environment.md#gcm_interactive
            startInfo.Environment["GCM_INTERACTIVE"] = "never";
        }
        startInfo.Environment["LC_ALL"] = "C";
        FilterEnvironment(startInfo.Environment, mode);

        return startInfo;
    }

    internal static void FilterEnvironment(IDictionary<string, string?> environment, GitCommandMode mode)
    {
        if (mode != GitCommandMode.Captured)
        {
            // Removing known names preserves the environment dictionary's platform-specific casing.
            foreach (var key in s_repositoryEnvironmentVariables)
            {
                environment.Remove(key);
            }
            return;
        }

        foreach (var key in environment.Keys.ToArray())
        {
            if (s_repositoryEnvironmentVariables.Contains(key) || s_worktreeEnvironmentVariables.Contains(key))
            {
                environment.Remove(key);
            }
        }
    }

    internal async Task<string> RunProcessAsync(ProcessStartInfo startInfo, bool captureCancellationOutput, GitCommandMode mode, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var stopwatch = Stopwatch.StartNew();
        // Arguments, directories, captured content and exception messages can expose credentials.
        // Log lifecycle metadata only; the generic capture runner must keep its diagnostics off.
        logger.LogDebug("Starting Git process.");
        using var registration = cancellationToken.Register(() => logger.LogDebug("Git cancellation requested after {Elapsed}.", stopwatch.Elapsed));
        var result = await ProcessCaptureRunner.RunAsync<GitOutput?>(
            startInfo, Timeout.InfiniteTimeSpan,
            async (process, token) =>
            {
                logger.LogDebug("Git process {ProcessId} started after {Elapsed}.", process.Id, stopwatch.Elapsed);
                if (startInfo.RedirectStandardInput)
                {
                    // Captured template commands must see EOF, not consume the CLI's input
                    // or wait for input until the acquisition deadline expires.
                    process.StandardInput.Close();
                }
                if (!startInfo.RedirectStandardOutput)
                {
                    return new GitOutput(string.Empty, string.Empty);
                }
                var stdout = ReadOutputAsync(process.StandardOutput, token);
                var stderr = startInfo.RedirectStandardError
                    ? ReadOutputAsync(process.StandardError, token)
                    : Task.FromResult(string.Empty);
                await Task.WhenAll(stdout, stderr);
                logger.LogDebug("Git process {ProcessId} output drained after {Elapsed}.", process.Id, stopwatch.Elapsed);
                return new GitOutput(await stdout, await stderr);
            },
            () => null, NullLogger.Instance, cancellationToken);
        logger.LogDebug("Git process completed after {Elapsed}; exit code {ExitCode}, failure {Failure}, cancelled {Cancelled}.",
            stopwatch.Elapsed, result.ExitCode, result.FailureKind, result.Cancelled);
        if (cancellationToken.IsCancellationRequested && captureCancellationOutput && result.Capture is { } interrupted)
        {
            throw new GitProcessCanceledException(interrupted.StandardOutput + interrupted.StandardError, cancellationToken);
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (result.FailureKind is ProcessCaptureFailureKind.StartFailed)
        {
            throw new GitCommandException(GitFailure.Unavailable, null);
        }
        if (result.Capture is not { } capture || result.FailureKind is not null)
        {
            throw new GitCommandException(GitFailure.RepositoryUnavailable, null);
        }
        if (result.ExitCode != 0)
        {
            throw CreateCommandException(capture.StandardError, result.ExitCode, mode);
        }

        return capture.StandardOutput;
    }

    internal static GitCommandException CreateCommandException(string stderr, int exitCode, GitCommandMode mode)
    {
        var authentication = stderr.Contains("authentication", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("permission denied", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("could not read Username", StringComparison.OrdinalIgnoreCase)
            || (mode == GitCommandMode.Captured && stderr.Contains("could not read Password", StringComparison.OrdinalIgnoreCase));
        var unavailable = stderr.Contains("repository not found", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("could not resolve host", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("unable to access", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("could not read from remote repository", StringComparison.OrdinalIgnoreCase);
        return new GitCommandException(authentication ? GitFailure.AuthenticationFailed
            : unavailable ? GitFailure.RepositoryUnavailable : GitFailure.CommandFailed, exitCode);
    }

    private static async Task<string> ReadOutputAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var output = new StringBuilder();
        var buffer = new char[4096];
        try
        {
            // Prompts such as "Enter passphrase for key '/home/user/.ssh/id_ed25519': "
            // can lack a newline. Read chunks without normalizing newlines or NUL delimiters.
            while (true)
            {
                var count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
                if (count == 0)
                {
                    break;
                }
                output.Append(buffer, 0, count);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The capture runner bounds tree shutdown and draining. Retain partial diagnostics.
        }

        return output.ToString();
    }

    private sealed record GitOutput(string StandardOutput, string StandardError);
}

internal sealed class GitProcessCanceledException(string output, CancellationToken cancellationToken) : OperationCanceledException(cancellationToken)
{
    // For explicit user display on timeout only, never exception messages, logs or telemetry.
    public string Output { get; } = output;
}
