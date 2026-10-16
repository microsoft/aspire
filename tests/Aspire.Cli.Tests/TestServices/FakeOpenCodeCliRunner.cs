// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Agents.OpenCode;
using Semver;

namespace Aspire.Cli.Tests.TestServices;

internal sealed class FakeOpenCodeCliRunner(SemVersion? version) : IOpenCodeCliRunner
{
    private int _calls;

    public int Calls => Volatile.Read(ref _calls);
    public bool WasCalled => Calls > 0;

    public Task<SemVersion?> GetVersionAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _calls);
        return Task.FromResult(version);
    }
}
