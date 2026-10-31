// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using System.Net.Sockets;
using System.Text;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Aspire.Hosting.Azure.Kusto.Tests;

public class AzureKustoHealthCheckTests
{
    [Theory]
    [InlineData("http://localhost:1234", "http://127.0.0.1:1234", null)]
    [InlineData("http://LocalHost:1234;Initial Catalog=TestDb", "http://127.0.0.1:1234", "TestDb")]
    [InlineData("http://127.0.0.1:1234", "http://127.0.0.1:1234", null)]
    [InlineData("http://kusto.example:1234", "http://kusto.example:1234", null)]
    public void GetConnectionStringBuilder_ForEmulator_UsesIPv4LoopbackForLocalhost(string connectionString, string expectedDataSource, string? expectedDatabase)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var kusto = builder.AddAzureKustoCluster("kusto").RunAsEmulator();

        var kcsb = AzureKustoBuilderExtensions.GetConnectionStringBuilder(kusto.Resource, connectionString);

        Assert.Equal(expectedDataSource, kcsb.DataSource);
        if (expectedDatabase is not null)
        {
            Assert.Equal(expectedDatabase, kcsb.InitialCatalog);
        }
    }

    [Fact]
    public async Task EmulatorHealthCheck_ReachesEndpointThatOnlyListensOnIPv4Loopback()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var kusto = builder.AddAzureKustoCluster("kusto").RunAsEmulator();

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        // Hold [::1]:port bound without listening so IPv6 loopback connections are refused. This mirrors an emulator
        // endpoint that is only reachable over IPv4 even though "localhost" also resolves to ::1.
        using var ipv6Blocker = TryBindIPv6Loopback(port);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var acceptTask = AcceptAndRespondAsync(listener, cts.Token);

        var kcsb = AzureKustoBuilderExtensions.GetConnectionStringBuilder(kusto.Resource, $"http://localhost:{port}");
        var healthCheck = new AzureKustoHealthCheck(kcsb, isClusterCheck: true);

        var result = await healthCheck.CheckHealthAsync(new HealthCheckContext(), cts.Token);

        // The fake endpoint answers with 400, so the check is unhealthy, but the request must have reached it.
        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.True(acceptTask.IsCompletedSuccessfully, $"The health check never connected to the IPv4 loopback endpoint. Result: {result.Exception}");
        cts.Cancel();
    }

    private static Socket? TryBindIPv6Loopback(int port)
    {
        if (!Socket.OSSupportsIPv6)
        {
            return null;
        }

        var socket = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            socket.Bind(new IPEndPoint(IPAddress.IPv6Loopback, port));
            return socket;
        }
        catch (SocketException)
        {
            socket.Dispose();
            return null;
        }
    }

    private static async Task AcceptAndRespondAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        using var client = await listener.AcceptTcpClientAsync(cancellationToken);
        var stream = client.GetStream();

        var buffer = new byte[4096];
        var request = new StringBuilder();
        while (!request.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }
            request.Append(Encoding.ASCII.GetString(buffer, 0, read));
        }

        var response = "HTTP/1.1 400 Bad Request\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray();
        await stream.WriteAsync(response, cancellationToken);
    }
}
