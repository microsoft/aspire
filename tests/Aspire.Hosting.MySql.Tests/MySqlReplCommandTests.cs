// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

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
    [InlineData("repl-password")]
    [InlineData("quotes'\" \\ $; `command` $(command) # spaces\r\n\t")]
    public async Task ReplUsesConfiguredPassword(string passwordValue)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var password = builder.AddParameter("password", passwordValue, secret: true);
        var mysql = builder.AddMySql("mysql", password: password);

        var options = await MySqlBuilderExtensions.CreateReplOptionsAsync(mysql.Resource, TestContext.Current.CancellationToken);
        var execOptions = ContainerReplCommand.CreateExecOptions(options, "docker", "container-id");

        Assert.Equal("mysql (mysql)", execOptions.Title);
        Assert.Equal(["exec", "-it", "--env", "MYSQL_PWD", "container-id", "mysql",
            "--no-defaults", "--no-login-paths", "--user=root", "--host=127.0.0.1", "--port=3306"], execOptions.Arguments);
        Assert.Collection(execOptions.EnvironmentVariables, variable =>
        {
            Assert.Equal("MYSQL_PWD", variable.Key);
            Assert.Equal(passwordValue, variable.Value);
        });
    }

    [Fact]
    [RequiresFeature(TestFeature.ContainerRuntime)]
    public async Task ReplExecutesAuthenticatedQuery()
    {
        using var builder = TestDistributedApplicationBuilder.CreateWithTestContainerRegistry(outputHelper);
        var password = builder.AddParameter("password", "repl-p@ss$word", secret: true);
        var mysql = builder.AddMySql("mysql", password: password);
        await using var app = builder.Build();

        await VerifyReplAsync(app, mysql.Resource, "mysql (mysql)",
            "mysql>", "SELECT CONCAT('authenticated-', CURRENT_USER());\r", "authenticated-root@");
    }
}
