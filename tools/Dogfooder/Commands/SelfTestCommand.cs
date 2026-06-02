// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Dogfooder.Commands;

/// <summary>
/// Phase 1 stub for the <c>self-test</c> subcommand. The real implementation
/// lands in Phase 2 once <c>Hex1bTerminalAutomator</c> can drive the embedded
/// terminal and assert against <see cref="State.AppState"/>. For now this
/// simply prints a placeholder message and exits zero so CI scripts that may
/// already be wired to invoke it don't break.
/// </summary>
internal sealed class SelfTestCommand
{
    public Task<int> RunAsync(CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        Console.WriteLine("dogfooder self-test: not implemented (Phase 2).");
        return Task.FromResult(0);
    }
}
