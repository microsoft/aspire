// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Tests;
using Aspire.Hosting.Utils;
using Aspire.TestUtilities;

#pragma warning disable ASPIRETERMINAL001

namespace Aspire.Hosting.PostgreSQL.Tests;

public class PostgresReplCommandTests(ITestOutputHelper outputHelper) : ContainerReplCommandTestBase
{
    protected override IResourceBuilder<ContainerResource> AddContainer(IDistributedApplicationBuilder builder) =>
        builder.AddPostgres("postgres");

    [Theory]
    [InlineData(null, "postgres")]
    [InlineData("user with spaces", "user with spaces")]
    public async Task ReplUsesConfiguredCredentials(string? username, string expectedUsername)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var user = username is null ? null : builder.AddParameter("username", username);
        var password = builder.AddParameter("password", "quotes'\" $; spaces", secret: true);
        var postgres = builder.AddPostgres("postgres", userName: user, password: password);

        var options = await PostgresBuilderExtensions.CreateReplOptionsAsync(postgres.Resource, TestContext.Current.CancellationToken);
        var execOptions = ContainerReplCommand.CreateExecOptions(options, "docker", "container-id");

        Assert.Equal("psql (postgres)", execOptions.Title);
        Assert.Equal(["exec", "-it", "--env", "PGPASSWORD", "container-id", "psql",
            "--username", expectedUsername, "--dbname", "postgres", "--no-password"], execOptions.Arguments);
        Assert.Collection(execOptions.EnvironmentVariables, variable =>
        {
            Assert.Equal("PGPASSWORD", variable.Key);
            Assert.Equal("quotes'\" $; spaces", variable.Value);
        });
    }

    [Fact]
    [RequiresFeature(TestFeature.ContainerRuntime)]
    public async Task ReplExecutesAuthenticatedQuery()
    {
        using var builder = TestDistributedApplicationBuilder.CreateWithTestContainerRegistry(outputHelper);
        var username = builder.AddParameter("username", "repl-user");
        var password = builder.AddParameter("password", "repl-p@ss$word", secret: true);
        var postgres = builder.AddPostgres("postgres", userName: username, password: password);
        await using var app = builder.Build();

        await VerifyReplAsync(app, postgres.Resource, "psql (postgres)",
            "postgres=#", "SELECT 'authenticated-' || current_user;\r", "authenticated-repl-user");
    }
}
