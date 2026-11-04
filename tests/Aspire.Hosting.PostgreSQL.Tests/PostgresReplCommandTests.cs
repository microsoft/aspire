// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Tests;
using Aspire.Hosting.Utils;
using Aspire.TestUtilities;

#pragma warning disable ASPIRETERMINAL001

namespace Aspire.Hosting.PostgreSQL.Tests;

public class PostgresReplCommandTests(ITestOutputHelper outputHelper) : ContainerReplCommandTestBase
{
    protected override IResourceBuilder<ContainerResource> AddContainer(IDistributedApplicationBuilder builder, bool enableRepl)
    {
        var resource = builder.AddPostgres("postgres");
        if (enableRepl)
        {
            Assert.Same(resource, resource.WithRepl());
        }

        return resource;
    }

    [Fact]
    public void WithReplRejectsNullBuilder()
    {
        var exception = Assert.Throws<ArgumentNullException>(() => PostgresBuilderExtensions.WithRepl(null!));

        Assert.Equal("builder", exception.ParamName);
    }

    [Theory]
    [InlineData(null, "postgres", 5432)]
    [InlineData(null, "postgres", 5433)]
    [InlineData("user with spaces", "user with spaces", 5432)]
    [InlineData("user with spaces", "user with spaces", 5433)]
    public async Task ReplUsesConfiguredCredentialsAndTargetPort(string? username, string expectedUsername, int port)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var user = username is null ? null : builder.AddParameter("username", username);
        var password = builder.AddParameter("password", "quotes'\" $; spaces", secret: true);
        var postgres = builder.AddPostgres("postgres", userName: user, password: password)
            .WithEndpoint("tcp", endpoint => endpoint.TargetPort = port);

        var options = await PostgresBuilderExtensions.CreateReplOptionsAsync(postgres.Resource, TestContext.Current.CancellationToken);
        var execOptions = ContainerReplCommand.CreateExecOptions(options, "docker", "container-id");

        Assert.Equal("psql (postgres)", execOptions.Title);
        Assert.Equal(["exec", "-it", "--env", "PGPASSWORD", "container-id", "psql",
            "--username", expectedUsername, "--dbname", "postgres", "--no-password",
            "--port", port.ToString(CultureInfo.InvariantCulture)], execOptions.Arguments);
        Assert.Collection(execOptions.EnvironmentVariables, variable =>
        {
            Assert.Equal("PGPASSWORD", variable.Key);
            Assert.Equal("quotes'\" $; spaces", variable.Value);
        });
    }

    [Fact]
    public async Task ReplRejectsMissingTargetPort()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var postgres = builder.AddPostgres("postgres")
            .WithEndpoint("tcp", endpoint => endpoint.TargetPort = null);

        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(() =>
            PostgresBuilderExtensions.CreateReplOptionsAsync(postgres.Resource, TestContext.Current.CancellationToken));

        Assert.Equal("The PostgreSQL REPL port is not available.", exception.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(5433)]
    [RequiresFeature(TestFeature.ContainerRuntime)]
    public async Task ReplExecutesAuthenticatedQuery(int? targetPort)
    {
        using var builder = TestDistributedApplicationBuilder.CreateWithTestContainerRegistry(outputHelper);
        var username = builder.AddParameter("username", "repl-user");
        var password = builder.AddParameter("password", "repl-p@ss$word", secret: true);
        var postgres = builder.AddPostgres("postgres", userName: username, password: password).WithRepl();
        if (targetPort is { } port)
        {
            postgres.WithArgs("-p", port.ToString(CultureInfo.InvariantCulture))
                .WithEndpoint("tcp", endpoint => endpoint.TargetPort = port);
        }

        await using var app = builder.Build();

        await VerifyReplAsync(app, postgres.Resource, "psql (postgres)",
            "postgres=#", "SELECT 'authenticated-' || current_user;\r", "authenticated-repl-user");
    }
}
