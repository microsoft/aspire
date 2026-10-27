// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ComponentModel;
using System.Text;
using Aspire.Cli.DotNet;

namespace Aspire.Cli.Git;

internal sealed class GitRunner(
    CliExecutionContext executionContext,
    IProcessExecutionFactory processExecutionFactory,
    TimeProvider timeProvider) : IGitRunner
{
    private static readonly TimeSpan s_operationTimeout = TimeSpan.FromMinutes(2);
    private static readonly HashSet<string> s_repositoryEnvironmentVariables = new(StringComparer.OrdinalIgnoreCase)
    {
        "GIT_DIR", "GIT_WORK_TREE", "GIT_INDEX_FILE", "GIT_OBJECT_DIRECTORY",
        "GIT_ALTERNATE_OBJECT_DIRECTORIES", "GIT_COMMON_DIR", "GIT_NAMESPACE", "GIT_SHALLOW_FILE"
    };

    public Task<string> ResolveReferenceAsync(string repository, string? reference, CancellationToken cancellationToken) =>
        WithTimeoutAsync(token => ResolveReferenceCoreAsync(repository, reference, token), cancellationToken);

    private async Task<string> ResolveReferenceCoreAsync(string repository, string? reference, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (reference is not null && IsCommitSha(reference))
        {
            return reference.ToLowerInvariant();
        }

        var requestedRef = reference ?? "HEAD";
        string[] candidates = requestedRef == "HEAD" || requestedRef.StartsWith("refs/", StringComparison.Ordinal)
            ? [$"{requestedRef}^{{}}", requestedRef]
            : [$"refs/heads/{requestedRef}", $"refs/tags/{requestedRef}^{{}}", $"refs/tags/{requestedRef}"];
        var output = await RunAsync(["ls-remote", "--", repository, .. candidates], executionContext.WorkingDirectory, cancellationToken);

        // git ls-remote emits "<object SHA>\t<ref>", including a peeled commit for annotated tags:
        //   abc...123\trefs/tags/v1
        //   def...456\trefs/tags/v1^{}
        // Match exact names, not Git's wildcard patterns, and prefer the peeled tag over its tag object.
        var refs = new Dictionary<string, string>(StringComparer.Ordinal);
        using var reader = new StringReader(output);
        while (reader.ReadLine() is { } line)
        {
            var separator = line.IndexOf('\t');
            if (separator > 0 && IsCommitSha(line[..separator]))
            {
                refs[line[(separator + 1)..]] = line[..separator].ToLowerInvariant();
            }
        }

        foreach (var candidate in candidates)
        {
            if (refs.TryGetValue(candidate, out var sha))
            {
                return sha;
            }
        }

        throw new GitReferenceException(requestedRef);
    }

    public Task CloneAsync(string repository, DirectoryInfo destination, string commitSha, CancellationToken cancellationToken) =>
        WithTimeoutAsync(async token =>
        {
            if (!IsCommitSha(commitSha))
            {
                throw new ArgumentException("A full commit SHA is required.", nameof(commitSha));
            }

            if (!Path.IsPathRooted(repository) && !repository.Contains(':'))
            {
                var localPath = Path.GetFullPath(repository, executionContext.WorkingDirectory.FullName);
                if (Directory.Exists(localPath))
                {
                    // Fetch runs inside the new checkout, not the caller's working directory.
                    repository = localPath;
                }
            }

            // Fetch the resolved SHA, not the original branch/tag: it can move between resolution
            // and checkout. A depth-one fetch also avoids local clone hard links, so cleanup cannot
            // change source repository object attributes. https://git-scm.com/docs/git-fetch
            await RunAsync(["init", "--quiet", $"--object-format={(commitSha.Length == 64 ? "sha256" : "sha1")}", "--", destination.FullName],
                executionContext.WorkingDirectory, token);
            await RunAsync(["fetch", "--depth", "1", "--no-tags", "--", repository, commitSha], destination, token);
            await RunAsync(["checkout", "--detach", "--force", "FETCH_HEAD"], destination, token);
            var actualSha = (await RunAsync(["rev-parse", "--verify", "HEAD"], destination, token)).Trim();
            if (!string.Equals(actualSha, commitSha, StringComparison.OrdinalIgnoreCase))
            {
                throw new GitReferenceException(commitSha);
            }

            return true;
        }, cancellationToken);

    private static bool IsCommitSha(string value) =>
        value.Length is 40 or 64 && value.All(char.IsAsciiHexDigit);

    private async Task<T> WithTimeoutAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var deadline = new CancellationTokenSource(s_operationTimeout, timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            return await operation(linked.Token);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new GitCommandException(GitFailure.TimedOut, null);
        }
    }

    private async Task<string> RunAsync(string[] arguments, DirectoryInfo workingDirectory, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var output = new StringBuilder();
        var failure = GitFailure.CommandFailed;
        try
        {
            await using var process = processExecutionFactory.CreateExecution(
                "git", ["--literal-pathspecs", .. arguments],
                new Dictionary<string, string> { ["GIT_TERMINAL_PROMPT"] = "0", ["LC_ALL"] = "C" },
                workingDirectory,
                new ProcessInvocationOptions
                {
                    // URLs and Git diagnostics can contain credentials or
                    // private data. Classify stderr locally without logging or retaining it.
                    SuppressLogging = true,
                    EnvironmentVariableFilter = s_repositoryEnvironmentVariables.Contains,
                    StandardOutputCallback = line => output.AppendLine(line),
                    StandardErrorCallback = line =>
                    {
                        if (line.Contains("authentication", StringComparison.OrdinalIgnoreCase)
                            || line.Contains("permission denied", StringComparison.OrdinalIgnoreCase)
                            || line.Contains("could not read Username", StringComparison.OrdinalIgnoreCase)
                            || line.Contains("could not read Password", StringComparison.OrdinalIgnoreCase))
                        {
                            failure = GitFailure.AuthenticationFailed;
                        }
                        else if (failure != GitFailure.AuthenticationFailed
                            && (line.Contains("repository not found", StringComparison.OrdinalIgnoreCase)
                                || line.Contains("could not resolve host", StringComparison.OrdinalIgnoreCase)
                                || line.Contains("unable to access", StringComparison.OrdinalIgnoreCase)
                                || line.Contains("could not read from remote repository", StringComparison.OrdinalIgnoreCase)))
                        {
                            failure = GitFailure.RepositoryUnavailable;
                        }
                    }
                });
            if (!await process.StartAsync(cancellationToken))
            {
                throw new GitCommandException(GitFailure.Unavailable, null);
            }

            var exitCode = await process.WaitForExitAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (exitCode != 0)
            {
                throw new GitCommandException(failure, exitCode);
            }

            return output.ToString();
        }
        catch (Win32Exception)
        {
            throw new GitCommandException(GitFailure.Unavailable, null);
        }
    }
}
