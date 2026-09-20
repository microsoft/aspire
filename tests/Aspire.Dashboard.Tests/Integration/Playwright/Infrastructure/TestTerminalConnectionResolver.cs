// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Text;
using System.Threading.Channels;
using Aspire.Dashboard.Terminal;
using Aspire.Dashboard.Tests.Shared;
using Hex1b;
using Hex1b.Input;
using Xunit;

namespace Aspire.Dashboard.Tests.Integration.Playwright.Infrastructure;

internal sealed class TestTerminalConnectionResolver : ITerminalConnectionResolver, IAsyncDisposable
{
    private readonly ConcurrentDictionary<(string ResourceName, int ReplicaIndex), Lazy<TestTerminalConnection>> _producers = new();
    private readonly Channel<TestTerminalConnection> _connections = Channel.CreateUnbounded<TestTerminalConnection>();

    public async Task<Stream?> ConnectAsync(string resourceName, int replicaIndex, CancellationToken cancellationToken)
    {
        // All viewers of a replica share its real producer, including a separate primary
        // peer. HMP handshakes and HWT projection remain owned by Hex1b, not this fixture.
        var producer = _producers.GetOrAdd((resourceName, replicaIndex), _ => new(() => new TestTerminalConnection())).Value;
        var stream = producer.Connect();
        await _connections.Writer.WriteAsync(producer, cancellationToken);
        return stream;
    }

    public Task<TestTerminalConnection> AcceptConnectionAsync(CancellationToken cancellationToken) =>
        _connections.Reader.ReadAsync(cancellationToken).AsTask();

    public async Task DiscardPendingConnectionsAsync()
    {
        foreach (var producer in _producers.Values)
        {
            if (producer.IsValueCreated)
            {
                await producer.Value.DisposeAsync();
            }
        }
        _producers.Clear();
        while (_connections.Reader.TryRead(out _))
        {
        }
    }

    public ValueTask DisposeAsync() => new(DiscardPendingConnectionsAsync());
}

internal sealed class TestTerminalConnection : IAsyncDisposable
{
    public const int Columns = 137;
    public const int Rows = 41;
    private readonly TerminalTestProducer _producer = new(Columns, Rows, 100);

    public Hex1bAppWorkloadAdapter Workload => _producer.Workload;
    public Hmp1PresentationAdapter Presentation => _producer.Presentation;
    public int ConnectionCount => _producer.ConnectionCount;

    public Stream Connect() => _producer.Connect();

    public Task WaitForPeerHandshakesAsync(CancellationToken cancellationToken) =>
        _producer.WaitForPeerHandshakesAsync(cancellationToken);

    public Task WaitForProducerTextAsync(string text, CancellationToken cancellationToken) =>
        _producer.WaitForProducerTextAsync(text, cancellationToken);

    public async Task<string> ReadInputTextAsync(int length, CancellationToken cancellationToken)
    {
        var text = new StringBuilder();
        while (text.Length < length)
        {
            if (await Workload.InputEvents.ReadAsync(cancellationToken) is Hex1bKeyEvent key)
            {
                // Non-text keys such as an accidentally forwarded F6 must not disappear
                // from the assertion just because their Text property is empty.
                Assert.False(string.IsNullOrEmpty(key.Text), $"Unexpected terminal key: {key}");
                text.Append(key.Text);
            }
        }
        return text.ToString();
    }

    public ValueTask DisposeAsync() => _producer.DisposeAsync();
}
