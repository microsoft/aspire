// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using System.Net.Sockets;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Options;

namespace Aspire.Hosting.Dcp;

/// <summary>
/// Allocates and tracks public ports for proxyless endpoints that do not specify one.
/// </summary>
/// <remarks>
/// Uses a stateful hybrid scan over the configured non-ephemeral port range. The allocator starts
/// with an exhaustive pseudo-random walk to find a likely-free region, then walks incrementally after
/// each successful allocation so adjacent free ports are consumed efficiently. If a candidate is in
/// use, the allocator jumps back to the random walk instead of linearly scanning through a dense used
/// cluster.
///
/// This approach was tested against naive incremental allocation, pure random allocation, and
/// ephemeral port allocation. It was the fastest strategy tested while avoiding the worst-case
/// failure modes of naive incremental search.
/// </remarks>
internal sealed class ProxylessEndpointPortAllocator : IDisposable
{
    private readonly object _lock = new();
    private readonly int _rangeStart;
    private readonly int _rangeEnd;
    private readonly int _rangeSize;
    private readonly bool[] _visited;
    private readonly Dictionary<EndpointAnnotation, int> _reservedPorts = new(ReferenceEqualityComparer.Instance);
    private readonly Func<int, ProtocolType, bool> _tryProbe;
    private int _visitedCount;
    private int _randomWalkCursor;
    private readonly int _randomWalkStep;
    private int? _nextCandidate;
    private bool _disposed;

    public ProxylessEndpointPortAllocator(IOptions<DcpOptions> options)
        : this(
            options.Value.ProxylessEndpointPortRangeStart,
            options.Value.ProxylessEndpointPortRangeEnd,
            Random.Shared,
            TryProbePort)
    {
    }

    internal ProxylessEndpointPortAllocator(int rangeStart, int rangeEnd, Random random, Func<int, ProtocolType, bool> tryProbe)
        : this(rangeStart, rangeEnd, GetRandomOffset(random, rangeStart, rangeEnd), GetRandomCoprimeStep(random, GetRangeSize(rangeStart, rangeEnd)), tryProbe)
    {
    }

    internal ProxylessEndpointPortAllocator(int rangeStart, int rangeEnd, int randomWalkOffset, int randomWalkStep, Func<int, ProtocolType, bool> tryProbe)
    {
        if (rangeStart is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(rangeStart), rangeStart, "Port range start must be between 1 and 65535.");
        }

        if (rangeEnd is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(rangeEnd), rangeEnd, "Port range end must be between 1 and 65535.");
        }

        if (rangeStart > rangeEnd)
        {
            throw new ArgumentException("Port range start must be less than or equal to the range end.", nameof(rangeStart));
        }

        _rangeStart = rangeStart;
        _rangeEnd = rangeEnd;
        _rangeSize = rangeEnd - rangeStart + 1;

        if (randomWalkOffset < 0 || randomWalkOffset >= _rangeSize)
        {
            throw new ArgumentOutOfRangeException(nameof(randomWalkOffset), randomWalkOffset, "Random walk offset must be within the configured range size.");
        }

        if (randomWalkStep < 1 || randomWalkStep > _rangeSize || GreatestCommonDivisor(randomWalkStep, _rangeSize) != 1)
        {
            throw new ArgumentOutOfRangeException(nameof(randomWalkStep), randomWalkStep, "Random walk step must be coprime with the configured range size.");
        }

        _visited = new bool[_rangeSize];
        _randomWalkCursor = randomWalkOffset;
        _randomWalkStep = randomWalkStep;
        _tryProbe = tryProbe;
    }

    public int AllocatePort(EndpointAnnotation endpoint)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_reservedPorts.TryGetValue(endpoint, out var reservedPort))
            {
                return reservedPort;
            }

            var port = AllocatePortCore(endpoint.Protocol);
            _reservedPorts.Add(endpoint, port);
            return port;
        }
    }

    public void ExcludePort(int port)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (TryGetPortIndex(port, out var index))
            {
                MarkVisited(index);
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }
    }

    private int AllocatePortCore(ProtocolType protocol)
    {
        _nextCandidate ??= GetNextRandomWalkCandidate();

        while (_nextCandidate is int candidate)
        {
            var port = _rangeStart + candidate;
            MarkVisited(candidate);

            // Bind only long enough to confirm the OS currently considers the port available.
            // After that, the allocator's visited/reserved state prevents Aspire from handing
            // the same port to another endpoint in this app model.
            if (_tryProbe(port, protocol))
            {
                _nextCandidate = GetNextIncrementalCandidate(candidate);
                return port;
            }

            _nextCandidate = GetNextRandomWalkCandidate();
        }

        throw new InvalidOperationException($"No available ports were found in the configured proxyless endpoint port range {_rangeStart}-{_rangeEnd}.");
    }

    private int? GetNextIncrementalCandidate(int afterIndex)
    {
        if (_visitedCount == _rangeSize)
        {
            return null;
        }

        for (var i = 1; i <= _rangeSize; i++)
        {
            var candidate = (afterIndex + i) % _rangeSize;
            if (!_visited[candidate])
            {
                return candidate;
            }
        }

        return null;
    }

    private int? GetNextRandomWalkCandidate()
    {
        if (_visitedCount == _rangeSize)
        {
            return null;
        }

        for (var i = 0; i < _rangeSize; i++)
        {
            var candidate = _randomWalkCursor;
            _randomWalkCursor = (_randomWalkCursor + _randomWalkStep) % _rangeSize;

            if (!_visited[candidate])
            {
                return candidate;
            }
        }

        return null;
    }

    private void MarkVisited(int index)
    {
        if (_visited[index])
        {
            return;
        }

        _visited[index] = true;
        _visitedCount++;
    }

    private bool TryGetPortIndex(int port, out int index)
    {
        if (port < _rangeStart || port > _rangeEnd)
        {
            index = -1;
            return false;
        }

        index = port - _rangeStart;
        return true;
    }

    // Exposed for tests so port-availability checks use the exact same IPv4+IPv6 probe as
    // production allocation. A test helper that probed a different address family could hand
    // back a port the allocator then rejects, producing spurious "no available ports" failures.
    internal static bool TryProbePort(int port, ProtocolType protocol)
    {
        return protocol == ProtocolType.Udp
            ? TryProbePort(port, SocketType.Dgram, ProtocolType.Udp)
            : TryProbePort(port, SocketType.Stream, ProtocolType.Tcp);
    }

    private static bool TryProbePort(int port, SocketType socketType, ProtocolType protocolType)
    {
        var sockets = new List<Socket>();

        try
        {
            sockets.Add(CreateBoundSocket(AddressFamily.InterNetwork, socketType, protocolType, new IPEndPoint(IPAddress.Any, port)));

            if (Socket.OSSupportsIPv6)
            {
                sockets.Add(CreateBoundSocket(AddressFamily.InterNetworkV6, socketType, protocolType, new IPEndPoint(IPAddress.IPv6Any, port)));
            }

            DisposeSockets(sockets);
            return true;
        }
        catch (SocketException ex) when (ex.SocketErrorCode is SocketError.AddressAlreadyInUse or SocketError.AccessDenied)
        {
            DisposeSockets(sockets);
            return false;
        }
        catch
        {
            DisposeSockets(sockets);
            throw;
        }
    }

    private static Socket CreateBoundSocket(AddressFamily addressFamily, SocketType socketType, ProtocolType protocolType, IPEndPoint endPoint)
    {
        var socket = new Socket(addressFamily, socketType, protocolType)
        {
            ExclusiveAddressUse = true
        };

        if (addressFamily == AddressFamily.InterNetworkV6)
        {
            socket.DualMode = false;
        }

        socket.Bind(endPoint);
        return socket;
    }

    private static int GetRandomCoprimeStep(Random random, int rangeSize)
    {
        if (rangeSize == 1)
        {
            return 1;
        }

        while (true)
        {
            var step = random.Next(1, rangeSize);
            if (GreatestCommonDivisor(step, rangeSize) == 1)
            {
                return step;
            }
        }
    }

    private static int GetRandomOffset(Random random, int rangeStart, int rangeEnd)
    {
        return random.Next(GetRangeSize(rangeStart, rangeEnd));
    }

    private static int GetRangeSize(int rangeStart, int rangeEnd)
    {
        if (rangeStart is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(rangeStart), rangeStart, "Port range start must be between 1 and 65535.");
        }

        if (rangeEnd is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(rangeEnd), rangeEnd, "Port range end must be between 1 and 65535.");
        }

        if (rangeStart > rangeEnd)
        {
            throw new ArgumentException("Port range start must be less than or equal to the range end.", nameof(rangeStart));
        }

        return rangeEnd - rangeStart + 1;
    }

    private static int GreatestCommonDivisor(int a, int b)
    {
        while (b != 0)
        {
            var temp = b;
            b = a % b;
            a = temp;
        }

        return Math.Abs(a);
    }

    private static void DisposeSockets(IEnumerable<Socket> sockets)
    {
        foreach (var socket in sockets)
        {
            socket.Dispose();
        }
    }

}
