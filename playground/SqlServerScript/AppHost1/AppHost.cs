// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting;

var builder = DistributedApplication.CreateBuilder(args);

builder.AddAzureContainerAppEnvironment("env");

var dbServer = builder.AddAzureSqlServer("mysqlserver");

var todosDb = dbServer.AddDatabase("todosdb");

builder.AddDotnetProject("api1", @"..\WebApplication1\WebApplication1.csproj")
    .WithExternalHttpEndpoints()
    .WithReference(todosDb).WaitFor(todosDb);

builder.AddDotnetProject("api2", @"..\WebApplication2\WebApplication2.csproj")
    .WithExternalHttpEndpoints()
    .WithReference(todosDb).WaitFor(todosDb);

builder.Build().Run();
