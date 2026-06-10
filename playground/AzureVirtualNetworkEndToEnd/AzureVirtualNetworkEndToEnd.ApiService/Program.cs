// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Azure.Storage.Blobs;
using Azure.Storage.Queues;
using Microsoft.Data.SqlClient;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.AddAzureBlobContainerClient("mycontainer");

builder.AddKeyedAzureQueue("myqueue");

builder.AddSqlServerClient("sqldb");

var app = builder.Build();

app.MapDefaultEndpoints();

app.MapGet("/", async (BlobContainerClient containerClient, [FromKeyedServices("myqueue")] QueueClient queue) =>
{
    var blobNames = new List<string>();
    var blobNameAndContent = Guid.NewGuid().ToString();

    await containerClient.UploadBlobAsync(blobNameAndContent, new BinaryData(blobNameAndContent));

    await ReadBlobsAsync(containerClient, blobNames);

    await queue.SendMessageAsync("Hello, world!");

    return blobNames;
});

app.MapGet("/sql", async (SqlConnection connection) =>
{
    await connection.OpenAsync();

    // Ensure the Items table exists
    await using var createCmd = connection.CreateCommand();
    createCmd.CommandText = """
        IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'Items')
        CREATE TABLE Items (Id INT IDENTITY(1,1) PRIMARY KEY, Name NVARCHAR(256) NOT NULL, CreatedAt DATETIME2 DEFAULT GETUTCDATE())
        """;
    await createCmd.ExecuteNonQueryAsync();

    // Insert a new item
    var itemName = $"Item-{Guid.NewGuid():N}";
    await using var insertCmd = connection.CreateCommand();
    insertCmd.CommandText = "INSERT INTO Items (Name) OUTPUT INSERTED.Id, INSERTED.Name, INSERTED.CreatedAt VALUES (@name)";
    insertCmd.Parameters.Add(new SqlParameter("@name", itemName));

    await using var reader = await insertCmd.ExecuteReaderAsync();
    if (await reader.ReadAsync())
    {
        return Results.Ok(new
        {
            Id = reader.GetInt32(0),
            Name = reader.GetString(1),
            CreatedAt = reader.GetDateTime(2)
        });
    }

    return Results.StatusCode(500);
});

app.Run();

static async Task ReadBlobsAsync(BlobContainerClient containerClient, List<string> output)
{
    output.Add(containerClient.Uri.ToString());
    var blobs = containerClient.GetBlobsAsync();
    await foreach (var blob in blobs)
    {
        output.Add(blob.Name);
    }
}
