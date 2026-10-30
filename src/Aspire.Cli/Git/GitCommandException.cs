// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Git;

internal sealed class GitCommandException(GitFailure failure, int? exitCode)
    : Exception(failure switch
    {
        GitFailure.Unavailable => Resources.ErrorStrings.GitUnavailable,
        GitFailure.AuthenticationFailed => Resources.ErrorStrings.GitAuthenticationFailed,
        GitFailure.RepositoryUnavailable => Resources.ErrorStrings.GitRepositoryUnavailable,
        GitFailure.TimedOut => Resources.ErrorStrings.GitTimedOut,
        _ => Resources.ErrorStrings.GitCommandFailed
    })
{
    public GitFailure Failure { get; } = failure;

    /// <summary>
    /// Gets the Git exit code, or null when no process exit code is available.
    /// </summary>
    public int? ExitCode { get; } = exitCode;
}

internal enum GitFailure
{
    Unavailable,
    AuthenticationFailed,
    RepositoryUnavailable,
    CommandFailed,
    TimedOut
}
