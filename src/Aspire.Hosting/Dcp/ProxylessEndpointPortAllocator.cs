// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Options;

namespace Aspire.Hosting.Dcp;

/// <summary>
/// Allocates and tracks non-ephemeral ports that Aspire selects before DCP starts a workload.
/// </summary>
/// <remarks>
/// Uses a stateful hybrid scan over the configured non-ephemeral port range. The allocator starts
/// with an exhaustive pseudo-random walk to find a likely-free region, then walks incrementally after
/// each successful allocation so adjacent free ports are consumed efficiently. If a candidate is in
/// use, the allocator jumps back to the random walk instead of linearly scanning through a dense used
/// cluster.
///
/// The random walk cursor is independent of incremental scanning. Incremental successes consume adjacent
/// candidates the random permutation would have reached later, but the next random jump resumes from the
/// previous permutation position and skips ports that are no longer available. This keeps the search
/// exhaustive while opportunistically exploiting nearby free ports.
///
/// This approach was tested against naive incremental allocation, pure random allocation, and
/// ephemeral port allocation. It was the fastest strategy tested while avoiding the worst-case
/// failure modes of naive incremental search.
///
/// The probe releases the port before the workload binds it, so allocators for different applications
/// in the same process (for example, tests that run AppHosts in parallel) could otherwise hand out the
/// same port. Port availability is therefore tracked in a process-wide
/// <see cref="ProxylessEndpointPortReservations"/>, the single availability authority for every allocator.
/// Allocated and explicitly excluded ports hold a reservation reference until the allocator is disposed, and
/// ports whose probe failed are permanently excluded so no allocator in the process probes them again.
/// </remarks>
internal sealed class ProxylessEndpointPortAllocator : IDisposable
{
    private readonly object _lock = new();
    private readonly int _rangeStart;
    private readonly int _rangeEnd;
    private readonly int _rangeSize;
    private readonly Dictionary<EndpointAnnotation, int> _reservedPorts = new(ReferenceEqualityComparer.Instance);
    private readonly Func<int, ProtocolType, bool> _tryProbe;
    private readonly ProxylessEndpointPortReservations _processReservations;
    // Ports this allocator holds a reservation reference for, released on disposal.
    private readonly HashSet<int> _ownedPorts = [];
    private int _randomWalkCursor;
    private readonly int _randomWalkStep;
    private int? _nextCandidate;
    private bool _disposed;

    public ProxylessEndpointPortAllocator(IOptions<DcpOptions> options)
        : this(
            options.Value.ProxylessEndpointPortRangeStart,
            options.Value.ProxylessEndpointPortRangeEnd,
            Random.Shared,
            TryProbePort,
            ProxylessEndpointPortReservations.Shared)
    {
    }

    internal ProxylessEndpointPortAllocator(int rangeStart, int rangeEnd, Random random, Func<int, ProtocolType, bool> tryProbe, ProxylessEndpointPortReservations? processReservations = null)
        : this(rangeStart, rangeEnd, GetRandomOffset(random, rangeStart, rangeEnd), GetRandomCoprimeStep(random, GetRangeSize(rangeStart, rangeEnd)), tryProbe, processReservations)
    {
    }

    internal ProxylessEndpointPortAllocator(int rangeStart, int rangeEnd, int randomWalkOffset, int randomWalkStep, Func<int, ProtocolType, bool> tryProbe, ProxylessEndpointPortReservations? processReservations = null)
    {
        PortRange.ValidateRange(rangeStart, rangeEnd, nameof(rangeStart), nameof(rangeEnd));

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

        _randomWalkCursor = randomWalkOffset;
        _randomWalkStep = randomWalkStep;
        _tryProbe = tryProbe;
        _processReservations = processReservations ?? new ProxylessEndpointPortReservations();
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

    public int AllocatePort(ProtocolType protocol)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            return AllocatePortCore(protocol);
        }
    }

    public void ExcludePort(int port)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (port is < IPEndPoint.MinPort or > IPEndPoint.MaxPort || _ownedPorts.Contains(port))
            {
                return;
            }

            // Exclusions are shared process-wide. Each one holds a reference so this allocator's disposal does
            // not clear an exclusion another allocator still requires. A permanently excluded port stays permanent.
            if (_processReservations.TryAddReference(port))
            {
                _ownedPorts.Add(port);
            }
        }
    }

    public bool TryReservePort(int port, ProtocolType protocol, bool allowInUse = false)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (port < _rangeStart || port > _rangeEnd || !_processReservations.TryReserve(port))
            {
                return false;
            }

            if (_nextCandidate == port - _rangeStart)
            {
                _nextCandidate = null;
            }

            bool probeSucceeded;
            try
            {
                probeSucceeded = allowInUse || _tryProbe(port, protocol);
            }
            catch
            {
                _processReservations.Release(port);
                throw;
            }

            if (probeSucceeded)
            {
                _ownedPorts.Add(port);
                return true;
            }

            _processReservations.MarkUnavailable(port);
            return false;
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

            foreach (var port in _ownedPorts)
            {
                _processReservations.Release(port);
            }

            _ownedPorts.Clear();
        }
    }

    private int AllocatePortCore(ProtocolType protocol)
    {
        _nextCandidate ??= GetNextRandomWalkCandidate();

        while (_nextCandidate is int candidate)
        {
            var port = _rangeStart + candidate;

            // Reserve the port process-wide before probing so another application's allocator in this
            // process cannot claim it between the probe releasing it and the workload binding it. A cached
            // incremental candidate can have been excluded or claimed since it was selected, so check again.
            if (_processReservations.TryReserve(port))
            {
                bool probeSucceeded;
                try
                {
                    // Bind only long enough to confirm the OS currently considers the port available.
                    // After that, the process-wide reservation prevents Aspire from handing the same
                    // port to another endpoint in this or any other app model in the process.
                    probeSucceeded = _tryProbe(port, protocol);
                }
                catch
                {
                    _processReservations.Release(port);
                    throw;
                }

                if (probeSucceeded)
                {
                    _ownedPorts.Add(port);
                    _nextCandidate = GetNextIncrementalCandidate(candidate);
                    return port;
                }

                // Something outside Aspire's tracking holds the port. Remember that process-wide without
                // registering cleanup so no allocator in this process probes it again.
                _processReservations.MarkUnavailable(port);
            }

            _nextCandidate = GetNextRandomWalkCandidate();
        }

        throw CreateNoAvailablePortsException();
    }

    private bool IsCandidateAvailable(int index)
    {
        return _processReservations.IsAvailable(_rangeStart + index);
    }

    private int? GetNextIncrementalCandidate(int afterIndex)
    {
        for (var i = 1; i < _rangeSize; i++)
        {
            var candidate = (afterIndex + i) % _rangeSize;
            if (IsCandidateAvailable(candidate))
            {
                return candidate;
            }
        }

        // Fall back to the random walk on the next allocation, which reports exhaustion if nothing is left.
        return null;
    }

    private int GetNextRandomWalkCandidate()
    {
        for (var i = 0; i < _rangeSize; i++)
        {
            var candidate = _randomWalkCursor;
            _randomWalkCursor = (_randomWalkCursor + _randomWalkStep) % _rangeSize;

            if (IsCandidateAvailable(candidate))
            {
                return candidate;
            }
        }

        throw CreateNoAvailablePortsException();
    }

    private InvalidOperationException CreateNoAvailablePortsException()
    {
        return new InvalidOperationException($"No available ports were found in the configured proxyless endpoint port range {_rangeStart}-{_rangeEnd}.");
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
        // On macOS a wildcard bind can succeed while a loopback listener already owns the port.
        // Probe loopback first, releasing those sockets before probing the wildcard addresses so
        // that the probe does not conflict with itself.
        return TryProbeAddresses(port, socketType, protocolType, IPAddress.Loopback, IPAddress.IPv6Loopback)
            && TryProbeAddresses(port, socketType, protocolType, IPAddress.Any, IPAddress.IPv6Any);
    }

    private static bool TryProbeAddresses(int port, SocketType socketType, ProtocolType protocolType, IPAddress ipv4Address, IPAddress ipv6Address)
    {
        var sockets = new List<Socket>();

        try
        {
            sockets.Add(CreateBoundSocket(AddressFamily.InterNetwork, socketType, protocolType, new IPEndPoint(ipv4Address, port)));

            if (Socket.OSSupportsIPv6)
            {
                sockets.Add(CreateBoundSocket(AddressFamily.InterNetworkV6, socketType, protocolType, new IPEndPoint(ipv6Address, port)));
            }

            DisposeSockets(sockets);
            return true;
        }
        catch
        {
            // This is a best-effort availability probe: any failure to bind (port in use, access
            // denied, or anything else) means we can't reserve this port, so treat it as unavailable
            // rather than letting an unexpected exception abort the whole allocation.
            DisposeSockets(sockets);
            return false;
        }
    }

    private static Socket CreateBoundSocket(AddressFamily addressFamily, SocketType socketType, ProtocolType protocolType, IPEndPoint endPoint)
    {
        var socket = new Socket(addressFamily, socketType, protocolType)
        {
            ExclusiveAddressUse = true
        };

        var socketReturned = false;
        try
        {
            if (addressFamily == AddressFamily.InterNetworkV6)
            {
                socket.DualMode = false;
            }

            socket.Bind(endPoint);
            socketReturned = true;
            return socket;
        }
        finally
        {
            if (!socketReturned)
            {
                socket.Dispose();
            }
        }
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
        PortRange.ValidateRange(rangeStart, rangeEnd, nameof(rangeStart), nameof(rangeEnd));

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

/// <summary>
/// Tracks port availability shared by every <see cref="ProxylessEndpointPortAllocator"/> in the process so that
/// allocators for different applications never hand out the same port or re-probe a port known to be in use.
/// </summary>
/// <remarks>
/// The port space is small and dense, so state is a directly indexed array (64 KB) rather than a hashed set.
/// Each entry is <c>0</c> when the port is available for dynamic allocation, <c>1</c>-<c>254</c> for the number
/// of live reservation references, or <c>255</c> when the port is permanently excluded after a failed probe.
/// The counts track exclusion lifetimes only; they do not grant permission to share a resource or listener.
/// Callers track which references they hold, because the shared count cannot identify owners.
/// </remarks>
internal sealed class ProxylessEndpointPortReservations
{
    private const byte Available = 0;
    private const byte MaxReferences = 254;
    private const byte PermanentlyExcluded = 255;

    private readonly object _lock = new();
    private readonly byte[] _states = new byte[IPEndPoint.MaxPort + 1];

    public static ProxylessEndpointPortReservations Shared { get; } = new();

    public bool IsAvailable(int port)
    {
        lock (_lock)
        {
            return _states[port] == Available;
        }
    }

    /// <summary>
    /// Atomically claims an available port for dynamic allocation by taking its first reservation reference.
    /// The caller must <see cref="Release"/> the reference when it no longer needs the port.
    /// </summary>
    public bool TryReserve(int port)
    {
        lock (_lock)
        {
            if (_states[port] != Available)
            {
                return false;
            }

            _states[port] = 1;
            return true;
        }
    }

    /// <summary>
    /// Adds a reservation reference for an explicitly excluded port, whether or not it is already reserved.
    /// Returns <see langword="false"/> without adding a reference when the port is permanently excluded.
    /// The caller must <see cref="Release"/> each reference it adds.
    /// </summary>
    public bool TryAddReference(int port)
    {
        lock (_lock)
        {
            var state = _states[port];
            if (state == PermanentlyExcluded)
            {
                return false;
            }

            if (state == MaxReferences)
            {
                throw new InvalidOperationException($"Port {port} has too many live reservation references.");
            }

            _states[port] = (byte)(state + 1);
            return true;
        }
    }

    /// <summary>
    /// Releases one reservation reference. Permanently excluded ports are never released.
    /// </summary>
    public void Release(int port)
    {
        lock (_lock)
        {
            var state = _states[port];
            if (state == PermanentlyExcluded)
            {
                return;
            }

            Debug.Assert(state != Available, $"Port {port} was released without a reservation reference.");
            if (state != Available)
            {
                _states[port] = (byte)(state - 1);
            }
        }
    }

    /// <summary>
    /// Records that a port is in use outside Aspire's tracking. The port is never released.
    /// </summary>
    public void MarkUnavailable(int port)
    {
        lock (_lock)
        {
            _states[port] = PermanentlyExcluded;
        }
    }

    internal int GetReferenceCount(int port)
    {
        lock (_lock)
        {
            var state = _states[port];
            return state == PermanentlyExcluded ? 0 : state;
        }
    }

    internal bool IsPermanentlyExcluded(int port)
    {
        lock (_lock)
        {
            return _states[port] == PermanentlyExcluded;
        }
    }
}
