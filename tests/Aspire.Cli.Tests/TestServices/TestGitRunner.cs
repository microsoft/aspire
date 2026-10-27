// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using Aspire.Cli.Git;

namespace Aspire.Cli.Tests.TestServices;

internal sealed class TestGitRunner : IGitRunner
{
    public const string DefaultCommitSha = "0123456789abcdef0123456789abcdef01234567";

    public Func<string, string?, CancellationToken, Task<string>> ResolveReferenceAsyncCallback { get; set; } =
        (_, _, _) => Task.FromResult(DefaultCommitSha);

    public Func<string, DirectoryInfo, string, CancellationToken, Task> CloneAsyncCallback { get; set; } =
        (_, _, _, _) => throw new InvalidOperationException("Unexpected Git clone invocation.");

    public ConcurrentQueue<(string Repository, string? Reference)> ResolveCalls { get; } = new();
    public ConcurrentQueue<(string Repository, DirectoryInfo Destination, string CommitSha)> CloneCalls { get; } = new();

    public Task<string> ResolveReferenceAsync(string repository, string? reference, CancellationToken cancellationToken)
    {
        ResolveCalls.Enqueue((repository, reference));
        return ResolveReferenceAsyncCallback(repository, reference, cancellationToken);
    }

    public Task CloneAsync(string repository, DirectoryInfo destination, string commitSha, CancellationToken cancellationToken)
    {
        CloneCalls.Enqueue((repository, destination, commitSha));
        return CloneAsyncCallback(repository, destination, commitSha, cancellationToken);
    }
}
