// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Git;

internal interface IGitClient
{
    Task<string> ResolveReferenceAsync(string repository, string? reference, DirectoryInfo workingDirectory, GitCommandMode mode, CancellationToken cancellationToken);
    Task InitializeAsync(DirectoryInfo workingDirectory, GitRepositoryLocation repository, string? objectFormat, GitCommandMode mode, CancellationToken cancellationToken);
    Task FetchAsync(GitRepositoryLocation destination, string repository, string reference, GitCommandMode mode, CancellationToken cancellationToken);
    Task CheckoutAsync(GitRepositoryLocation repository, GitCommandMode mode, CancellationToken cancellationToken);
    Task<string> GetCommitAsync(GitRepositoryLocation repository, string reference, GitCommandMode mode, CancellationToken cancellationToken);
    Task<GitTreeEntry?> GetFileAsync(GitRepositoryLocation repository, string commit, string path, GitCommandMode mode, CancellationToken cancellationToken);
    Task<long?> GetObjectSizeAsync(GitRepositoryLocation repository, string objectId, GitCommandMode mode, CancellationToken cancellationToken);
    Task<string> ReadBlobAsync(GitRepositoryLocation repository, string objectId, GitCommandMode mode, CancellationToken cancellationToken);
}

internal sealed record GitRepositoryLocation(DirectoryInfo Directory, bool IsBare);

internal sealed record GitTreeEntry(string Mode, string Type, string ObjectId)
{
    public const string RegularFileMode = "100644";
    public const string ExecutableFileMode = "100755";
    public const string SymbolicLinkMode = "120000";
    public const string DirectoryMode = "040000";
}

internal enum GitCommandMode
{
    // Preserve template acquisition's existing credential-helper behavior: disable terminal
    // prompts without overriding GCM_INTERACTIVE or configured askpass/SSH launchers.
    Captured,
    Interactive,
    Unattended
}
