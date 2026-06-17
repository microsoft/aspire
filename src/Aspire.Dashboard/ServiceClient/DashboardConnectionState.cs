// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Dashboard.ServiceClient;

/// <summary>
/// Represents the connection state between the dashboard and the app host resource service.
/// </summary>
public enum DashboardConnectionState
{
    /// <summary>
    /// The dashboard is attempting to connect to the resource service.
    /// </summary>
    Connecting,

    /// <summary>
    /// The dashboard is connected to the resource service and receiving data.
    /// </summary>
    Connected,

    /// <summary>
    /// The dashboard has lost its connection to the resource service and is attempting to reconnect.
    /// </summary>
    Disconnected
}
