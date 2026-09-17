using Qdrant.Client;
using Qdrant.Client.Grpc;

var builder = WebApplication.CreateBuilder(args);

// Add service defaults & Aspire client integrations.
builder.AddServiceDefaults();

// Add services to the container.
builder.Services.AddProblemDetails();

builder.AddQdrantClient("qdrant");

var app = builder.Build();

// Configure the HTTP request pipeline.
app.UseExceptionHandler();

app.MapGet("/create", async (QdrantClient client, ILogger<Program> logger) =>
{
    var collections = await client.ListCollectionsAsync();
    if (collections.Any(x => x.Contains("movie_collection")))
    {
        await client.DeleteCollectionAsync("movie_collection");
    }

    await client.CreateCollectionAsync("movie_collection", new VectorParams { Size = 2, Distance = Distance.Cosine });
    var collectionInfo = await client.GetCollectionInfoAsync("movie_collection");
    logger.LogInformation(collectionInfo.ToString());

    // generate some vectors
    var data = new[]
    {
        new PointStruct
        {
            Id = 1,
            Vectors = new [] {0.10022575f, -0.23998135f},
            Payload =
            {
                ["title"] = "The Lion King"
            }
        },
        new PointStruct
        {
            Id = 2,
            Vectors = new [] {0.10327095f, 0.2563685f},
            Payload =
            {
                ["title"] = "Inception"
            }
        },
        new PointStruct
        {
            Id = 3,
            Vectors = new [] {0.095857024f, -0.201278f},
            Payload =
            {
                ["title"] = "Toy Story"
            }
        },
        new PointStruct
        {
            Id = 4,
            Vectors = new [] {0.106827796f, 0.21676421f},
            Payload =
            {
                ["title"] = "Pulp Function"
            }
        },
        new PointStruct
        {
            Id = 5,
            Vectors = new [] {0.09568083f, -0.21177962f},
            Payload =
            {
                ["title"] = "Shrek"
            }
        },
    };
    var updateResult = await client.UpsertAsync("movie_collection", data);

    return updateResult.Status;
});

app.MapGet("/search", async (QdrantClient client) =>
{
    var results = await client.QueryAsync("movie_collection", new[] { 0.12217915f, -0.034832448f }, limit: 3);
    return results.Select(titles => titles.Payload["title"].StringValue);
});

app.MapDefaultEndpoints();

app.Run();
