// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Hex1b;
using Terminals.TerminalHost;

// The terminal host process receives configuration via environment variables:
//   TERMINAL_SOCKET_PATH — path to the Unix domain socket for client connections
//   TERMINAL_COLUMNS     — initial terminal width (default: 120)
//   TERMINAL_ROWS        — initial terminal height (default: 30)
//   TERMINAL_SHELL       — shell to run (default: platform-specific)

var socketPath = Environment.GetEnvironmentVariable("TERMINAL_SOCKET_PATH");
if (string.IsNullOrEmpty(socketPath))
{
    Console.Error.WriteLine("Error: TERMINAL_SOCKET_PATH environment variable is required.");
    return 1;
}

var columns = int.TryParse(Environment.GetEnvironmentVariable("TERMINAL_COLUMNS"), out var c) ? c : 120;
var rows = int.TryParse(Environment.GetEnvironmentVariable("TERMINAL_ROWS"), out var r) ? r : 30;
var shell = Environment.GetEnvironmentVariable("TERMINAL_SHELL")
    ?? (OperatingSystem.IsWindows() ? "pwsh" : "/bin/bash");

Console.Error.WriteLine($"[TerminalHost] Starting: shell={shell}, size={columns}x{rows}, socket={socketPath}");

// Create the UDS presentation adapter — this is the "display" side that clients connect to
var adapter = new UnixDomainSocketPresentationAdapter(socketPath, columns, rows);

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

try
{
    // Wait for a client to connect before starting the terminal
    await adapter.WaitForClientAsync(cts.Token);

    // Build the Hex1b terminal:
    //   - WithPresentation: our UDS adapter serves as the display
    //   - WithPtyProcess: the shell runs as a child process with a real PTY
    await using var terminal = Hex1bTerminal.CreateBuilder()
        .WithPresentation(adapter)
        .WithPtyProcess(shell)
        .WithDimensions(columns, rows)
        .Build();

    Console.Error.WriteLine($"[TerminalHost] Terminal running with PID shell");

    // RunAsync blocks until the shell exits or cancellation
    var exitCode = await terminal.RunAsync(cts.Token);
    Console.Error.WriteLine($"[TerminalHost] Shell exited with code {exitCode}");

    // Send EXIT + CLOSE per protocol spec
    await adapter.SendExitAsync(exitCode, Aspire.Terminal.TerminalProtocol.ExitReason.Exited, cts.Token);

    return exitCode;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("[TerminalHost] Shutting down");
    return 0;
}
