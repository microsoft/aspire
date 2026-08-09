// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Projects;

namespace Aspire.Cli.Tests.TestServices;

internal sealed class TestAppHostServerProjectFactory : IAppHostServerProjectFactory
{
    public Func<string, CancellationToken, Task<IAppHostServerProject>>? CreateAsyncCallback { get; set; }

    public Task<IAppHostServerProject> CreateAsync(string appPath, CancellationToken cancellationToken = default)
    {
        if (CreateAsyncCallback is { } callback)
        {
            return callback(appPath, cancellationToken);
        }

        throw new NotImplementedException("TestAppHostServerProjectFactory.CreateAsync is not implemented for this test.");
    }
}
