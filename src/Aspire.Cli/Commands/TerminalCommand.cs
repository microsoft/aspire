// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.Globalization;
using System.Net.Sockets;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Configuration;
using Aspire.Cli.Interaction;
using Aspire.Cli.Resources;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Utils;
using Aspire.Terminal;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Commands;

/// <summary>
/// Attaches to an interactive terminal session for a resource.
/// Connects to the resource's terminal UDS using the Aspire Terminal Protocol,
/// puts the local console in raw mode, and bridges stdin/stdout bidirectionally.
/// Press Ctrl+] to detach.
/// </summary>
internal sealed class TerminalCommand : BaseCommand
{
    internal override HelpGroup HelpGroup => HelpGroup.Monitoring;

    private readonly IInteractionService _interactionService;
    private readonly AppHostConnectionResolver _connectionResolver;
    private readonly ILogger<TerminalCommand> _logger;

    private static readonly Argument<string> s_resourceArgument = new("resource")
    {
        Description = "The name of the resource to attach a terminal to."
    };

    private static readonly OptionWithLegacy<FileInfo?> s_appHostOption =
        new("--apphost", "--project", SharedCommandStrings.AppHostOptionDescription);

    public TerminalCommand(
        IInteractionService interactionService,
        IAuxiliaryBackchannelMonitor backchannelMonitor,
        IFeatures features,
        ICliUpdateNotifier updateNotifier,
        CliExecutionContext executionContext,
        AspireCliTelemetry telemetry,
        ILogger<TerminalCommand> logger)
        : base("terminal", "Attach to an interactive terminal session for a resource.", features, updateNotifier, executionContext, interactionService, telemetry)
    {
        _interactionService = interactionService;
        _logger = logger;
        _connectionResolver = new AppHostConnectionResolver(backchannelMonitor, interactionService, executionContext, logger);

        Arguments.Add(s_resourceArgument);
        Options.Add(s_appHostOption);
    }

    protected override async Task<int> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        using var activity = Telemetry.StartDiagnosticActivity(Name);

        var resourceName = parseResult.GetValue(s_resourceArgument)!;
        var passedAppHostProjectFile = parseResult.GetValue(s_appHostOption);

        var result = await _connectionResolver.ResolveConnectionAsync(
            passedAppHostProjectFile,
            SharedCommandStrings.ScanningForRunningAppHosts,
            string.Format(CultureInfo.CurrentCulture, SharedCommandStrings.SelectAppHost, "attach terminal to"),
            SharedCommandStrings.AppHostNotRunning,
            cancellationToken).ConfigureAwait(false);

        if (!result.Success)
        {
            _interactionService.DisplayMessage(KnownEmojis.Information, result.ErrorMessage);
            return ExitCodeConstants.Success;
        }

        var connection = result.Connection!;

        // Get terminal info from the AppHost
        var terminalInfo = await connection.GetTerminalInfoAsync(resourceName, cancellationToken).ConfigureAwait(false);

        if (!terminalInfo.IsAvailable || string.IsNullOrEmpty(terminalInfo.SocketPath))
        {
            _interactionService.DisplayError($"Terminal is not available for resource '{resourceName}'. Ensure the resource has .WithTerminal() configured.");
            return ExitCodeConstants.FailedToDotnetRunAppHost;
        }

        _logger.LogDebug("Connecting to terminal at {SocketPath}", terminalInfo.SocketPath);

        // Connect to the terminal UDS
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(terminalInfo.SocketPath), cancellationToken).ConfigureAwait(false);
        }
        catch (SocketException ex)
        {
            _interactionService.DisplayError($"Failed to connect to terminal socket: {ex.Message}");
            return ExitCodeConstants.FailedToDotnetRunAppHost;
        }

        await using var stream = new NetworkStream(socket, ownsSocket: false);
        var reader = new TerminalFrameReader(stream);
        var writer = new TerminalFrameWriter(stream);

        // Read HELLO
        var helloFrame = await reader.ReadFrameAsync(cancellationToken).ConfigureAwait(false);
        if (helloFrame is null || helloFrame.Value.Type != TerminalProtocol.MessageType.Hello)
        {
            _interactionService.DisplayError("Terminal protocol error: did not receive HELLO frame.");
            return ExitCodeConstants.FailedToDotnetRunAppHost;
        }

        var (version, cols, rows, flags) = helloFrame.Value.ParseHello();
        _logger.LogDebug("Terminal HELLO: v{Version}, {Cols}x{Rows}, flags={Flags}", version, cols, rows, flags);

        _interactionService.DisplayMessage(KnownEmojis.Gear, $"Connected to terminal for '{resourceName}' ({cols}x{rows}). Press Ctrl+] to detach.");

        // Enter raw mode and bridge I/O
        var originalControlC = Console.TreatControlCAsInput;
        Console.TreatControlCAsInput = true;

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            // Send initial resize
            if (!Console.IsInputRedirected)
            {
                try
                {
                    await writer.WriteResizeAsync(
                        (ushort)Console.WindowWidth,
                        (ushort)Console.WindowHeight,
                        cts.Token).ConfigureAwait(false);
                }
                catch
                {
                    // Console may not support dimensions
                }
            }

            var outputTask = ReadOutputAsync(reader, cts);
            var inputTask = ReadInputAsync(writer, cts);

            await Task.WhenAny(outputTask, inputTask).ConfigureAwait(false);
            await cts.CancelAsync().ConfigureAwait(false);

            try
            {
                await Task.WhenAll(outputTask, inputTask).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected
            }
        }
        finally
        {
            Console.TreatControlCAsInput = originalControlC;
        }

        return ExitCodeConstants.Success;
    }

    private static async Task ReadOutputAsync(TerminalFrameReader reader, CancellationTokenSource cts)
    {
        try
        {
            while (!cts.Token.IsCancellationRequested)
            {
                var frame = await reader.ReadFrameAsync(cts.Token).ConfigureAwait(false);
                if (frame is null)
                {
                    break;
                }

                switch (frame.Value.Type)
                {
                    case TerminalProtocol.MessageType.Data:
                        using (var stdout = Console.OpenStandardOutput())
                        {
                            await stdout.WriteAsync(frame.Value.Payload, cts.Token).ConfigureAwait(false);
                            await stdout.FlushAsync(cts.Token).ConfigureAwait(false);
                        }
                        break;

                    case TerminalProtocol.MessageType.Exit:
                        var (exitCode, reason) = frame.Value.ParseExit();
                        Console.Error.WriteLine($"\r\n[Process exited: code={exitCode}, reason={reason}]");
                        await cts.CancelAsync().ConfigureAwait(false);
                        return;

                    case TerminalProtocol.MessageType.Close:
                        Console.Error.WriteLine("\r\n[Server closed connection]");
                        await cts.CancelAsync().ConfigureAwait(false);
                        return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected
        }
        catch (IOException)
        {
            // Disconnected
        }
    }

    private static async Task ReadInputAsync(TerminalFrameWriter writer, CancellationTokenSource cts)
    {
        try
        {
            using var stdin = Console.OpenStandardInput();
            var buffer = new byte[256];

            while (!cts.Token.IsCancellationRequested)
            {
                var bytesRead = await stdin.ReadAsync(buffer, cts.Token).ConfigureAwait(false);
                if (bytesRead == 0)
                {
                    break;
                }

                // Check for Ctrl+] (0x1D) to detach
                for (var i = 0; i < bytesRead; i++)
                {
                    if (buffer[i] == 0x1D)
                    {
                        Console.Error.WriteLine("\r\n[Detached]");
                        try
                        {
                            await writer.WriteCloseAsync(cts.Token).ConfigureAwait(false);
                        }
                        catch
                        {
                            // Best effort
                        }

                        await cts.CancelAsync().ConfigureAwait(false);
                        return;
                    }
                }

                await writer.WriteDataAsync(buffer.AsMemory(0, bytesRead), cts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected
        }
        catch (IOException)
        {
            // Disconnected
        }
    }
}
