// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Tests;
using Aspire.Hosting.Utils;
using Aspire.TestUtilities;

#pragma warning disable ASPIRETERMINAL001

namespace Aspire.Hosting.Valkey.Tests;

public class ValkeyReplCommandTests(ITestOutputHelper outputHelper) : ContainerReplCommandTestBase
{
    protected override IResourceBuilder<ContainerResource> AddContainer(IDistributedApplicationBuilder builder, bool enableRepl)
    {
        var resource = builder.AddValkey("valkey");
        if (enableRepl)
        {
            Assert.Same(resource, resource.WithRepl());
        }

        return resource;
    }

    [Fact]
    public void WithReplRejectsNullBuilder()
    {
        var exception = Assert.Throws<ArgumentNullException>(() => ValkeyBuilderExtensions.WithRepl(null!));

        Assert.Equal("builder", exception.ParamName);
    }

    [Theory]
    [InlineData(6379)]
    [InlineData(6380)]
    public async Task ReplUsesConfiguredPasswordAndTargetPort(int port)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var password = builder.AddParameter("password", "quotes'\" $; spaces", secret: true);
        var valkey = builder.AddValkey("valkey", password: password)
            .WithEndpoint("tcp", endpoint => endpoint.TargetPort = port);

        var options = await ValkeyBuilderExtensions.CreateReplOptionsAsync(valkey.Resource, TestContext.Current.CancellationToken);
        var execOptions = ContainerReplCommand.CreateExecOptions(options, "docker", "container-id");

        Assert.Equal("valkey-cli (valkey)", execOptions.Title);
        Assert.Equal(["exec", "-it", "--env", "VALKEYCLI_AUTH", "container-id", "valkey-cli",
            "-h", "127.0.0.1", "-p", port.ToString(CultureInfo.InvariantCulture)], execOptions.Arguments);
        Assert.Collection(execOptions.EnvironmentVariables, variable =>
        {
            Assert.Equal("VALKEYCLI_AUTH", variable.Key);
            Assert.Equal("quotes'\" $; spaces", variable.Value);
        });
    }

    [Fact]
    public async Task ReplSupportsPasswordlessValkey()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var valkey = builder.AddResource(new ValkeyResource("valkey"))
            .WithEndpoint(targetPort: 6379, name: "tcp");

        var options = await ValkeyBuilderExtensions.CreateReplOptionsAsync(valkey.Resource, TestContext.Current.CancellationToken);
        var execOptions = ContainerReplCommand.CreateExecOptions(options, "docker", "container-id");

        Assert.Equal(["exec", "-it", "container-id", "valkey-cli", "-h", "127.0.0.1", "-p", "6379"], execOptions.Arguments);
        Assert.Empty(execOptions.EnvironmentVariables);
    }

    [Fact]
    [RequiresFeature(TestFeature.ContainerRuntime)]
    public async Task ReplExecutesAuthenticatedCommand()
    {
        using var builder = TestDistributedApplicationBuilder.CreateWithTestContainerRegistry(outputHelper);
        var password = builder.AddParameter("password", "repl-password", secret: true);
        var valkey = builder.AddValkey("valkey", password: password).WithRepl();
        await using var app = builder.Build();

        await VerifyReplAsync(app, valkey.Resource, "valkey-cli (valkey)", "127.0.0.1:", "PING\r", "PONG");
    }
}
