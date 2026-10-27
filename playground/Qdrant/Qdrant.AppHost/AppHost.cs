// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

var builder = DistributedApplication.CreateBuilder(args);

var qdrant = builder.AddQdrant("qdrant")
    .WithDataVolume("qdrant-data");

builder.AddDotnetProject("apiservice", @"..\Qdrant.ApiService\Qdrant.ApiService.csproj")
    .WithExternalHttpEndpoints()
    .WithReference(qdrant)
    .WaitFor(qdrant);

builder.Build().Run();
