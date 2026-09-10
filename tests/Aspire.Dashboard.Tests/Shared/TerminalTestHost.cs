// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net.WebSockets;
using Aspire.Dashboard.Configuration;
using Aspire.Dashboard.Terminal;
using Aspire.Dashboard.Tests.Integration;
using Aspire.DashboardService.Proto.V1;
using Aspire.Hosting;
using Google.Protobuf;
using Grpc.Core;
using Hex1b;
using Hex1b.Automation;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aspire.Dashboard.Tests.Shared;

internal sealed class TerminalTestHost : ITerminalConnectionResolver, IAsyncDisposable
{
    private readonly DashboardWebApplication _app;
    private readonly TerminalTestProducer _producer = new(100, 30, 100);
    private readonly bool _useGrpc;
    private readonly ConcurrentBag<Task> _attachmentDisposals = [];
    private int _disposedAttachments;
    private int _terminalEnded;
    private int _includeHmpExit;
    private readonly TaskCompletionSource _endedObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TerminalTestHost(ITestOutputHelper output, bool requireAuthentication, bool useGrpc = false)
    {
        _useGrpc = useGrpc;
        _app = IntegrationTestHelpers.CreateDashboardWebApplication(output,
            additionalConfiguration: configuration =>
            {
                if (requireAuthentication)
                {
                    configuration[DashboardConfigNames.DashboardFrontendAuthModeName.ConfigKey] = nameof(FrontendAuthMode.BrowserToken);
                    configuration[DashboardConfigNames.DashboardFrontendBrowserTokenName.ConfigKey] = "test-token";
                }
            },
            preConfigureBuilder: builder =>
            {
                builder.Services.AddSingleton<ITerminalConnectionResolver>(this);
                builder.Services.AddSingleton<IDashboardClient>(new TestDashboardClient(attachTerminal: AttachTerminalAsync));
            });
    }

    public Hex1bAppWorkloadAdapter Workload => _producer.Workload;
    public Hmp1PresentationAdapter Presentation => _producer.Presentation;
    public int ConnectionCount => _producer.ConnectionCount;
    public int DisposedAttachments => Volatile.Read(ref _disposedAttachments);
    private string Endpoint => _useGrpc ? "/api/apphost-terminal?terminalId=test" : "/api/terminal?resource=test&replica=0";

    public Task StartAsync(CancellationToken cancellationToken) => _app.StartAsync(cancellationToken);

    public TerminalViewSession CreateViewSession(bool readOnly) =>
        _app.Services.GetRequiredService<TerminalViewSessionRegistry>().Create(Endpoint, readOnly);

    public Task WaitForEndedObservedAsync(CancellationToken cancellationToken) => _endedObserved.Task.WaitAsync(cancellationToken);

    public async Task EndTerminalAsync(bool includeHmpExit)
    {
        Volatile.Write(ref _includeHmpExit, includeHmpExit ? 1 : 0);
        Volatile.Write(ref _terminalEnded, 1);
        await Presentation.DisposeAsync();
    }

    public Task WaitForProducerTextAsync(string text, CancellationToken cancellationToken) =>
        _producer.WaitForProducerTextAsync(text, cancellationToken);

    public Task WaitForPeerHandshakesAsync(CancellationToken cancellationToken) =>
        _producer.WaitForPeerHandshakesAsync(cancellationToken);

    public Task WaitForAttachmentsReleasedAsync(CancellationToken cancellationToken) =>
        _producer.WaitForAttachmentsReleasedAsync(cancellationToken);

    public Task WaitForDisposedAttachmentsAsync(CancellationToken cancellationToken) =>
        Task.WhenAll(_attachmentDisposals).WaitAsync(cancellationToken);

    public Task<ClientWebSocket> ConnectBrowserAsync(CancellationToken cancellationToken) =>
        ConnectBrowserCoreAsync(viewId: null, cancellationToken);

    public Task<ClientWebSocket> ConnectBrowserAsync(TerminalViewSession session, CancellationToken cancellationToken) =>
        ConnectBrowserCoreAsync(session.Id, cancellationToken);

    private async Task<ClientWebSocket> ConnectBrowserCoreAsync(string? viewId, CancellationToken cancellationToken)
    {
        var frontend = new Uri(_app.FrontendSingleEndPointAccessor().GetResolvedAddress());
        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Origin", frontend.GetLeftPart(UriPartial.Authority));
        try
        {
            await socket.ConnectAsync(new UriBuilder(frontend)
            {
                Scheme = "ws",
                Path = _useGrpc ? "/api/apphost-terminal" : "/api/terminal",
                Query = (_useGrpc ? "terminalId=test" : "resource=test&replica=0") +
                    (viewId is null ? string.Empty : $"&viewId={viewId}")
            }.Uri, cancellationToken);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    public Task<Stream?> ConnectAsync(string resourceName, int replicaIndex, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<Stream?>(_producer.Connect());
    }

    private async Task<Stream> AttachTerminalAsync(string terminalId, CancellationToken cancellationToken)
    {
        Assert.Equal("test", terminalId);
        // A completed terminal reports Ended without attaching to the disposed
        // producer or returning any HMP handshake bytes.
        var connection = Volatile.Read(ref _terminalEnded) != 0
            ? Stream.Null
            : (await ConnectAsync(terminalId, 0, cancellationToken))!;
        var disposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _attachmentDisposals.Add(disposed.Task);
        var call = new AsyncDuplexStreamingCall<TerminalClientFrame, TerminalServerFrame>(
            new TerminalRequestWriter(connection),
            new TerminalResponseReader(connection, () => Volatile.Read(ref _terminalEnded) != 0,
                () => Volatile.Read(ref _includeHmpExit) != 0, _endedObserved),
            Task.FromResult(new Metadata()),
            () => Status.DefaultSuccess,
            () => new Metadata(),
            () =>
            {
                Interlocked.Increment(ref _disposedAttachments);
                connection.Dispose();
                disposed.TrySetResult();
            });
        var stream = new GrpcTerminalClientStream(call, terminalId);
        await stream.SendSelectorAsync(cancellationToken);
        return stream;
    }

    public async ValueTask DisposeAsync()
    {
        await _producer.CancelConnectionsAsync();
        try
        {
            await _app.DisposeAsync();
        }
        finally
        {
            await _producer.DisposeAsync();
        }
    }

    private sealed class TerminalResponseReader(Stream stream, Func<bool> terminalEnded, Func<bool> includeHmpExit,
        TaskCompletionSource endedObserved) : IAsyncStreamReader<TerminalServerFrame>
    {
        // Deliberately split HMP frames across small gRPC messages: transport boundaries
        // must not affect the HMP handshake, UTF-8 input, graphics, or terminal state.
        private readonly byte[] _buffer = new byte[31];
        private bool _sentEnded;
        private bool _sentExit;

        public TerminalServerFrame Current { get; private set; } = new();

        public async Task<bool> MoveNext(CancellationToken cancellationToken)
        {
            var count = await stream.ReadAsync(_buffer, cancellationToken);
            if (count == 0)
            {
                if (terminalEnded() && includeHmpExit() && !_sentExit)
                {
                    // HMP Exit precedes gRPC Ended: type 0x06, four-byte LE payload
                    // length, then a four-byte LE exit code (zero in this fixture).
                    // https://github.com/mitchdenny/hex1b/blob/798b26c/docs/muxer-protocol.md#exit-0x06
                    _sentExit = true;
                    byte[] exit = [0x06, 4, 0, 0, 0, 0, 0, 0, 0];
                    Current = new TerminalServerFrame { Data = ByteString.CopyFrom(exit) };
                    return true;
                }

                if (terminalEnded() && !_sentEnded)
                {
                    _sentEnded = true;
                    Current = new TerminalServerFrame { Ended = true };
                    endedObserved.TrySetResult();
                    return true;
                }

                return false;
            }

            Current = new TerminalServerFrame { Data = ByteString.CopyFrom(_buffer, 0, count) };
            return true;
        }
    }

    private sealed class TerminalRequestWriter(Stream stream) : IClientStreamWriter<TerminalClientFrame>
    {
        public WriteOptions? WriteOptions { get; set; }

        public Task CompleteAsync() => Task.CompletedTask;

        public Task WriteAsync(TerminalClientFrame message) => WriteAsync(message, CancellationToken.None);

        public async Task WriteAsync(TerminalClientFrame message, CancellationToken cancellationToken)
        {
            if (!message.Data.IsEmpty)
            {
                await stream.WriteAsync(message.Data.Memory, cancellationToken);
            }
        }
    }
}

internal sealed class TerminalTestProducer : IAsyncDisposable
{
    private readonly ConcurrentBag<Task<Hmp1ClientHandle>> _connections = [];
    private readonly ConcurrentBag<Task> _disconnections = [];
    private readonly CancellationTokenSource _stopping = new();
    private readonly Hex1bTerminal _terminal;
    private int _disposed;

    public TerminalTestProducer(int width, int height, int scrollback)
    {
        Workload = new Hex1bAppWorkloadAdapter();
        Presentation = new Hmp1PresentationAdapter(width, height);
        _terminal = Hex1bTerminal.CreateBuilder()
            .WithWorkload(Workload)
            .WithPresentation(Presentation)
            .WithDimensions(width, height)
            .WithScrollback(scrollback)
            .Build();
    }

    public Hex1bAppWorkloadAdapter Workload { get; }
    public Hmp1PresentationAdapter Presentation { get; }
    public int Width => Workload.Width;
    public int Height => Workload.Height;
    public int ConnectionCount => _connections.Count;

    public Stream Connect()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var toClient = new Pipe();
        var toServer = new Pipe();
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = new DuplexStream(toServer.Reader.AsStream(), toClient.Writer.AsStream(), onDispose: null);
        var client = new DuplexStream(toClient.Reader.AsStream(), toServer.Writer.AsStream(), () => disconnected.TrySetResult());
        _disconnections.Add(disconnected.Task);
        _connections.Add(Presentation.AddClient(server, _stopping.Token));
        return client;
    }

    public async Task WaitForProducerTextAsync(string text, CancellationToken cancellationToken)
    {
        using var snapshot = await new Hex1bTerminalInputSequenceBuilder()
            .WaitUntil(snapshot => snapshot.ContainsText(text), TimeSpan.FromSeconds(10), "Terminal producer output was not applied.")
            .Build()
            .ApplyAsync(_terminal, cancellationToken);
    }

    public Task WaitForPeerHandshakesAsync(CancellationToken cancellationToken) =>
        Task.WhenAll(_connections).WaitAsync(cancellationToken);

    public Task WaitForAttachmentsReleasedAsync(CancellationToken cancellationToken) =>
        Task.WhenAll(_disconnections).WaitAsync(cancellationToken);

    internal Task CancelConnectionsAsync() => _stopping.CancelAsync();

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _stopping.CancelAsync();
        try
        {
            foreach (var connection in _connections)
            {
                try
                {
                    await using var handle = await connection;
                }
                catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
                {
                    // A cancelled request can end during the producer's initial handshake.
                }
            }
        }
        finally
        {
            await _terminal.DisposeAsync();
            _stopping.Dispose();
        }
    }

    private sealed class DuplexStream(Stream input, Stream output, Action? onDispose) : Stream
    {
        private int _disposed;

        public override bool CanRead => input.CanRead;
        public override bool CanWrite => output.CanWrite;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => input.Read(buffer, offset, count);
        public override void Write(byte[] buffer, int offset, int count) => output.Write(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => input.ReadAsync(buffer, cancellationToken);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => output.WriteAsync(buffer, cancellationToken);
        public override void Flush() => output.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => output.FlushAsync(cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                input.Dispose();
                output.Dispose();
                onDispose?.Invoke();
            }
            base.Dispose(disposing);
        }
    }
}
