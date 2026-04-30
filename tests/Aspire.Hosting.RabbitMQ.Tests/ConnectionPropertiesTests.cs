// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.RabbitMQ.Tests;

public class ConnectionPropertiesTests
{
    [Fact]
    public void RabbitMqServerResourceGetConnectionPropertiesReturnsExpectedValues()
    {
        var user = new ParameterResource("user", _ => "guest");
        var password = new ParameterResource("password", _ => "p@ssw0rd1", secret: true);
        var resource = new RabbitMQServerResource("rabbit", user, password);

        var properties = ((IResourceWithConnectionString)resource).GetConnectionProperties().ToArray();

        Assert.Collection(
            properties,
            property =>
            {
                Assert.Equal("Host", property.Key);
                Assert.Equal("{rabbit.bindings.tcp.host}", property.Value.ValueExpression);
            },
            property =>
            {
                Assert.Equal("Port", property.Key);
                Assert.Equal("{rabbit.bindings.tcp.port}", property.Value.ValueExpression);
            },
            property =>
            {
                Assert.Equal("Username", property.Key);
                Assert.Equal("{user.value}", property.Value.ValueExpression);
            },
            property =>
            {
                Assert.Equal("Password", property.Key);
                Assert.Equal("{password.value}", property.Value.ValueExpression);
            },
            property =>
            {
                Assert.Equal("Uri", property.Key);
                Assert.Equal("amqp://{user.value}:{password.value}@{rabbit.bindings.tcp.host}:{rabbit.bindings.tcp.port}", property.Value.ValueExpression);
            });
    }

    [Fact]
    public async Task VerifyManifestWithConnectionProperties()
    {
        var builder = DistributedApplication.CreateBuilder();

        var server = builder.AddRabbitMQ("server");

        var serverWithParameters = builder.AddRabbitMQ(
            "serverWithParameters",
            userName: builder.AddParameter("username", "some#User"),
            password: builder.AddParameter("password", "p@ssw0rd!", secret: true));

        var vhost = server.AddVirtualHost("myvhost");
        var queue = vhost.AddQueue("myqueue");
        var exchange = vhost.AddExchange("myexchange");

        // Force connection properties to be generated
        var app = builder.AddExecutable("app", "command", ".")
            .WithReference(server)
            .WithReference(serverWithParameters)
            .WithReference(vhost)
            .WithReference(queue)
            .WithReference(exchange);

        var manifest = await ManifestUtils.GetManifest(app.Resource);

        await Verify(manifest.ToString(), "json");
    }
}