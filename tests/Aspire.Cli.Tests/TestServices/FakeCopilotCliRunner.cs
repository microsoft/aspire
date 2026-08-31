// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Agents.Copilot;
using Semver;

namespace Aspire.Cli.Tests.TestServices;

internal sealed class FakeCopilotCliRunner(SemVersion? version) : ICopilotCliRunner
{
    public bool WasCalled { get; private set; }

    public Task<SemVersion?> GetVersionAsync(CancellationToken cancellationToken)
    {
        WasCalled = true;
        return Task.FromResult(version);
    }
}
