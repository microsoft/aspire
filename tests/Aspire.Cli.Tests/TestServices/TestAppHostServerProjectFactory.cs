// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Projects;

namespace Aspire.Cli.Tests.TestServices;

internal sealed class TestAppHostServerProjectFactory : IAppHostServerProjectFactory
{
    public Func<string, CancellationToken, Task<IAppHostServerProject>>? CreateAsyncCallback { get; set; }

    public string? RestoreRootConfigDirectory { get; private set; }

    public Task<IAppHostServerProject> CreateAsync(string appPath, CancellationToken cancellationToken = default)
        => CreateAsync(appPath, restoreRootConfigDirectory: null, cancellationToken);

    public Task<IAppHostServerProject> CreateAsync(string appPath, string? restoreRootConfigDirectory, CancellationToken cancellationToken)
    {
        RestoreRootConfigDirectory = restoreRootConfigDirectory;
        if (CreateAsyncCallback is { } callback)
        {
            return callback(appPath, cancellationToken);
        }

        throw new NotImplementedException("TestAppHostServerProjectFactory.CreateAsync is not implemented for this test.");
    }
}
