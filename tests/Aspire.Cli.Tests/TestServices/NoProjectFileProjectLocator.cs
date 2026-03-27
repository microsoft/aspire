// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Projects;

namespace Aspire.Cli.Tests.TestServices;

internal sealed class NoProjectFileProjectLocator : IProjectLocator
{
    public Task<AppHostProjectSearchResult> UseOrFindAppHostProjectFileAsync(FileInfo? projectFile, MultipleAppHostProjectsFoundBehavior multipleAppHostProjectsFoundBehavior, bool createSettingsFile, CancellationToken cancellationToken = default)
    {
        throw new ProjectLocatorException("No project file found.", ProjectLocatorFailureReason.NoProjectFileFound);
    }

    public Task<FileInfo?> UseOrFindAppHostProjectFileAsync(FileInfo? projectFile, bool createSettingsFile, CancellationToken cancellationToken)
    {
        throw new ProjectLocatorException("No project file found.", ProjectLocatorFailureReason.NoProjectFileFound);
    }

    public Task<FileInfo?> GetAppHostFromSettingsAsync(CancellationToken cancellationToken = default) => Task.FromResult<FileInfo?>(null);
}
