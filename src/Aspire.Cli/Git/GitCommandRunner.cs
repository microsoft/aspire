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
        if (IsUnattended(mode) && command is "fetch" or "ls-remote")
        {
            await ConfigureUnattendedSshAsync(startInfo, arguments, cancellationToken);
        }
        return await RunProcessAsync(startInfo, captureCancellationOutput: command is "fetch" or "ls-remote" && mode != GitCommandMode.Captured && startInfo.RedirectStandardOutput,
            mode, cancellationToken);
    }

    private bool IsUnattended(GitCommandMode mode) =>
        mode == GitCommandMode.Unattended || mode == GitCommandMode.Interactive && !hostEnvironment.SupportsInteractiveInput;

    internal ProcessStartInfo CreateStartInfo(string directory, string[] arguments, GitCommandMode mode)
    {
        var interactive = mode == GitCommandMode.Interactive && hostEnvironment.SupportsInteractiveInput;
        var unattended = IsUnattended(mode);
        var command = arguments[0].StartsWith("--git-dir=", StringComparison.Ordinal) ? arguments[1] : arguments[0];
        var interactiveFetch = command == "fetch" && interactive;
        // Ref resolution needs stdout for object IDs, but authentication instructions on
        // stderr must remain visible before an interactive credential prompt completes.
        var interactiveRemote = interactive && command is "fetch" or "ls-remote";
        var startInfo = new ProcessStartInfo("git", unattended
            ? ["--literal-pathspecs", "-c", "core.askPass=", .. arguments]
            : ["--literal-pathspecs", .. arguments])
        {
            WorkingDirectory = directory,
            RedirectStandardInput = mode == GitCommandMode.Captured || unattended,
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
            // GIT_TERMINAL_PROMPT does not govern GCM UI. Keep cached credentials usable.
            // https://github.com/git-ecosystem/git-credential-manager/blob/release/docs/environment.md#gcm_interactive
            startInfo.Environment["GCM_INTERACTIVE"] = "never";
        }
        startInfo.Environment["LC_ALL"] = "C";
        FilterEnvironment(startInfo.Environment, mode);
        if (unattended)
        {
            DisableAskPass(startInfo.Environment);
        }

        return startInfo;
    }

    internal static void DisableAskPass(IDictionary<string, string?> environment)
    {
        // Git tries GIT_ASKPASS, core.askPass and SSH_ASKPASS before consulting
        // GIT_TERMINAL_PROMPT. Empty (not unset) overrides prevent those fallbacks.
        // https://git-scm.com/docs/gitcredentials#_requesting_credentials
        environment["GIT_ASKPASS"] = "";
        environment["SSH_ASKPASS"] = "";
        environment["SSH_ASKPASS_REQUIRE"] = "never";
    }

    private ProcessStartInfo CreateUnattendedQuery(ProcessStartInfo source, string[] arguments)
    {
        var query = CreateStartInfo(source.WorkingDirectory, arguments, GitCommandMode.Unattended);
        query.Environment.Clear();
        foreach (var (name, value) in source.Environment)
        {
            query.Environment[name] = value;
        }
        DisableAskPass(query.Environment);

        return query;
    }

    internal async Task ConfigureUnattendedSshAsync(ProcessStartInfo startInfo, string[] arguments, CancellationToken cancellationToken)
    {
        var separator = Array.IndexOf(arguments, "--");
        if (separator < 0 || separator + 1 == arguments.Length)
        {
            return;
        }
        string[] repositoryArguments = arguments[0].StartsWith("--git-dir=", StringComparison.Ordinal) ? [arguments[0]] : [];
        // --get-url applies insteadOf without contacting the remote: an HTTPS
        // selector can actually use SSH, and a local selector can remain offline.
        // https://git-scm.com/docs/git-ls-remote#Documentation/git-ls-remote.txt---get-url
        var urlQuery = CreateUnattendedQuery(startInfo,
            [.. repositoryArguments, "ls-remote", "--get-url", "--", arguments[separator + 1]]);
        var repository = (await RunProcessAsync(urlQuery, captureCancellationOutput: false, GitCommandMode.Unattended, cancellationToken)).TrimEnd('\r', '\n');
        if (!UsesSsh(repository))
        {
            return;
        }

        string? command;
        if (!startInfo.Environment.TryGetValue("GIT_SSH_COMMAND", out command) || command is null)
        {
            var configQuery = CreateUnattendedQuery(startInfo,
                [.. repositoryArguments, "config", "--null", "--get", "core.sshCommand"]);
            try
            {
                // --null emits "<command>\0", preserving newlines inside the
                // value so invalid shell commands cannot normalize into "ssh".
                command = (await RunProcessAsync(configQuery, captureCancellationOutput: false, GitCommandMode.Unattended, cancellationToken)).TrimEnd('\0');
            }
            catch (GitCommandException ex) when (ex.ExitCode is 1)
            {
                // --get exits 1 for an unset key. A configured empty command is
                // distinct and must not silently fall back to another launcher.
                command = null;
            }
        }
        var executable = command is not null
            ? GetSshExecutable(command)
            : startInfo.Environment.TryGetValue("GIT_SSH", out var configuredExecutable) && configuredExecutable is not null
                ? configuredExecutable
                : "ssh";
        var openSsh = await GetOpenSshExecutableAsync(startInfo, executable, cancellationToken);
        if (openSsh is null)
        {
            throw new GitCommandException(GitFailure.UnattendedSshUnsupported, null);
        }

        // OpenSSH uses the first value, so place BatchMode before user config.
        // Keep identity files, known hosts and agent authentication available.
        // https://man.openbsd.org/ssh_config#BatchMode
        startInfo.Environment["GIT_SSH_COMMAND"] = $"'{openSsh.Replace("'", "'\\''", StringComparison.Ordinal)}' -o BatchMode=yes";
        startInfo.Environment["GIT_SSH_VARIANT"] = "ssh";
    }

    internal static bool UsesSsh(string repository)
    {
        if (Uri.TryCreate(repository, UriKind.Absolute, out var uri)
            && (uri.IsFile || repository.Contains("://", StringComparison.Ordinal)))
        {
            return uri.Scheme is "ssh" or "git+ssh" or "ssh+git";
        }
        // SCP-style selectors look like git@host:path or host:path. A slash
        // before ':' makes this a local path instead; C:\path is local as well.
        var colon = repository.IndexOf(':');
        return colon > 0 && (colon != 1 || !char.IsAsciiLetter(repository[0]))
            && repository.AsSpan(0, colon).IndexOfAny('/', '\\') < 0;
    }

    internal static string GetSshExecutable(string command)
    {
        var executable = command.Trim();
        if (executable.Length >= 2 && executable[0] is '\'' or '"' && executable[^1] == executable[0])
        {
            executable = executable[1..^1];
        }
        else if (executable.Any(char.IsWhiteSpace))
        {
            throw new GitCommandException(GitFailure.UnattendedSshUnsupported, null);
        }
        // A command string can contain a wrapper, shell expansion or options
        // that defeat a batch-mode prefix. Do not parse or execute that shell.
        if (executable.Length == 0 || executable.AsSpan().IndexOfAny("'\"`$;&|<>\0\r\n()*?[]".AsSpan()) >= 0)
        {
            throw new GitCommandException(GitFailure.UnattendedSshUnsupported, null);
        }

        return executable;
    }

    private async Task<string?> GetOpenSshExecutableAsync(ProcessStartInfo gitStartInfo, string executable, CancellationToken cancellationToken)
    {
        // Never apply OpenSSH options based on a filename alone. Reject non-SSH
        // selections before probing; verify the selected client's actual version.
        if (string.IsNullOrWhiteSpace(executable) || executable.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            return null;
        }
        var name = Path.GetFileName(executable);
        if (!name.Equals("ssh", StringComparison.OrdinalIgnoreCase) && !name.Equals("ssh.exe", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        var pathEnvironment = new Dictionary<string, string>();
        foreach (var key in new[] { "PATH", "PATHEXT" })
        {
            if (gitStartInfo.Environment.TryGetValue(key, out var value) && value is not null)
            {
                pathEnvironment[key] = key == "PATH" ? NormalizeSshPath(value, gitStartInfo.WorkingDirectory) : value;
            }
        }
        // Git prepends its executable directories, including bundled SSH on Windows.
        // Query its child environment rather than substituting the CLI's SSH client.
        // https://github.com/git-for-windows/MINGW-packages/blob/main/mingw-w64-git/git-wrapper.c
        // https://github.com/git/git/blob/master/exec-cmd.c
        var pathCommand = OperatingSystem.IsWindows()
            ? $"!'{Path.Combine(Environment.SystemDirectory, "cmd.exe").Replace("\\", "/", StringComparison.Ordinal).Replace("'", "'\\''", StringComparison.Ordinal)}' /d /c set PATH"
            : "!printf '%s' \"$PATH\"";
        var pathQuery = CreateUnattendedQuery(gitStartInfo,
            ["-c", $"alias.aspire-ssh-path={pathCommand}", "aspire-ssh-path"]);
        foreach (var (key, value) in pathEnvironment)
        {
            pathQuery.Environment[key] = value;
        }
        // Read Windows' native semicolon-delimited PATH, not MSYS' POSIX translation.
        // Prevent MSYS from converting cmd.exe's /d and /c switches into paths.
        if (OperatingSystem.IsWindows())
        {
            pathQuery.Environment["MSYS2_ARG_CONV_EXCL"] = "*";
        }
        string childPath;
        try
        {
            childPath = await RunProcessAsync(pathQuery, captureCancellationOutput: false, GitCommandMode.Unattended, cancellationToken);
        }
        catch (GitCommandException ex) when (ex.Failure == GitFailure.CommandFailed)
        {
            // Without a trustworthy child PATH, report the existing unsupported-SSH
            // diagnostic instead of guessing a different client.
            return null;
        }
        if (OperatingSystem.IsWindows())
        {
            // `set PATH` emits "Path=C:\...\r\nPATHEXT=.COM;.EXE;...\r\n".
            // Match the complete variable name; similarly prefixed variables are unrelated.
            var pathLine = childPath.Split('\n').FirstOrDefault(line => line.StartsWith("PATH=", StringComparison.OrdinalIgnoreCase));
            if (pathLine is null)
            {
                return null;
            }
            childPath = pathLine[5..].TrimEnd('\r');
        }
        pathEnvironment["PATH"] = NormalizeSshPath(childPath, gitStartInfo.WorkingDirectory);
        if (!PathLookupHelper.TryResolveExecutablePath(executable, out var resolved, pathEnvironment))
        {
            return null;
        }
        executable = Path.GetFullPath(resolved, gitStartInfo.WorkingDirectory);
        var probe = CreateUnattendedQuery(gitStartInfo, ["version"]);
        probe.FileName = executable;
        probe.ArgumentList.Clear();
        probe.ArgumentList.Add("-V");
        var result = await ProcessCaptureRunner.RunAsync<string?>(
            probe, TimeSpan.FromSeconds(5),
            async (process, token) =>
            {
                process.StandardInput.Close();
                var stdout = ReadOutputAsync(process.StandardOutput, token);
                var stderr = ReadOutputAsync(process.StandardError, token);
                await Task.WhenAll(stdout, stderr);
                return (await stdout + await stderr).Trim();
            },
            () => null, NullLogger.Instance, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return !result.Cancelled && result.FailureKind is null && result.ExitCode == 0
            && result.Capture?.StartsWith("OpenSSH_", StringComparison.Ordinal) is true ? executable : null;
    }

    internal static string NormalizeSshPath(string path, string workingDirectory)
    {
        var directories = new List<string>();
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                // Existence checks and execution must use the same base directory.
                // The CLI invocation directory can differ from Git's private snapshot.
                directories.Add(Path.GetFullPath(directory, workingDirectory));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // Match PathLookupHelper's treatment of malformed PATH entries:
                // skip only that entry and continue to later executable candidates.
            }
        }

        return string.Join(Path.PathSeparator, directories);
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
                    // Captured and unattended commands must see EOF, not consume the CLI's input
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
