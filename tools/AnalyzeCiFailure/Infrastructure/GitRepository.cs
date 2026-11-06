// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.TestTools;

namespace AnalyzeCiFailure;

/// <summary>Executes explicit Git operations with invocation-bound context and controlled remote credentials.</summary>
internal sealed class GitRepository(AnalyzerCommandExecutor command)
{
    // Reset inherited helpers before using the workflow's GitHub CLI credential.
    private static readonly string[] s_authenticatedRemoteArguments =
        ["--no-pager", "-c", "credential.helper=", "-c", "credential.helper=!gh auth git-credential"];

    internal Task<ProcessResult> GetOriginUrlAsync()
        => command.RunAsync("git", ["--no-pager", "remote", "get-url", "origin"]);

    internal async Task<string> GetHeadCommitAsync(string directory)
        => (await command.ExecuteAsync("git", ["--no-pager", "-C", directory, "rev-parse", "HEAD"])).Trim();

    internal Task<ProcessResult> FindRemoteBranchAsync(string remoteUrl, string branch)
        => command.RunAsync("git", [.. s_authenticatedRemoteArguments, "ls-remote", "--exit-code", "--heads", remoteUrl, $"refs/heads/{branch}"]);

    internal Task CloneBranchAsync(string remoteUrl, string branch, string directory)
        => command.ExecuteAsync("git", [.. s_authenticatedRemoteArguments, "clone", "--depth", "1", "--branch", branch, remoteUrl, directory]);

    internal Task InitializeAsync(string directory)
        => command.ExecuteAsync("git", ["--no-pager", "init", directory]);

    internal Task CreateOrphanBranchAsync(string directory, string branch)
        => command.ExecuteAsync("git", ["--no-pager", "-C", directory, "checkout", "--orphan", branch]);

    internal Task AddOriginAsync(string directory, string remoteUrl)
        => command.ExecuteAsync("git", ["--no-pager", "-C", directory, "remote", "add", "origin", remoteUrl]);

    internal async Task ConfigureCommitIdentityAsync(string directory, string name, string email)
    {
        await command.ExecuteAsync("git", ["--no-pager", "-C", directory, "config", "user.name", name]);
        await command.ExecuteAsync("git", ["--no-pager", "-C", directory, "config", "user.email", email]);
    }

    internal Task ConfigureGitHubCredentialsAsync(string directory)
        => command.ExecuteAsync("git", ["--no-pager", "-C", directory, "config", "credential.helper", "!gh auth git-credential"]);

    internal Task StageFilesAsync(string directory, IReadOnlyList<string> files)
        => command.ExecuteAsync("git", ["--no-pager", "-C", directory, "add", "--", .. files]);

    internal Task<string> GetStagedFileNamesAsync(string directory)
        => command.ExecuteAsync("git", ["--no-pager", "-C", directory, "diff", "--cached", "--name-only"]);

    internal Task CommitAsync(string directory, string message)
        => command.ExecuteAsync("git", ["--no-pager", "-C", directory, "commit", "-m", message]);

    internal Task PushBranchAsync(string directory, string branch)
        => command.ExecuteAsync("git", ["--no-pager", "-C", directory, "push", "origin", $"HEAD:{branch}"]);
}
