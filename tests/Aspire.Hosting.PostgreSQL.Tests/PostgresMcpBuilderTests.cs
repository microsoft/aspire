// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Postgres;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Aspire.Hosting.ApplicationModel;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.PostgreSQL.Tests;

[Experimental("ASPIREPOSTGRES001")]
public class PostgresMcpBuilderTests
{
    [Fact]
    public async Task WithPostgresMcpOnDatabaseAddsContainerResourceWithMcpEndpointAnnotation()
    {
        using var appBuilder = TestDistributedApplicationBuilder.Create();

        appBuilder.AddPostgres("postgres")
            .AddDatabase("db")
            .WithPostgresMcp();

        using var app = await appBuilder.BuildAsync();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var mcpContainer = Assert.Single(appModel.Resources.OfType<PostgresMcpContainerResource>());
        Assert.Equal("db-mcp", mcpContainer.Name);

        var endpoint = Assert.Single(mcpContainer.Annotations.OfType<EndpointAnnotation>());
        Assert.Equal(PostgresMcpContainerResource.PrimaryEndpointName, endpoint.Name);
        Assert.Equal("http", endpoint.UriScheme);
        Assert.Equal(8000, endpoint.TargetPort);

        // Verify MCP annotation exists
        var mcpAnnotation = Assert.Single(mcpContainer.Annotations.OfType<McpServerEndpointAnnotation>());
        Assert.NotNull(mcpAnnotation.EndpointUrlResolver);
    }

    [Fact]
    public async Task WithPostgresMcpOnDatabaseResolvesEndpointUrl()
    {
        using var appBuilder = TestDistributedApplicationBuilder.Create();

        var postgres = appBuilder.AddPostgres("postgres");
        postgres.WithEndpoint("tcp", e => e.AllocatedEndpoint = new AllocatedEndpoint(e, "localhost", 5432));

        postgres.AddDatabase("db")
            .WithPostgresMcp();

        using var app = await appBuilder.BuildAsync();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var mcpContainer = Assert.Single(appModel.Resources.OfType<PostgresMcpContainerResource>());

        // Allocate the MCP container's endpoint so GetValueAsync can resolve
        var mcpEndpoint = Assert.Single(mcpContainer.Annotations.OfType<EndpointAnnotation>());
        mcpEndpoint.AllocatedEndpoint = new AllocatedEndpoint(mcpEndpoint, "db-mcp.dev.internal", 8000);

        var mcpAnnotation = Assert.Single(mcpContainer.Annotations.OfType<McpServerEndpointAnnotation>());

        var resolvedUri = await mcpAnnotation.EndpointUrlResolver(mcpContainer, CancellationToken.None);

        Assert.NotNull(resolvedUri);
        Assert.Equal("http://db-mcp.dev.internal:8000/sse", resolvedUri!.ToString());
    }

    [Fact]
    public async Task WithPostgresMcpOnDatabaseSetsDatabaseUriEnvironmentVariable()
    {
        using var appBuilder = TestDistributedApplicationBuilder.Create();

        var pass = appBuilder.AddParameter("pass", "p@ssw0rd1");

        appBuilder.AddPostgres("postgres", password: pass)
            .WithEndpoint("tcp", e =>
            {
                e.AllocatedEndpoint = new AllocatedEndpoint(e, "localhost", 5432);
                e.AllAllocatedEndpoints.AddOrUpdateAllocatedEndpoint(KnownNetworkIdentifiers.DefaultAspireContainerNetwork, new AllocatedEndpoint(e, "postgres.dev.internal", 5432, EndpointBindingMode.SingleAddress, targetPortExpression: null, networkID: KnownNetworkIdentifiers.DefaultAspireContainerNetwork));
            })
            .AddDatabase("db")
            .WithPostgresMcp();

        using var app = await appBuilder.BuildAsync();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var database = Assert.Single(appModel.Resources.OfType<PostgresDatabaseResource>());
        var mcpContainer = Assert.Single(appModel.Resources.OfType<PostgresMcpContainerResource>());

        var env = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(mcpContainer, DistributedApplicationOperation.Run, TestServiceProvider.Instance);

        var databaseUri = Assert.Single(env, e => e.Key == "DATABASE_URI");
        Assert.Equal("postgresql://postgres:p%40ssw0rd1@postgres.dev.internal:5432/db", databaseUri.Value);
    }

    [Fact]
    public async Task WithPostgresMcpOnDatabaseCanBeCalledMultipleTimesWithoutDuplicateResources()
    {
        using var appBuilder = TestDistributedApplicationBuilder.Create();

        var db = appBuilder.AddPostgres("postgres")
            .AddDatabase("db");

        db.WithPostgresMcp();
        db.WithPostgresMcp();

        using var app = await appBuilder.BuildAsync();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        Assert.Single(appModel.Resources.OfType<PostgresMcpContainerResource>());
    }

    [Fact]
    public async Task WithPostgresMcpWithCustomNameCreatesDistinctResourcesPerName()
    {
        using var appBuilder = TestDistributedApplicationBuilder.Create();

        var db = appBuilder.AddPostgres("postgres")
            .AddDatabase("db");

        db.WithPostgresMcp(containerName: "postgres-mcp-a");
        db.WithPostgresMcp(containerName: "postgres-mcp-b");

        using var app = await appBuilder.BuildAsync();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        Assert.Equal(2, appModel.Resources.OfType<PostgresMcpContainerResource>().Count());
    }
}
