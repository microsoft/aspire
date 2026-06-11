// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

var builder = DistributedApplication.CreateBuilder(args);

var api = builder.AddProject<Projects.AzureDataLakeEndToEnd_WebApi>("api");

builder.AddAzureContainerAppEnvironment("aca-env");

var storage = builder.AddAzureStorage("azure-storage");
var dataLake = storage.AddDataLake("data-lake");
var fileSystem = storage.AddDataLakeFileSystem("data-lake-file-system");

api.WithReference(dataLake).WaitFor(dataLake).WithReference(fileSystem).WaitFor(fileSystem);

builder.Build().Run();
