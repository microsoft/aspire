// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Bson;
using MongoDB.Driver;

var builder = DistributedApplication.CreateBuilder(args);

var db = builder.AddMongoDB("mongo")
    .WithMongoExpress(c => c.WithHostPort(3022))
    .AddDatabase("db")
    .OnResourceReady(async (db, @event, ct) =>{
        // Artificial delay to demonstrate the waiting
        await Task.Delay(TimeSpan.FromSeconds(10), ct);

        // Seed the database with some data
        //var cs = await db.Resource.ConnectionStringExpression.GetValueAsync(ct);
        var cs = await db.ConnectionStringExpression.GetValueAsync(ct);
        using var client = new MongoClient(cs);

        const string collectionName = "entries";

        var myDb = client.GetDatabase("db");
        await myDb.CreateCollectionAsync(collectionName, cancellationToken: ct);

        for (int i = 0; i < 10; i++)
        {
            await myDb.GetCollection<Entry>(collectionName).InsertOneAsync(new Entry(), cancellationToken: ct);
        }
    });

builder.AddProject<Projects.Mongo_ApiService>("api")
       .WithExternalHttpEndpoints()
       .WithReference(db)
       .WaitFor(db);

#if !SKIP_DASHBOARD_REFERENCE
// This project is only added in playground projects to support development/debugging
// of the dashboard. It is not required in end developer code. Comment out this code
// or build with `/p:SkipDashboardReference=true`, to test end developer
// dashboard launch experience, Refer to Directory.Build.props for the path to
// the dashboard binary (defaults to the Aspire.Dashboard bin output in the
// artifacts dir).
builder.AddProject<Projects.Aspire_Dashboard>(KnownResourceNames.AspireDashboard);
#endif

builder.Build().Run();

public sealed class Entry
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? Id { get; set; }
}
