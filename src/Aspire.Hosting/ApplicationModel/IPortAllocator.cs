// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Provides port allocation functionality for resources.
/// </summary>
public interface IPortAllocator
{
    /// <summary>
    /// Allocates a port that is not currently in use.
    /// </summary>
    /// <returns>An available port number.</returns>
    int AllocatePort();

    /// <summary>
    /// Marks a port as used to prevent it from being allocated.
    /// </summary>
    /// <param name="port">The port number to mark as used.</param>
    void AddUsedPort(int port);
}
