// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using System.Net.Sockets;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Tests.Utils;
using Aspire.Hosting;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.Backchannel;

public class ExtensionBackchannelTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task ConnectAsync_WhenConnectionSetupIsCanceled_PropagatesCancellationToConcurrentWaiters()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var backchannel = CreateBackchannel(GetUnusedTcpEndpoint(), workspace.CreateExecutionContext());

        using var setupCancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        using var waiterCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var setupTask = backchannel.ConnectAsync(setupCancellation.Token);
        var waiterTask = backchannel.ConnectAsync(waiterCancellation.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => setupTask).DefaultTimeout();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiterTask).DefaultTimeout();
    }

    private static ExtensionBackchannel CreateBackchannel(string endpoint, CliExecutionContext executionContext)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [KnownConfigNames.ExtensionEndpoint] = endpoint,
                [KnownConfigNames.ExtensionToken] = "test-token"
            })
            .Build();

        return new ExtensionBackchannel(NullLogger<ExtensionBackchannel>.Instance, new ExtensionRpcTarget(configuration, executionContext), configuration);
    }

    private static string GetUnusedTcpEndpoint()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        return $"127.0.0.1:{port}";
    }
}
