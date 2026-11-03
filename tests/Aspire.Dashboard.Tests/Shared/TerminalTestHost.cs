// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
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
    private readonly TerminalTestProducer _producer = new(100, 30, 10000);
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
    public StatusCode? AttachmentFailureStatus { get; init; }
    public bool FailAttachmentDuringHandshake { get; init; }
    private string Endpoint => _useGrpc ? "/api/apphost-terminal?terminalId=test" : "/api/terminal?resource=test";

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
                Query = (_useGrpc ? "terminalId=test" : "resource=test") +
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

    public Task<Stream?> ConnectAsync(string resourceName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Assert.Equal("test", resourceName);
        return Task.FromResult<Stream?>(_producer.Connect());
    }

    private async Task<Stream> AttachTerminalAsync(string terminalId, CancellationToken cancellationToken)
    {
        Assert.Equal("test", terminalId);
        var failure = AttachmentFailureStatus is { } status
            ? new RpcException(new Status(status, "Terminal attachment failed."))
            : null;
        if (failure is not null && !FailAttachmentDuringHandshake)
        {
            throw failure;
        }

        // A completed terminal reports Ended without attaching to the disposed
        // producer or returning any HMP handshake bytes.
        var connection = failure is not null || Volatile.Read(ref _terminalEnded) != 0
            ? Stream.Null
            : (await ConnectAsync(terminalId, cancellationToken))!;
        var disposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _attachmentDisposals.Add(disposed.Task);
        var call = new AsyncDuplexStreamingCall<TerminalClientFrame, TerminalServerFrame>(
            new TerminalRequestWriter(connection),
            new TerminalResponseReader(connection, () => Volatile.Read(ref _terminalEnded) != 0,
                () => Volatile.Read(ref _includeHmpExit) != 0, _endedObserved, failure),
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
        TaskCompletionSource endedObserved, RpcException? failure) : IAsyncStreamReader<TerminalServerFrame>
    {
        // Deliberately split HMP frames across small gRPC messages: transport boundaries
        // must not affect the HMP handshake, UTF-8 input, graphics, or terminal state.
        private readonly byte[] _buffer = new byte[31];
        private bool _sentEnded;
        private bool _sentExit;

        public TerminalServerFrame Current { get; private set; } = new();

        public async Task<bool> MoveNext(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (failure is not null)
            {
                throw failure;
            }

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
