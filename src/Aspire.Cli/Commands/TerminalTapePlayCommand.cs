// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.Globalization;
using System.Net.Sockets;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Interaction;
using Aspire.Cli.Resources;
using Hex1b;
using Hex1b.Automation;
using Hex1b.Tokens;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Commands;

/// <summary>
/// Plays a tape against an existing resource terminal without owning its process or dimensions.
/// </summary>
internal sealed class TerminalTapePlayCommand : BaseCommand
{
    // CancellationTokenSource's underlying timer accepts at most uint.MaxValue - 1 milliseconds.
    private const int MaximumTimeoutSeconds = (int)((uint.MaxValue - 1) / 1000);
    internal override HelpGroup HelpGroup => HelpGroup.Monitoring;

    private readonly AppHostConnectionResolver _connectionResolver;
    private readonly TerminalResourceResolver _terminalResolver;
    private readonly ILogger<TerminalTapePlayCommand> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly Argument<string> _resourceArgument = new("resource")
    {
        Description = TerminalCommandStrings.ResourceArgumentDescription
    };
    private readonly Option<string> _tapeFileOption = new("--tape-file")
    {
        Description = TerminalCommandStrings.TapeFileDescription,
        Required = true
    };
    private readonly Option<int?> _replicaOption = new("--replica", "-r")
    {
        Description = TerminalCommandStrings.ReplicaOptionDescription
    };
    private readonly OptionWithLegacy<FileInfo?> _appHostOption =
        new("--apphost", "--project", SharedCommandStrings.AppHostOptionDescription);
    private readonly Option<int> _timeoutOption = new("--timeout")
    {
        Description = TerminalCommandStrings.TapeTimeoutDescription,
        DefaultValueFactory = _ => 120
    };

    public TerminalTapePlayCommand(
        AppHostConnectionResolver connectionResolver,
        TerminalResourceResolver terminalResolver,
        ILogger<TerminalTapePlayCommand> logger,
        TimeProvider timeProvider,
        CommonCommandServices services) : base("play", TerminalCommandStrings.TapePlayDescription, services)
    {
        _connectionResolver = connectionResolver;
        _terminalResolver = terminalResolver;
        _logger = logger;
        _timeProvider = timeProvider;
        Arguments.Add(_resourceArgument);
        Options.Add(_tapeFileOption);
        Options.Add(_replicaOption);
        Options.Add(_appHostOption);
        Options.Add(_timeoutOption);
    }

    protected override async Task<CommandResult> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        using var activity = Telemetry.StartDiagnosticActivity("terminal tape play");
        // The final screen is the command's stdout payload; discovery, warnings and failures belong on stderr.
        InteractionService.Console = ConsoleOutput.Error;
        var resourceName = parseResult.GetValue(_resourceArgument)!;
        var tapePath = parseResult.GetValue(_tapeFileOption)!;
        var timeoutSeconds = parseResult.GetValue(_timeoutOption);
        if (string.IsNullOrWhiteSpace(resourceName))
        {
            return CommandResult.Failure(CliExitCodes.InvalidCommand, TerminalCommandStrings.ResourceRequired);
        }
        if (timeoutSeconds is <= 0 or > MaximumTimeoutSeconds)
        {
            return CommandResult.Failure(CliExitCodes.InvalidCommand,
                string.Format(CultureInfo.CurrentCulture, TerminalCommandStrings.TapeTimeoutInvalid, MaximumTimeoutSeconds));
        }

        FileInfo file;
        TapeDocument tape;
        try
        {
            file = new FileInfo(Path.GetFullPath(tapePath, ExecutionContext.WorkingDirectory.FullName));
            tape = await new TapeParser().ParseAsync(file, cancellationToken).ConfigureAwait(false);
        }
        catch (TapeParseException ex)
        {
            DisplayDiagnostics(ex.Diagnostics);
            return CommandResult.Failure(CliExitCodes.InvalidCommand);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return CommandResult.Failure(CliExitCodes.InvalidCommand,
                string.Format(CultureInfo.CurrentCulture, TerminalCommandStrings.TapeFileReadFailed, tapePath, ex.Message));
        }

        var connectionResult = await _connectionResolver.ResolveConnectionAsync(
            parseResult.GetValue(_appHostOption),
            SharedCommandStrings.ScanningForRunningAppHosts,
            string.Format(CultureInfo.CurrentCulture, SharedCommandStrings.SelectAppHost, TerminalCommandStrings.TapeSelectAppHostAction),
            SharedCommandStrings.AppHostNotRunning,
            cancellationToken).ConfigureAwait(false);
        if (!connectionResult.Success)
        {
            return CommandResult.FromExitCode(AppHostConnectionResultHandler.DisplayFailureAsError(
                connectionResult, InteractionService, CliExitCodes.FailedToFindProject));
        }
        if (!connectionResult.Connection.SupportsTerminalsV1)
        {
            return CommandResult.Failure(CliExitCodes.AppHostIncompatible, TerminalCommandStrings.TerminalIncompatible);
        }

        var (canonicalName, replica) = await _terminalResolver.ResolveAsync(
            connectionResult.Connection, resourceName, parseResult.GetValue(_replicaOption), cancellationToken).ConfigureAwait(false);
        if (replica is null)
        {
            return CommandResult.Failure(CliExitCodes.InvalidCommand);
        }
        if (!replica.IsAlive)
        {
            return CommandResult.Failure(CliExitCodes.FailedToExecuteResourceCommand,
                string.Format(CultureInfo.CurrentCulture, TerminalCommandStrings.TapeReplicaExited, replica.ReplicaIndex, canonicalName));
        }

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds), _timeProvider);
        using var playback = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var disconnected = 0;
        try
        {
            await using var adapter = new Hmp1WorkloadAdapter(new Hmp1ClientOptions
            {
                StreamFactory = async ct => await Hmp1Transports.ConnectUnixSocket(replica.ConsumerUdsPath, ct).ConfigureAwait(false),
                DefaultRole = Hmp1Role.Secondary,
                DisplayName = $"aspire-tape:{Environment.ProcessId}",
                OnDisconnected = _ =>
                {
                    Interlocked.Exchange(ref disconnected, 1);
                    // HMP can ignore writes after disconnection. Cancel the player instead of reporting a
                    // successful tape whose input never reached the resource.
                    playback.Cancel();
                    return Task.CompletedTask;
                }
            });
            await adapter.ConnectAsync(playback.Token).ConfigureAwait(false);

            var initialScreen = new InitialScreenFilter();
            // A preconnected workload starts the mirror's pumps during Build(). No local process is created,
            // and no scrollback is enabled: VHS Wait+Screen must inspect this mirror's visible screen.
            await using var terminal = Hex1bTerminal.CreateBuilder()
                .WithHeadless()
                .WithWorkload(adapter)
                .WithDimensions(adapter.RemoteWidth, adapter.RemoteHeight)
                .AddPresentationFilter(initialScreen)
                .Build();
            await initialScreen.Ready.WaitAsync(playback.Token).ConfigureAwait(false);

            var player = new TapePlayer();
            var options = new TapePlaybackOptions
            {
                WorkingDirectory = file.DirectoryName
            };
            var validation = await player.ValidateAsync(tape, terminal, options, playback.Token).ConfigureAwait(false);
            DisplayDiagnostics(validation.Diagnostics);
            if (!validation.CanExecute)
            {
                return CommandResult.Failure(CliExitCodes.InvalidCommand);
            }

            using var result = await player.PlayAsync(tape, terminal, options, playback.Token).ConfigureAwait(false);
            playback.Token.ThrowIfCancellationRequested();
            DisplayDiagnostics(result.Diagnostics.Except(validation.Diagnostics));
            InteractionService.DisplayRawText(result.FinalSnapshot.GetScreenText(), ConsoleOutput.Standard);
            return CommandResult.Success();
        }
        catch (TapeValidationException ex)
        {
            DisplayDiagnostics(ex.Diagnostics);
            return CommandResult.Failure(CliExitCodes.InvalidCommand);
        }
        catch (TapePlaybackException ex)
        {
            InteractionService.DisplayRawText(ex.TerminalText, ConsoleOutput.Standard);
            // The native diagnostic already contains the failing source span and command context.
            return CommandResult.Failure(CliExitCodes.FailedToExecuteResourceCommand, ex.Message);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            return CommandResult.Failure(CliExitCodes.WaitTimeout,
                string.Format(CultureInfo.CurrentCulture, TerminalCommandStrings.TapeTimeout, timeoutSeconds));
        }
        catch (OperationCanceledException) when (Volatile.Read(ref disconnected) != 0)
        {
            return CommandResult.Failure(CliExitCodes.FailedToExecuteResourceCommand, TerminalCommandStrings.TapeConnectionClosed);
        }
        catch (Exception ex) when (ex is IOException or SocketException or TimeoutException)
        {
            _logger.LogDebug(ex, "Terminal tape connection failed for {ResourceName}, replica {ReplicaIndex}.", canonicalName, replica.ReplicaIndex);
            return CommandResult.Failure(CliExitCodes.FailedToExecuteResourceCommand,
                string.Format(CultureInfo.CurrentCulture, TerminalCommandStrings.TapePlaybackFailed, ex.Message));
        }
    }

    private void DisplayDiagnostics(IEnumerable<TapeDiagnostic> diagnostics)
    {
        foreach (var diagnostic in diagnostics)
        {
            var span = diagnostic.Span;
            InteractionService.DisplayRawText(
                FormattableString.Invariant($"{span.SourceName}:{span.Line}:{span.Column}: {diagnostic.Severity} {diagnostic.Code}: {diagnostic.Message}"),
                ConsoleOutput.Error);
        }
    }

    private sealed class InitialScreenFilter : IHex1bTerminalPresentationFilter
    {
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Ready => _ready.Task;

        public ValueTask<IReadOnlyList<AnsiToken>> OnOutputAsync(
            IReadOnlyList<AppliedToken> appliedTokens, TimeSpan elapsed, CancellationToken cancellationToken = default)
        {
            // This pin exposes no public initial-replay barrier. A fresh headless HMP mirror first calls its
            // presentation filters after committing the authoritative screen, even when it is empty.
            // https://github.com/mitchdenny/hex1b/blob/496ccf508470eed8744dbe46675e3d26928e8c91/src/Hex1b/Hmp1/Hex1bTerminal.Hmp1Replay.cs#L117-L149
            _ready.TrySetResult();
            return ValueTask.FromResult<IReadOnlyList<AnsiToken>>(appliedTokens.Select(t => t.Token).ToArray());
        }

        public ValueTask OnSessionStartAsync(int width, int height, DateTimeOffset timestamp, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;
        public ValueTask OnInputAsync(IReadOnlyList<AnsiToken> tokens, TimeSpan elapsed, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;
        public ValueTask OnResizeAsync(int width, int height, TimeSpan elapsed, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;
        public ValueTask OnSessionEndAsync(TimeSpan elapsed, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;
    }
}
