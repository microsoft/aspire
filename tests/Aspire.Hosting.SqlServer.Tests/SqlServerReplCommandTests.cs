// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Tests;
using Aspire.Hosting.Utils;
using Aspire.TestUtilities;

#pragma warning disable ASPIRETERMINAL001

namespace Aspire.Hosting.SqlServer.Tests;

public class SqlServerReplCommandTests(ITestOutputHelper outputHelper) : ContainerReplCommandTestBase
{
    protected override IResourceBuilder<ContainerResource> AddContainer(IDistributedApplicationBuilder builder, bool enableRepl)
    {
        var resource = builder.AddSqlServer("sqlserver");
        if (enableRepl)
        {
            Assert.Same(resource, resource.WithRepl());
        }

        return resource;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WithReplRejectsNullBuilder(bool configure)
    {
        var exception = Assert.Throws<ArgumentNullException>(() => configure
            ? SqlServerBuilderExtensions.WithRepl(null!, options => options.Command = SqlServerReplCommand.Version17)
            : SqlServerBuilderExtensions.WithRepl(null!));

        Assert.Equal("builder", exception.ParamName);
    }

    [Fact]
    public void ReplDefaultsToToolsIncludedInDefaultImage()
    {
        var options = new SqlServerReplOptions();

        Assert.Equal("/opt/mssql-tools/bin/sqlcmd", SqlServerReplCommand.Version17);
        Assert.Equal("/opt/mssql-tools18/bin/sqlcmd", SqlServerReplCommand.Version18);
        Assert.Equal(SqlServerReplCommand.Version18, SqlServerContainerImageTags.ReplCommand);
        Assert.Equal(SqlServerContainerImageTags.ReplCommand, options.Command);
    }

    [Theory]
    [InlineData(DistributedApplicationOperation.Run, 1)]
    [InlineData(DistributedApplicationOperation.Publish, 0)]
    public void WithReplConfiguresOptionsOnceInRunMode(DistributedApplicationOperation operation, int expectedCalls)
    {
        using var builder = TestDistributedApplicationBuilder.Create(operation);
        var sqlServer = builder.AddSqlServer("sqlserver");
        var calls = 0;

        var result = sqlServer.WithRepl(options =>
        {
            calls++;
            Assert.Equal(SqlServerReplCommand.Version18, options.Command);
            options.Command = SqlServerReplCommand.Version17;
        });

        Assert.Same(sqlServer, result);
        Assert.Equal(expectedCalls, calls);
        Assert.Equal(expectedCalls, sqlServer.Resource.Annotations.OfType<ResourceCommandAnnotation>().Count());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t")]
    public void WithReplRejectsMissingCommand(string? command)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var sqlServer = builder.AddSqlServer("sqlserver");

        var exception = Assert.ThrowsAny<ArgumentException>(() =>
            sqlServer.WithRepl(options => options.Command = command!));

        Assert.Equal("Command", exception.ParamName);
        Assert.Empty(sqlServer.Resource.Annotations.OfType<ResourceCommandAnnotation>());
    }

    [Theory]
    [InlineData(SqlServerReplCommand.Version17, false, 1433)]
    [InlineData(SqlServerReplCommand.Version17, true, 1434)]
    [InlineData(SqlServerReplCommand.Version18, false, 1433)]
    [InlineData(SqlServerReplCommand.Version18, true, 1434)]
    [InlineData("/custom tools/sqlcmd", false, 1433)]
    [InlineData("/custom tools/sqlcmd", true, 1434)]
    public async Task ReplUsesSelectedClientAndCurrentPasswordAndLoopbackPort(string executablePath, bool replacePassword, int targetPort)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var password = builder.AddParameter("password", "quotes'\" $; spaces", secret: true);
        var sqlServer = builder.AddSqlServer("sqlserver", password: password, port: 15433)
            .WithEndpoint("tcp", endpoint => endpoint.TargetPort = targetPort);
        if (replacePassword)
        {
            sqlServer.WithPassword(builder.AddParameter("replacement", "replacement'\" $; spaces", secret: true));
        }

        var replOptions = new SqlServerReplOptions { Command = executablePath };
        var options = await SqlServerBuilderExtensions.CreateReplOptionsAsync(sqlServer.Resource, replOptions.Command, TestContext.Current.CancellationToken);
        var execOptions = ContainerReplCommand.CreateExecOptions(options, "docker", "container-id");

        Assert.Equal("sqlcmd (sqlserver)", execOptions.Title);
        Assert.Equal(["exec", "-it", "--env", "SQLCMDPASSWORD", "container-id", executablePath,
            "-S", $"127.0.0.1,{targetPort}", "-U", "sa", "-d", "master", "-C"], execOptions.Arguments);
        Assert.Collection(execOptions.EnvironmentVariables, variable =>
        {
            Assert.Equal("SQLCMDPASSWORD", variable.Key);
            Assert.Equal(replacePassword ? "replacement'\" $; spaces" : "quotes'\" $; spaces", variable.Value);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [RequiresFeature(TestFeature.ContainerRuntime)]
    public async Task ReplExecutesAuthenticatedBatch(bool configure)
    {
        using var builder = TestDistributedApplicationBuilder.Create(outputHelper);
        var password = builder.AddParameter("password", "Repl-p@ss$word1", secret: true);
        var sqlServer = builder.AddSqlServer("sqlserver", password: password);
        if (configure)
        {
            SqlServerReplOptions? capturedOptions = null;
            sqlServer.WithRepl(options =>
            {
                options.Command = SqlServerReplCommand.Version18;
                capturedOptions = options;
            });
            Assert.NotNull(capturedOptions);
            // A retained options instance must not change the already configured launch.
            capturedOptions.Command = SqlServerReplCommand.Version17;
        }
        else
        {
            sqlServer.WithRepl();
        }
        await using var app = builder.Build();

        await VerifyReplAsync(app, sqlServer.Resource, "sqlcmd (sqlserver)",
            [("1>", "SELECT 'authenticated-' + SUSER_SNAME() + '-' + DB_NAME();\r"), ("2>", "GO\r")],
            "authenticated-sa-master");
    }
}
