// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Azure.Storage.Files.DataLake;
using Microsoft.AspNetCore.Mvc;

var builder = WebApplication.CreateBuilder(args);
builder.AddAzureDataLakeServiceClient("data-lake");
builder.AddAzureDataLakeFileSystemClient("data-lake-file-system");

var app = builder.Build();

app.MapGet("/creat-file-system", async ([FromQuery] string fileSystemName, [FromServices] DataLakeServiceClient dataLakeServiceClient) =>
{
    var result = await dataLakeServiceClient.CreateFileSystemAsync(fileSystemName);
    return TypedResults.Text($"{result.Value.Uri} created.");
});

app.MapGet("/creat-directory", async ([FromQuery] string directory, [FromServices] DataLakeFileSystemClient dataLakeFileSystemClient) =>
{
    var result = await dataLakeFileSystemClient.CreateDirectoryAsync(directory);
    return TypedResults.Text($"{result.Value.Uri} created.");
});

app.Run();
