// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Terminal;
using Aspire.TerminalHost;
using Hex1b;

// The Aspire Terminal Host bridges a PTY process to the Aspire Terminal Protocol
// over a Unix domain socket. It is launched by the AppHost orchestrator for resources
// with .WithTerminal() and serves as the terminal state manager using Hex1b.
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

var adapter = new UnixDomainSocketPresentationAdapter(socketPath, columns, rows);

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

try
{
    // Wait for client connection before starting the shell
    await adapter.WaitForClientAsync(cts.Token);

    // Build the Hex1b terminal: PTY process + UDS presentation
    await using var terminal = Hex1bTerminal.CreateBuilder()
        .WithPresentation(adapter)
        .WithPtyProcess(shell, shellArgs)
        .WithDimensions(columns, rows)
        .Build();

    var exitCode = await terminal.RunAsync(cts.Token);
    Console.Error.WriteLine($"[Aspire.TerminalHost] Shell exited with code {exitCode}");

    await adapter.SendExitAsync(exitCode, TerminalProtocol.ExitReason.Exited, CancellationToken.None);
    return exitCode;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("[Aspire.TerminalHost] Shutting down");
    return 0;
}
