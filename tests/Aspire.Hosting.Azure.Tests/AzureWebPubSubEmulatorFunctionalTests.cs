// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.WebSockets;
using System.Text;
using Aspire.Hosting.Utils;
using Aspire.TestUtilities;
using Azure;
using Azure.Core;
using Azure.Messaging.WebPubSub;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Aspire.Hosting.Azure.Tests;

public class AzureWebPubSubEmulatorFunctionalTests(ITestOutputHelper testOutputHelper)
{
    [Fact]
    [RequiresFeature(TestFeature.ContainerRuntime)]
    public async Task RunAsEmulatorSupportsWebPubSubServiceClient()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var webPubSub = builder.AddAzureWebPubSub("webpubsub").RunAsEmulator();
        var hub = webPubSub.AddHub("chat-resource", "chat");
        await using var app = builder.Build();

        using var startCts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await app.StartAsync(startCts.Token);
        using var readyCts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await app.ResourceNotifications.WaitForResourceHealthyAsync(webPubSub.Resource.Name, readyCts.Token);

        using var messagingCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var clientBuilder = Host.CreateApplicationBuilder();
        clientBuilder.Configuration["ConnectionStrings:chat-resource"] = await hub.Resource.ConnectionStringExpression.GetValueAsync(messagingCts.Token);
        clientBuilder.AddAzureWebPubSubServiceClient("chat-resource");
        using var clientHost = clientBuilder.Build();
        var client = clientHost.Services.GetRequiredService<WebPubSubServiceClient>();
        Assert.Equal("chat", client.Hub);

        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(client.GetClientAccessUri(), messagingCts.Token);
        using var content = RequestContent.Create(BinaryData.FromString("Hello from Aspire"));
        await client.SendToAllAsync(content, ContentType.TextPlain, context: new RequestContext { CancellationToken = messagingCts.Token });

        var buffer = new byte[1024];
        var result = await socket.ReceiveAsync(buffer, messagingCts.Token);
        Assert.Equal(WebSocketMessageType.Text, result.MessageType);
        Assert.True(result.EndOfMessage);
        Assert.Equal("Hello from Aspire", Encoding.UTF8.GetString(buffer, 0, result.Count));
    }
}
