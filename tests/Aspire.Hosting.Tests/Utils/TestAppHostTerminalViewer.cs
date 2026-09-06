// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Terminals;
using Hex1b;
using Hex1b.Automation;
using Microsoft.AspNetCore.InternalTesting;

#pragma warning disable ASPIRETERMINAL002 // Test consumer of the experimental AppHost terminal API.

namespace Aspire.Hosting.Tests.Utils;

internal sealed class TestAppHostTerminalViewer : IAsyncDisposable
{
    private readonly CancellationTokenSource _attachmentCts = new();
    private readonly CancellationTokenSource _clientCts = new();
    private readonly TaskCompletionSource _connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TestDuplexStream _serverStream;
    private readonly TestDuplexStream _clientStream;
    private readonly Hex1bTerminal _client;
    private readonly Task _attachment;
    private readonly Task _run;
    private bool _disposed;

    private TestAppHostTerminalViewer(TerminalService service, string terminalId)
    {
        (_serverStream, _clientStream) = TestDuplexStream.CreatePair();
        _client = Hex1bTerminal.CreateBuilder()
            .WithHeadless()
            .WithDimensions(80, 24)
            .WithHmp1Stream(_clientStream, options =>
            {
                options.DefaultRole = Hmp1Role.Secondary;
                options.OnConnected = (_, _) =>
                {
                    _connected.TrySetResult();
                    return Task.CompletedTask;
                };
            })
            .Build();

        _attachment = service.AttachAsync(terminalId, _serverStream, _attachmentCts.Token);
        _run = _client.RunAsync(_clientCts.Token);
    }

    public static async Task<TestAppHostTerminalViewer> ConnectAsync(TerminalService service, string terminalId)
    {
        var viewer = new TestAppHostTerminalViewer(service, terminalId);
        try
        {
            var completed = await Task.WhenAny(viewer._connected.Task, viewer._run, viewer._attachment).DefaultTimeout();
            await completed;
            Assert.True(viewer._connected.Task.IsCompletedSuccessfully, "The HMP1 connection ended before the handshake completed.");
            return viewer;
        }
        catch
        {
            await viewer.DisposeAsync();
            throw;
        }
    }

    public Task WaitForTextAsync(string text)
        => new Hex1bTerminalAutomator(_client, TimeSpan.FromSeconds(30)).WaitUntilTextAsync(text);

    public Task SendTextAsync(string text)
        => new Hex1bTerminalAutomator(_client, TimeSpan.FromSeconds(30)).TypeAsync(text);

    public async Task DisconnectPeerAsync()
    {
        await StopClientAsync();
        // No attachment cancellation: the server must detect the peer's EOF and release the RPC itself.
        await _attachment.DefaultTimeout();
    }

    private async Task StopClientAsync()
    {
        await _clientCts.CancelAsync();
        try
        {
            await _run.DefaultTimeout();
        }
        catch (OperationCanceledException) when (_clientCts.IsCancellationRequested)
        {
        }

        await _client.DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            await _attachmentCts.CancelAsync();
            await _attachment.DefaultTimeout();
        }
        finally
        {
            await StopClientAsync();
            _serverStream.Dispose();
            _clientStream.Dispose();
            _attachmentCts.Dispose();
            _clientCts.Dispose();
        }
    }
}
