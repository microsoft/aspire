// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.TerminalHost;
using Hex1b;

// The Aspire Terminal Host bridges a PTY process to the Aspire Terminal Protocol
// over a Unix domain socket. It is launched by the AppHost orchestrator for resources
// with .WithTerminal() and serves as the terminal state manager using Hex1b.
//
// Architecture:
//   - Hex1b runs the shell in headless mode (manages internal terminal state)
//   - A presentation filter intercepts all output and broadcasts to UDS clients
//   - Clients can disconnect and reconnect; current screen state is replayed
//
// Environment variables:
//   TERMINAL_SOCKET_PATH  — UDS path for client connections (required)
//   TERMINAL_COLUMNS      — initial terminal width (default: 120)
//   TERMINAL_ROWS         — initial terminal height (default: 30)
//   TERMINAL_SHELL        — shell command to run (default: platform-specific)
//   TERMINAL_SHELL_ARGS   — shell arguments, semicolon-separated (default: platform-specific)

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

var shellArgsStr = Environment.GetEnvironmentVariable("TERMINAL_SHELL_ARGS");
var shellArgs = !string.IsNullOrEmpty(shellArgsStr)
    ? shellArgsStr.Split(';', StringSplitOptions.RemoveEmptyEntries)
    : (OperatingSystem.IsWindows() ? ["-NoLogo"] : ["--norc"]);

Console.Error.WriteLine($"[Aspire.TerminalHost] shell={shell}, size={columns}x{rows}, socket={socketPath}");

// Create the socket server (presentation filter)
await using var server = new TerminalSocketServer(socketPath, columns, rows);

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

try
{
    // Build the terminal: headless presentation + PTY process + our filter for UDS broadcasting
    await using var terminal = Hex1bTerminal.CreateBuilder()
        .WithHeadless()
        .WithPtyProcess(shell, shellArgs)
        .WithDimensions(columns, rows)
        .AddPresentationFilter(server)
        .Build();

    // Give the server access to the terminal for snapshots and input forwarding
    server.SetTerminal(terminal);

    // Start accepting client connections
    await server.StartListeningAsync(cts.Token);

    // Run the terminal (blocks until shell exits or cancellation)
    var exitCode = await terminal.RunAsync(cts.Token);
    Console.Error.WriteLine($"[Aspire.TerminalHost] Shell exited with code {exitCode}");
    return exitCode;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("[Aspire.TerminalHost] Shutting down");
    return 0;
}
