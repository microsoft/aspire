// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Sockets;
using Aspire.Terminal;

if (args.Length < 1)
{
    Console.Error.WriteLine("Usage: Terminals.Client <socket-path>");
    Console.Error.WriteLine();
    Console.Error.WriteLine("Connects to an Aspire Terminal Protocol server on the given UDS path.");
    Console.Error.WriteLine("Ctrl+] to detach.");
    return 1;
}

var socketPath = args[0];

if (!File.Exists(socketPath))
{
    Console.Error.WriteLine($"Socket not found: {socketPath}");
    return 1;
}

Console.Error.WriteLine($"Connecting to {socketPath}...");

using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath));

await using var stream = new NetworkStream(socket, ownsSocket: false);
var reader = new TerminalFrameReader(stream);
var writer = new TerminalFrameWriter(stream);

// Read HELLO
var helloFrame = await reader.ReadFrameAsync();
if (helloFrame is null || helloFrame.Value.Type != TerminalProtocol.MessageType.Hello)
{
    Console.Error.WriteLine("Error: did not receive HELLO frame");
    return 1;
}

var (version, cols, rows, flags) = helloFrame.Value.ParseHello();
Console.Error.WriteLine($"Connected! Protocol v{version}, {cols}x{rows}, flags={flags}");
Console.Error.WriteLine("Press Ctrl+] to detach.");
Console.Error.WriteLine();

using var cts = new CancellationTokenSource();

// Put console in raw mode
var originalMode = Console.TreatControlCAsInput;
Console.TreatControlCAsInput = true;

try
{
    // Start reading output from server
    var outputTask = Task.Run(async () =>
    {
        try
        {
            while (!cts.Token.IsCancellationRequested)
            {
                var frame = await reader.ReadFrameAsync(cts.Token);
                if (frame is null)
                {
                    break;
                }

                switch (frame.Value.Type)
                {
                    case TerminalProtocol.MessageType.Data:
                        using (var stdout = Console.OpenStandardOutput())
                        {
                            await stdout.WriteAsync(frame.Value.Payload, cts.Token);
                            await stdout.FlushAsync(cts.Token);
                        }
                        break;

                    case TerminalProtocol.MessageType.Exit:
                        var (exitCode, reason) = frame.Value.ParseExit();
                        Console.Error.WriteLine($"\r\n[Terminal exited: code={exitCode}, reason={reason}]");
                        await cts.CancelAsync();
                        break;

                    case TerminalProtocol.MessageType.Close:
                        Console.Error.WriteLine("\r\n[Server closed connection]");
                        await cts.CancelAsync();
                        break;

                    case TerminalProtocol.MessageType.Resize:
                        var (newCols, newRows) = frame.Value.ParseResize();
                        Console.Error.WriteLine($"\r\n[Server resized to {newCols}x{newRows}]");
                        break;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
    }, cts.Token);

    // Read input from local console and send to server
    var inputTask = Task.Run(async () =>
    {
        try
        {
            using var stdin = Console.OpenStandardInput();
            var buffer = new byte[256];

            while (!cts.Token.IsCancellationRequested)
            {
                var bytesRead = await stdin.ReadAsync(buffer, cts.Token);
                if (bytesRead == 0)
                {
                    break;
                }

                // Check for Ctrl+] (0x1D) to detach
                for (var i = 0; i < bytesRead; i++)
                {
                    if (buffer[i] == 0x1D) // Ctrl+]
                    {
                        Console.Error.WriteLine("\r\n[Detached]");
                        await writer.WriteCloseAsync(cts.Token);
                        await cts.CancelAsync();
                        return;
                    }
                }

                await writer.WriteDataAsync(buffer.AsMemory(0, bytesRead), cts.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
    }, cts.Token);

    // Send initial resize based on current console size
    if (!Console.IsInputRedirected)
    {
        try
        {
            await writer.WriteResizeAsync(
                (ushort)Console.WindowWidth,
                (ushort)Console.WindowHeight,
                cts.Token);
        }
        catch { /* console may not support this */ }
    }

    await Task.WhenAny(outputTask, inputTask);
    await cts.CancelAsync();

    try { await Task.WhenAll(outputTask, inputTask); }
    catch (OperationCanceledException) { }
}
finally
{
    Console.TreatControlCAsInput = originalMode;
}

return 0;
