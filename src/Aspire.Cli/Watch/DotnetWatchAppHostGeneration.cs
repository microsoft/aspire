// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO.Pipes;
using System.Text.Json;
using Aspire.Shared;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Watch;

internal sealed class DotnetWatchAppHostGeneration : IAsyncDisposable, IGracefulShutdownWindow
{
    private readonly CancellationTokenSource _stop = new();
    private readonly CancellationTokenSource _graceful;
    private readonly IGracefulShutdownWindow _commandShutdown;
    private readonly TimeSpan _shutdownBudget;
    private readonly ILogger _logger;
    private readonly TaskCompletionSource _invalidation = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _hostStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly NamedPipeServerStream _pipe;
    private readonly Task _listener;
    private int _gracefulWindowStarted;
    private string? _hostInstance;
    private volatile bool _isEnabled;

    internal DotnetWatchAppHostGeneration(IGracefulShutdownWindow commandShutdown, TimeSpan shutdownBudget, ILogger logger)
    {
        _commandShutdown = commandShutdown;
        _shutdownBudget = shutdownBudget;
        _logger = logger;
        _graceful = CancellationTokenSource.CreateLinkedTokenSource(commandShutdown.GracefulShutdownToken);
        Session = Guid.NewGuid().ToString("N");
        // Unix pipe names share the host's runtime socket directory and its small path limit.
        // The full independent session identity is still required inside every notification.
        PipeName = $"ah{Session[..14]}";
        _pipe = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        _listener = ListenAsync();
    }

    internal string PipeName { get; }
    internal string Session { get; }
    internal bool IsEnabled => _isEnabled;
    internal Task Invalidated => _invalidation.Task;
    internal Task HostStarted => _hostStarted.Task;
    internal bool RequiresLauncherMonitoring { get; init; } = true;
    internal Action? OnBackchannelEstablished { get; init; }

    bool IGracefulShutdownWindow.IsEnabled => _commandShutdown.IsEnabled;
    public CancellationToken GracefulShutdownToken => _graceful.Token;

    internal void ApplyEnvironment(IDictionary<string, string> environment)
    {
        _isEnabled = true;
        environment[DotnetWatchConfiguration.HostControlPipe] = PipeName;
        environment[DotnetWatchConfiguration.HostSession] = Session;
    }

    internal void Disable() => _isEnabled = false;

    internal void MarkBackchannelEstablished() => OnBackchannelEstablished?.Invoke();

    public void BeginGracefulWindow()
    {
        // A model restart must not consume the command-wide shutdown budget: a later Ctrl+C
        // still needs its full cooperative window, and each generation needs a fresh deadline.
        if (Interlocked.Exchange(ref _gracefulWindowStarted, 1) == 0)
        {
            _graceful.CancelAfter(_commandShutdown.IsEnabled ? _shutdownBudget : TimeSpan.Zero);
        }
    }

    private async Task ListenAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                await _pipe.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
                try
                {
                    using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                    readTimeout.CancelAfter(TimeSpan.FromSeconds(2));
                    var message = await DotnetWatchHostProtocol.ReadAsync(_pipe, readTimeout.Token).ConfigureAwait(false);
                    if (IsEnabled && message.Version == 1 && message.Session == Session)
                    {
                        if (message.Kind == DotnetWatchHostProtocol.HostStarted && message.Instance is { Length: > 0 } instance)
                        {
                            _hostStarted.TrySetResult();
                            if (_hostInstance is null)
                            {
                                _hostInstance = instance;
                                continue;
                            }
                            if (_hostInstance == instance)
                            {
                                continue;
                            }
                        }
                        else if (message.Kind != DotnetWatchHostProtocol.ModelInvalidated)
                        {
                            continue;
                        }

                        _invalidation.TrySetResult();
                        return;
                    }
                }
                catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException or OperationCanceledException)
                {
                    _logger.LogDebug(exception, "Ignoring an invalid AppHost watch notification.");
                }
                finally
                {
                    _pipe.Disconnect();
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
        catch (IOException exception) when (_stop.IsCancellationRequested)
        {
            _logger.LogDebug(exception, "AppHost watch notification listener stopped.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        await _listener.ConfigureAwait(false);
        await _pipe.DisposeAsync().ConfigureAwait(false);
        _graceful.Dispose();
        _stop.Dispose();
    }
}
