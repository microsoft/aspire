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

    [Fact]
    public void WithReplRejectsNullBuilder()
    {
        var exception = Assert.Throws<ArgumentNullException>(() => SqlServerBuilderExtensions.WithRepl(null!));

        Assert.Equal("builder", exception.ParamName);
    }

    [Theory]
    [InlineData(false, 1433)]
    [InlineData(true, 1434)]
    public async Task ReplUsesCurrentPasswordAndLoopbackPort(bool replacePassword, int targetPort)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var password = builder.AddParameter("password", "quotes'\" $; spaces", secret: true);
        var sqlServer = builder.AddSqlServer("sqlserver", password: password, port: 15433)
            .WithEndpoint("tcp", endpoint => endpoint.TargetPort = targetPort);
        if (replacePassword)
        {
            sqlServer.WithPassword(builder.AddParameter("replacement", "replacement'\" $; spaces", secret: true));
        }

        var options = await SqlServerBuilderExtensions.CreateReplOptionsAsync(sqlServer.Resource, TestContext.Current.CancellationToken);
        var execOptions = ContainerReplCommand.CreateExecOptions(options, "docker", "container-id");

        const string selectSqlCmd = """
            if [ -x /opt/mssql-tools18/bin/sqlcmd ]; then
                exec /opt/mssql-tools18/bin/sqlcmd "$@"
            elif [ -x /opt/mssql-tools/bin/sqlcmd ]; then
                exec /opt/mssql-tools/bin/sqlcmd "$@"
            else
                echo 'The SQL Server REPL requires sqlcmd in /opt/mssql-tools18/bin or /opt/mssql-tools/bin.' >&2
                exit 127
            fi
            """;
        Assert.Equal("sqlcmd (sqlserver)", execOptions.Title);
        Assert.Equal(["exec", "-it", "--env", "SQLCMDPASSWORD", "container-id", "/bin/sh",
            "-c", selectSqlCmd, "sqlcmd", "-S", $"127.0.0.1,{targetPort}", "-U", "sa", "-d", "master", "-C"], execOptions.Arguments);
        Assert.Collection(execOptions.EnvironmentVariables, variable =>
        {
            Assert.Equal("SQLCMDPASSWORD", variable.Key);
            Assert.Equal(replacePassword ? "replacement'\" $; spaces" : "quotes'\" $; spaces", variable.Value);
        });
    }

    [Fact]
    [RequiresFeature(TestFeature.ContainerRuntime)]
    public async Task ReplExecutesAuthenticatedBatch()
    {
        using var builder = TestDistributedApplicationBuilder.Create(outputHelper);
        var password = builder.AddParameter("password", "Repl-p@ss$word1", secret: true);
        var sqlServer = builder.AddSqlServer("sqlserver", password: password).WithRepl();
        await using var app = builder.Build();

        await VerifyReplAsync(app, sqlServer.Resource, "sqlcmd (sqlserver)",
            [("1>", "SELECT 'authenticated-' + SUSER_SNAME() + '-' + DB_NAME();\r"), ("2>", "GO\r")],
            "authenticated-sa-master");
    }
}
