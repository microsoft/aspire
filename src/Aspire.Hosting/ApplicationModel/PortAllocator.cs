// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Allocates and manages a range of ports for use in an application context.
/// </summary>
/// <remarks>
/// This class starts allocating ports from a specified initial port (default is 8000)
/// and ensures that allocated ports do not overlap with ports already marked as used.
/// </remarks>
public sealed class PortAllocator(int startPort = 8000) : IPortAllocator
{
    private int _allocatedPortStart = startPort;
    private readonly HashSet<int> _usedPorts = [];

    /// <inheritdoc/>
    public int AllocatePort()
    {
        while (true)
        {
            if (!_usedPorts.Contains(_allocatedPortStart))
            {
                return _allocatedPortStart;
            }

            _allocatedPortStart++;
        }
    }

    /// <inheritdoc/>
    public void AddUsedPort(int port)
    {
        _usedPorts.Add(port);
    }
}
