// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Tests;
using Aspire.Hosting.Utils;
using Aspire.TestUtilities;

#pragma warning disable ASPIRETERMINAL001

namespace Aspire.Hosting.MySql.Tests;

public class MySqlReplCommandTests(ITestOutputHelper outputHelper) : ContainerReplCommandTestBase
{
    protected override IResourceBuilder<ContainerResource> AddContainer(IDistributedApplicationBuilder builder) =>
        builder.AddMySql("mysql");

    [Theory]
    [InlineData("repl-password", 3306)]
    [InlineData("repl-password", 3307)]
    [InlineData("quotes'\" \\ $; `command` $(command) # spaces\r\n\t", 3306)]
    [InlineData("quotes'\" \\ $; `command` $(command) # spaces\r\n\t", 3307)]
    public async Task ReplUsesConfiguredPasswordAndTargetPort(string passwordValue, int port)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var password = builder.AddParameter("password", passwordValue, secret: true);
        var mysql = builder.AddMySql("mysql", password: password)
            .WithEndpoint("tcp", endpoint => endpoint.TargetPort = port);

        var options = await MySqlBuilderExtensions.CreateReplOptionsAsync(mysql.Resource, TestContext.Current.CancellationToken);
        var execOptions = ContainerReplCommand.CreateExecOptions(options, "docker", "container-id");

        Assert.Equal("mysql (mysql)", execOptions.Title);
        Assert.Equal(["exec", "-it", "--env", "MYSQL_PWD", "container-id", "mysql",
            "--no-defaults", "--no-login-paths", "--user=root", "--host=127.0.0.1",
            $"--port={port.ToString(CultureInfo.InvariantCulture)}"], execOptions.Arguments);
        Assert.Collection(execOptions.EnvironmentVariables, variable =>
        {
            Assert.Equal("MYSQL_PWD", variable.Key);
            Assert.Equal(passwordValue, variable.Value);
        });
    }

    [Fact]
    public async Task ReplRejectsMissingTargetPort()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var mysql = builder.AddMySql("mysql")
            .WithEndpoint("tcp", endpoint => endpoint.TargetPort = null);

        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(() =>
            MySqlBuilderExtensions.CreateReplOptionsAsync(mysql.Resource, TestContext.Current.CancellationToken));

        Assert.Equal("The MySQL REPL port is not available.", exception.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(3307)]
    [RequiresFeature(TestFeature.ContainerRuntime)]
    public async Task ReplExecutesAuthenticatedQuery(int? targetPort)
    {
        using var builder = TestDistributedApplicationBuilder.CreateWithTestContainerRegistry(outputHelper);
        var password = builder.AddParameter("password", "repl-p@ss$word", secret: true);
        var mysql = builder.AddMySql("mysql", password: password);
        if (targetPort is { } port)
        {
            mysql.WithArgs($"--port={port.ToString(CultureInfo.InvariantCulture)}")
                .WithEndpoint("tcp", endpoint => endpoint.TargetPort = port);
        }

        await using var app = builder.Build();

        await VerifyReplAsync(app, mysql.Resource, "mysql (mysql)",
            "mysql>", "SELECT CONCAT('authenticated-', CURRENT_USER());\r", "authenticated-root@");
    }
}
