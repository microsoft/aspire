// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Scaffolding;

namespace Aspire.Cli.Tests.TestServices;

internal sealed class TestScaffoldingService : IScaffoldingService
{
    public Func<ScaffoldContext, CancellationToken, Task<bool>>? ScaffoldAsyncCallback { get; set; }

    public Task<bool> ScaffoldAsync(ScaffoldContext context, CancellationToken cancellationToken)
    {
        if (ScaffoldAsyncCallback is not null)
        {
            return ScaffoldAsyncCallback(context, cancellationToken);
        }

        return Task.FromResult(true);
    }
}
