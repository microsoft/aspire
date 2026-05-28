// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Tests.Utils;
using Hex1b.Automation;

namespace Aspire.Cli.EndToEnd.Tests.Helpers;

/// <summary>
/// Wraps a terminal run session and ensures diagnostics are captured and the terminal is properly
/// exited on disposal. Use via <see cref="CliE2ETestHelpers.StartRun"/> to consistently capture
/// diagnostics at the end of every CLI E2E test.
/// </summary>
internal sealed class TerminalRun : IAsyncDisposable
{
    private readonly Task _pendingRun;
    private readonly Hex1bTerminalAutomator _automator;
    private readonly SequenceCounter _counter;
    private readonly TemporaryWorkspace _workspace;

    internal TerminalRun(Task pendingRun, Hex1bTerminalAutomator automator, SequenceCounter counter, TemporaryWorkspace workspace)
    {
        _pendingRun = pendingRun;
        _automator = automator;
        _counter = counter;
        _workspace = workspace;
    }

    public async ValueTask DisposeAsync()
    {
        // Capture diagnostics (best effort)
        try
        {
            await _automator.CaptureAspireDiagnosticsAsync(_counter, _workspace);
        }
        catch
        {
            // Best effort diagnostics capture — don't mask the original test failure.
        }

        // Exit the terminal (best effort)
        try
        {
            await _automator.TypeAsync("exit");
            await _automator.EnterAsync();
        }
        catch
        {
            // Best effort exit — the terminal may already be closed.
        }

        // Wait for the terminal process to finish
        try
        {
            await _pendingRun;
        }
        catch
        {
            // Best effort — if the test body threw, we don't want to mask it.
        }
    }
}
