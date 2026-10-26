// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using Aspire.Cli.Git;

namespace Aspire.Cli.Tests.TestServices;

internal sealed class TestGitCommandRunner : IGitCommandRunner
{
    public const string DefaultCommitSha = "0123456789abcdef0123456789abcdef01234567";

    public ConcurrentQueue<GitCommandInvocation> Commands { get; } = new();
    public string Output { get; set; } = DefaultCommitSha;
    public Func<GitCommandInvocation, CancellationToken, Task<string>>? RunCallback { get; set; }

    public static TestGitCommandRunner CreateForTemplateCheckout(Func<string, DirectoryInfo, string, CancellationToken, Task> checkout)
    {
        var commands = new TestGitCommandRunner();
        commands.RunCallback = async (command, token) =>
        {
            switch (command.Arguments[0])
            {
                case "ls-remote":
                    return $"{DefaultCommitSha}\t{command.Arguments[3]}\n";
                case "init":
                case "checkout":
                    return string.Empty;
                case "fetch":
                    await checkout(command.Arguments[5], command.WorkingDirectory, command.Arguments[6], token);
                    return string.Empty;
                case "rev-parse":
                    return commands.Commands.Last(call => call.Arguments[0] == "fetch"
                        && call.WorkingDirectory.FullName == command.WorkingDirectory.FullName).Arguments[6];
                default:
                    throw new InvalidOperationException($"Unexpected Git command: {command.Arguments[0]}.");
            }
        };
        return commands;
    }

    public async Task<string> RunAsync(DirectoryInfo workingDirectory, string[] arguments, GitCommandMode mode, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var command = new GitCommandInvocation(workingDirectory, arguments, mode);
        Commands.Enqueue(command);
        var output = RunCallback is null ? Output : await RunCallback(command, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return output;
    }
}

internal sealed record GitCommandInvocation(DirectoryInfo WorkingDirectory, string[] Arguments, GitCommandMode Mode);
