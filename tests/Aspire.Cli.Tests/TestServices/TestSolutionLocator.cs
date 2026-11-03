// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Projects;

namespace Aspire.Cli.Tests.TestServices;

internal sealed class TestSolutionLocator : ISolutionLocator
{
    public required Func<DirectoryInfo, CancellationToken, Task<FileInfo?>> FindSolutionFileAsyncCallback { get; init; }

    public Task<FileInfo?> FindSolutionFileAsync(DirectoryInfo startDirectory, CancellationToken cancellationToken = default)
    {
        return FindSolutionFileAsyncCallback(startDirectory, cancellationToken);
    }
}
