// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Git;

namespace Aspire.Cli.Tests.TestServices;

internal sealed class TestGitCommandRunner : IGitCommandRunner
{
    public List<GitCommandInvocation> Commands { get; } = [];
    public string Output { get; set; } = TestGitRunner.DefaultCommitSha;
    public Func<GitCommandInvocation, CancellationToken, Task<string>>? RunCallback { get; set; }

    public async Task<string> RunAsync(DirectoryInfo workingDirectory, string[] arguments, GitCommandMode mode, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var command = new GitCommandInvocation(workingDirectory, arguments, mode);
        Commands.Add(command);
        var output = RunCallback is null ? Output : await RunCallback(command, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return output;
    }
}

internal sealed record GitCommandInvocation(DirectoryInfo WorkingDirectory, string[] Arguments, GitCommandMode Mode);
