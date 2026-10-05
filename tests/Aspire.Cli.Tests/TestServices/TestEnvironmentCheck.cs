// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Utils.EnvironmentChecker;

namespace Aspire.Cli.Tests.TestServices;

internal sealed class TestEnvironmentCheck(
    int order,
    Func<CancellationToken, Task<IReadOnlyList<EnvironmentCheckResult>>> checkAsync) : IEnvironmentCheck
{
    public int Order => order;

    public Task<IReadOnlyList<EnvironmentCheckResult>> CheckAsync(CancellationToken cancellationToken = default)
        => checkAsync(cancellationToken);
}