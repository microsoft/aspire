// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net.WebSockets;
using Aspire.Dashboard.Configuration;
using Aspire.Dashboard.Terminal;
using Aspire.Dashboard.Tests.Integration;
using Aspire.Hosting;
using Hex1b;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aspire.Dashboard.Tests.Shared;

internal sealed class TerminalTestHost : ITerminalConnectionResolver, IAsyncDisposable
{
    private readonly ConcurrentBag<Task<Hmp1ClientHandle>> _connections = [];
    private readonly CancellationTokenSource _stopping = new();
    private readonly DashboardWebApplication _app;
    private readonly Hex1bTerminal _producer;

    public TerminalTestHost(ITestOutputHelper output, bool requireAuthentication)
    {
        Workload = new Hex1bAppWorkloadAdapter();
        Presentation = new Hmp1PresentationAdapter(100, 30);
        _producer = Hex1bTerminal.CreateBuilder()
            .WithWorkload(Workload)
            .WithPresentation(Presentation)
            .WithDimensions(100, 30)
            .WithScrollback(100)
            .Build();
        _app = IntegrationTestHelpers.CreateDashboardWebApplication(output,
            additionalConfiguration: configuration =>
            {
                if (requireAuthentication)
                {
                    configuration[DashboardConfigNames.DashboardFrontendAuthModeName.ConfigKey] = nameof(FrontendAuthMode.BrowserToken);
                    configuration[DashboardConfigNames.DashboardFrontendBrowserTokenName.ConfigKey] = "test-token";
                }
            },
            preConfigureBuilder: builder => builder.Services.AddSingleton<ITerminalConnectionResolver>(this));
    }

    public Hex1bAppWorkloadAdapter Workload { get; }
    public Hmp1PresentationAdapter Presentation { get; }
    public int ConnectionCount => _connections.Count;

    public Task StartAsync(CancellationToken cancellationToken) => _app.StartAsync(cancellationToken);

    public async Task<ClientWebSocket> ConnectBrowserAsync(CancellationToken cancellationToken)
    {
        var frontend = new Uri(_app.FrontendSingleEndPointAccessor().GetResolvedAddress());
        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Origin", frontend.GetLeftPart(UriPartial.Authority));
        try
        {
            await socket.ConnectAsync(new UriBuilder(frontend)
            {
                Scheme = "ws",
                Path = "/api/terminal",
                Query = "resource=test&replica=0"
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
        var toClient = new Pipe();
        var toServer = new Pipe();
        var server = new DuplexStream(toServer.Reader.AsStream(), toClient.Writer.AsStream());
        var client = new DuplexStream(toClient.Reader.AsStream(), toServer.Writer.AsStream());
        _connections.Add(Presentation.AddClient(server, _stopping.Token));
        return Task.FromResult<Stream?>(client);
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();
        await _app.DisposeAsync();
        foreach (var connection in _connections)
        {
            await using var handle = await connection;
        }
        await _producer.DisposeAsync();
        _stopping.Dispose();
    }

    private sealed class DuplexStream(Stream input, Stream output) : Stream
    {
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
            if (disposing)
            {
                input.Dispose();
                output.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
