// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Radius.Tests.TestHosts;

/// <summary>
/// App host containing all mapped resource types from ResourceTypeMapper:
/// Redis, SQL Server + database, RabbitMQ, MongoDB, Postgres (manual provisioning),
/// two container instances, and one project resource.
/// Dapr resources are excluded since the Dapr hosting package is not available.
/// </summary>
internal static class MultiResourceAppHost
{
    public static void Configure(IDistributedApplicationBuilder builder)
    {
        builder.AddRadiusEnvironment();

        // Data resources
        builder.AddRedis("cache");
        builder.AddSqlServer("sqlserver").AddDatabase("sqldb");
        builder.AddMongoDB("mongo");
        builder.AddPostgres("postgres");

        // Messaging resources
        builder.AddRabbitMQ("rabbitmq");

        // Compute resources
        builder.AddContainer("frontend", "myapp/frontend:latest");
        builder.AddContainer("backend", "myapp/backend:latest");
    }
}
