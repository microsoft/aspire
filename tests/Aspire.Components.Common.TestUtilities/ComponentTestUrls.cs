// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using System.Net.Sockets;

namespace Aspire.Components.Common.TestUtilities;

public static class ComponentTestUrls
{
    public static Uri CreateUnavailableHttpUri()
        => new($"http://127.0.0.1:{GetUnusedTcpPort()}");

    public static Uri CreateUnavailableHttpsUri()
        => new($"https://127.0.0.1:{GetUnusedTcpPort()}");

    public static int GetUnusedTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
