// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Hex1b;
using Hex1b.Tokens;
using Microsoft.Extensions.Logging;

namespace Aspire.TerminalHost;

/// <summary>
/// Hosts an HMP1 presentation adapter on a Unix domain socket for one terminal session.
/// </summary>
internal sealed class Hmp1UdsServerListenerFilter(
    string socketPath,
    Hmp1PresentationAdapter presentation,
    ILogger<Hmp1UdsServerListenerFilter> logger) : IHex1bTerminalPresentationFilter
{
    private CancellationTokenSource? _listenerCts;
    private Task? _listenerTask;

    /// <inheritdoc />
    public ValueTask OnSessionStartAsync(
        int width,
        int height,
        DateTimeOffset timestamp,
        CancellationToken ct = default)
    {
        _listenerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _listenerTask = Task.Run(() => RunListenerAsync(_listenerCts.Token), _listenerCts.Token);

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<AnsiToken>> OnOutputAsync(
        IReadOnlyList<AppliedToken> appliedTokens,
        TimeSpan elapsed,
        CancellationToken ct = default)
    {
        return ValueTask.FromResult<IReadOnlyList<AnsiToken>>(
            appliedTokens.Select(t => t.Token).ToList());
    }

    /// <inheritdoc />
    public ValueTask OnInputAsync(
        IReadOnlyList<AnsiToken> tokens,
        TimeSpan elapsed,
        CancellationToken ct = default)
    {
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask OnResizeAsync(
        int width,
        int height,
        TimeSpan elapsed,
        CancellationToken ct = default)
    {
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public async ValueTask OnSessionEndAsync(TimeSpan elapsed, CancellationToken ct = default)
    {
        if (_listenerCts is null)
        {
            return;
        }

        await _listenerCts.CancelAsync().ConfigureAwait(false);

        if (_listenerTask is not null)
        {
            try
            {
                await _listenerTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _listenerCts.Dispose();
    }

    private async Task RunListenerAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var stream in Hmp1Transports.ListenUnixSocket(socketPath, ct).ConfigureAwait(false))
            {
                _ = AddClientAsync(stream, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    private async Task AddClientAsync(Stream stream, CancellationToken ct)
    {
        try
        {
            await presentation.AddClient(stream, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (
            ex is IOException or ObjectDisposedException or OperationCanceledException or InvalidOperationException)
        {
            try
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception disposeException) when (
                disposeException is IOException or ObjectDisposedException or OperationCanceledException)
            {
                logger.LogDebug(disposeException, "Failed to dispose an HMP1 consumer stream after its session ended.");
            }
        }
    }
}
