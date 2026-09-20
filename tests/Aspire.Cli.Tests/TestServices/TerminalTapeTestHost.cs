// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net.Sockets;
using System.Text;
using Hex1b;
using Hex1b.Automation;
using Microsoft.AspNetCore.InternalTesting;

namespace Aspire.Cli.Tests.TestServices;

/// <summary>
/// A real HMP resource terminal backed by controllable streams, without a shell or child process.
/// </summary>
internal sealed class TerminalTapeTestHost : IAsyncDisposable
{
    // Keep the socket outside the deeper CLI workspace to fit macOS's Unix socket path limit.
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("tape-");
    private readonly CancellationTokenSource _stopping = new();
    private readonly Stream _outputReader;
    private readonly Stream _inputWriter;
    private readonly Hex1bTerminal _terminal;
    private readonly Hmp1PresentationAdapter _presentation;
    private readonly Socket _listener = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
    private readonly ConcurrentBag<Hmp1ClientHandle> _clients = [];
    private readonly Task _runTask;
    private readonly Task _listenTask;
    private bool _disposed;

    private TerminalTapeTestHost(int width, int height)
    {
        SocketPath = Path.Combine(_directory.FullName, "h.sock");
        var output = new Pipe();
        var input = new Pipe();
        _outputReader = output.Reader.AsStream();
        Output = output.Writer.AsStream();
        Input = input.Reader.AsStream();
        _inputWriter = input.Writer.AsStream();
        _presentation = new Hmp1PresentationAdapter(width, height);
        _terminal = Hex1bTerminal.CreateBuilder()
            .WithWorkload(new StreamWorkloadAdapter(_outputReader, _inputWriter))
            .WithPresentation(_presentation)
            .Build();
        _runTask = _terminal.RunAsync(_stopping.Token);
        _listener.Bind(new UnixDomainSocketEndPoint(SocketPath));
        _listener.Listen();
        _listenTask = ListenAsync();
    }

    public string SocketPath { get; }
    public Stream Input { get; }
    public Stream Output { get; }
    public bool IsRunning => !_runTask.IsCompleted;

    public static Task<TerminalTapeTestHost> StartAsync(int width = 100, int height = 30)
    {
        return Task.FromResult(new TerminalTapeTestHost(width, height));
    }

    private async Task ListenAsync()
    {
        while (!_stopping.IsCancellationRequested)
        {
            var socket = await _listener.AcceptAsync(_stopping.Token);
            var client = await _presentation.AddClient(new NetworkStream(socket, ownsSocket: true), _stopping.Token);
            _clients.Add(client);
        }
    }

    public async Task<string> ReadInputAsync(int byteCount)
    {
        var bytes = new byte[byteCount];
        await Input.ReadExactlyAsync(bytes).AsTask().DefaultTimeout();
        return Encoding.UTF8.GetString(bytes);
    }

    public string GetScreenText()
    {
        using var snapshot = _terminal.CreateSnapshot();
        return snapshot.GetScreenText();
    }

    public (int Width, int Height) GetDimensions()
    {
        using var snapshot = _terminal.CreateSnapshot();
        return (snapshot.Width, snapshot.Height);
    }

    public async Task WriteAsync(string text)
    {
        await Output.WriteAsync(Encoding.UTF8.GetBytes(text));
        using var snapshot = await new Hex1bTerminalInputSequenceBuilder()
            .WaitUntil(snapshot => snapshot.ContainsText(text.Trim()), TimeSpan.FromSeconds(10), "Producer output was not applied.")
            .Build()
            .ApplyAsync(_terminal, CancellationToken.None)
            .DefaultTimeout();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        await _stopping.CancelAsync();
        try
        {
            await Task.WhenAll(_runTask, _listenTask);
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
            // The test owns this producer and stops it only during cleanup.
        }
        finally
        {
            _listener.Dispose();
            foreach (var client in _clients)
            {
                await client.DisposeAsync();
            }
            await _terminal.DisposeAsync();
            await Output.DisposeAsync();
            await Input.DisposeAsync();
            await _outputReader.DisposeAsync();
            await _inputWriter.DisposeAsync();
            _stopping.Dispose();
            _directory.Delete(recursive: true);
        }
    }
}
