// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Tests.TestServices;

/// <summary>
/// Test double for <see cref="IGracefulShutdownWindow"/>. Unlike the real
/// <see cref="ConsoleCancellationManager"/>, it registers no process-global OS signal handlers and
/// arms no real timer, so it is safe to use from unit tests running in parallel. The graceful token
/// only fires when a test explicitly calls <see cref="Expire"/>; <see cref="BeginGracefulWindow"/>
/// is a no-op so happy-path tests (process exits before any escalation) observe an unfired token
/// deterministically.
/// </summary>
internal sealed class TestGracefulShutdownWindow : IGracefulShutdownWindow, IDisposable
{
    private readonly CancellationTokenSource _cts = new();

    /// <summary>
    /// Whether the coordinator should run the graceful ladder. Defaults to <see langword="true"/>
    /// (models a run command with a configured budget); set to <see langword="false"/> to model a
    /// non-run command that force-kills.
    /// </summary>
    public bool IsEnabled { get; set; } = true;

    public CancellationToken GracefulShutdownToken => _cts.Token;

    public void BeginGracefulWindow()
    {
        // Intentionally no-op: tests drive escalation explicitly via Expire().
    }

    public void Expire()
    {
        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public void Dispose() => _cts.Dispose();
}
