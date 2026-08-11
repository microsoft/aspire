// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Git;

namespace Aspire.Cli.Tests.TestServices;

internal sealed class TestGitRepository : IGitRepository
{
    public Func<CancellationToken, Task<DirectoryInfo?>>? GetRootAsyncCallback { get; set; }

    public Func<DirectoryInfo, CancellationToken, Task<IReadOnlySet<string>?>>? GetIncludedFilesAsyncCallback { get; set; }

    public Task<DirectoryInfo?> GetRootAsync(CancellationToken cancellationToken)
    {
        return GetRootAsyncCallback?.Invoke(cancellationToken) ?? Task.FromResult<DirectoryInfo?>(null);
    }

    public Task<IReadOnlySet<string>?> GetIncludedFilesAsync(DirectoryInfo searchRoot, CancellationToken cancellationToken)
    {
        return GetIncludedFilesAsyncCallback?.Invoke(searchRoot, cancellationToken) ?? Task.FromResult<IReadOnlySet<string>?>(null);
    }
}
