// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Git;

internal sealed class GitRunner(
    CliExecutionContext executionContext,
    IGitClient git,
    TimeProvider timeProvider) : IGitRunner
{
    private static readonly TimeSpan s_operationTimeout = TimeSpan.FromMinutes(2);

    public Task<string> ResolveReferenceAsync(string repository, string? reference, CancellationToken cancellationToken) =>
        WithTimeoutAsync(token => git.ResolveReferenceAsync(repository, reference, executionContext.WorkingDirectory, token), cancellationToken);

    public Task CloneAsync(string repository, DirectoryInfo destination, string commitSha, CancellationToken cancellationToken) =>
        WithTimeoutAsync(async token =>
        {
            if (!GitClient.IsCommitSha(commitSha))
            {
                throw new ArgumentException("A full commit SHA is required.", nameof(commitSha));
            }
            if (!Path.IsPathRooted(repository) && !repository.Contains(':'))
            {
                var localPath = Path.GetFullPath(repository, executionContext.WorkingDirectory.FullName);
                if (Directory.Exists(localPath))
                {
                    repository = localPath;
                }
            }

            // Fetch the resolved SHA rather than a movable ref. Depth-one fetches also avoid
            // local clone hard links, so cleanup cannot change source object attributes.
            // https://git-scm.com/docs/git-fetch
            var location = new GitRepositoryLocation(destination, IsBare: false);
            await git.InitializeAsync(executionContext.WorkingDirectory, location,
                commitSha.Length == 64 ? "sha256" : "sha1", GitCommandMode.Captured, token);
            await git.FetchAsync(location, repository, commitSha, GitCommandMode.Captured, token);
            await git.CheckoutAsync(location, GitCommandMode.Captured, token);
            var actualSha = await git.GetCommitAsync(location, "HEAD", GitCommandMode.Captured, token);
            if (!string.Equals(actualSha, commitSha, StringComparison.OrdinalIgnoreCase))
            {
                throw new GitReferenceException(commitSha);
            }

            return true;
        }, cancellationToken);

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
}
