// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Aspire.Shared;

namespace Aspire.Tray.Spike;

internal sealed class CliAppHostClient : IAppHostClient
{
    private readonly string _cliPath;
    private readonly TimeSpan _livenessTimeout;
    private readonly TimeSpan _retryDelay;
    private readonly CliAppHostCommands _commands;

    public CliAppHostClient(string cliPath)
        : this(cliPath, TimeSpan.FromSeconds(60), TrayCliProtocol.LivenessTimeout, TimeSpan.FromSeconds(1))
    {
    }

    internal CliAppHostClient(string cliPath, TimeSpan stopTimeout, TimeSpan livenessTimeout, TimeSpan retryDelay)
    {
        _ = CliProcess.CreateStartInfo(cliPath);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(livenessTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(retryDelay, TimeSpan.Zero);
        _cliPath = cliPath;
        _livenessTimeout = livenessTimeout;
        _retryDelay = retryDelay;
        _commands = new(cliPath, stopTimeout);
    }

    public Task<StopResult> StopAsync(AppHostId id, CancellationToken cancellationToken)
        => _commands.StopAsync(id, cancellationToken);

    public async IAsyncEnumerable<AppHostSnapshot> WatchAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var latest = new AppHostSnapshot([], DiscoveryState.Connecting);
        var delay = _retryDelay;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return latest with { Discovery = DiscoveryState.Connecting };
            Exception failure;
            var stream = WatchConnectionAsync(cancellationToken).GetAsyncEnumerator(cancellationToken);
            await using (stream.ConfigureAwait(false))
            {
                while (true)
                {
                    bool available;
                    try
                    {
                        available = await stream.MoveNextAsync().ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex) when (ex is IOException or Win32Exception or CliProtocolException
                        or JsonException or InvalidOperationException or OperationCanceledException)
                    {
                        failure = ex;
                        break;
                    }
                    if (!available)
                    {
                        failure = new EndOfStreamException();
                        break;
                    }
                    latest = stream.Current;
                    delay = _retryDelay;
                    yield return latest;
                }
            }

            // Do not print payloads, parser messages, or raw stderr: dashboard URLs contain tokens.
            Console.Error.WriteLine($"CLI discovery unavailable ({failure.GetType().Name}).");
            if (failure is CliDiscoveryException { Code: "limit_exceeded" })
            {
                yield return latest with { Discovery = DiscoveryState.LimitExceeded };
                yield break;
            }
            if (failure is CliProtocolException or JsonException)
            {
                yield return latest with { Discovery = DiscoveryState.Incompatible };
                yield break;
            }
            yield return latest with { Discovery = DiscoveryState.Disconnected };
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 10));
        }
    }

    internal static ProcessStartInfo CreateWatchStartInfo(string executable)
        => CliProcess.CreateStartInfo(executable, "ps", "--follow", "--format", "json",
            "--protocol-version", "1", "--non-interactive", "--nologo");

    private async IAsyncEnumerable<AppHostSnapshot> WatchConnectionAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var process = Process.Start(CreateWatchStartInfo(_cliPath))
            ?? throw new InvalidOperationException("Could not start the Aspire CLI.");
        using var streams = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var stderr = Task.CompletedTask;
        try
        {
            process.StandardInput.Close();
            stderr = CliProcess.DrainAsync(process.StandardError, streams.Token);
            streams.CancelAfter(_livenessTimeout);
            var receivedSnapshot = false;
            await foreach (var line in CliProtocol.ReadLinesAsync(process.StandardOutput, streams.Token).ConfigureAwait(false))
            {
                var message = CliProtocol.ReadWatchMessage(line);
                streams.CancelAfter(_livenessTimeout);
                switch (message.Type)
                {
                    case "snapshot":
                        var snapshot = CliProtocol.ReadSnapshot(message);
                        receivedSnapshot = true;
                        yield return snapshot;
                        break;
                    case "heartbeat" when receivedSnapshot:
                        break;
                    case "error":
                        throw new CliDiscoveryException(message.ErrorCode!);
                    default:
                        throw new CliProtocolException();
                }
            }
            // EOF is a lost watcher, never evidence that all AppHosts stopped.
            if (!receivedSnapshot)
            {
                throw new CliProtocolException();
            }
            throw new EndOfStreamException();
        }
        finally
        {
            await streams.CancelAsync().ConfigureAwait(false);
            await CliProcess.TerminateOwnedChildAsync(process).ConfigureAwait(false);
            try
            {
                await stderr.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (streams.IsCancellationRequested)
            {
            }
        }
    }
}
