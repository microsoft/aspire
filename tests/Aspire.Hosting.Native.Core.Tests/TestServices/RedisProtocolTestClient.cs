// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Sockets;
using System.Text;
using Aspire.Hosting.Native.Client;

namespace Aspire.Hosting.Native.Core.Tests.TestServices;

/// <summary>Verifies actual Redis authentication before a contract test publishes dependency readiness.</summary>
internal static class RedisProtocolTestClient
{
    public static async Task<bool> PingAsync(WorkloadEndpointAllocation endpoint, string? password, CancellationToken cancellationToken)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(endpoint.Host, endpoint.Port, cancellationToken);
        await using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        if (password is not null)
        {
            // RESP2 command framing: *2\r\n$4\r\nAUTH\r\n$<UTF8 byte length>\r\n<password>\r\n.
            var authenticate = $"*2\r\n$4\r\nAUTH\r\n${Encoding.UTF8.GetByteCount(password)}\r\n{password}\r\n";
            await stream.WriteAsync(Encoding.UTF8.GetBytes(authenticate), cancellationToken);
            if (await reader.ReadLineAsync(cancellationToken) != "+OK")
            {
                return false;
            }
        }
        await stream.WriteAsync("*1\r\n$4\r\nPING\r\n"u8.ToArray(), cancellationToken);

        return await reader.ReadLineAsync(cancellationToken) == "+PONG";
    }

    public static async Task WaitForAuthenticatedPingAsync(WorkloadEndpointAllocation endpoint, string password,
        TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        while (true)
        {
            try
            {
                if (await PingAsync(endpoint, password, cancellationToken))
                {
                    return;
                }
            }
            catch (Exception exception) when (exception is SocketException or IOException)
            {
            }
            await Task.Delay(TimeSpan.FromMilliseconds(100), timeProvider, cancellationToken);
        }
    }
}
