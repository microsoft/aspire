using BoardData;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

// Database — reads connection string from DATABASE_URL env var
var dbConnStr = Environment.GetEnvironmentVariable("DATABASE_URL")
    ?? "Host=localhost;Port=5432;Database=boardapp;Username=postgres;Password=localdev123";
builder.Services.AddDbContext<BoardDbContext>(options => options.UseNpgsql(dbConnStr));

// Redis — reads from REDIS_URL env var
var redisUrl = Environment.GetEnvironmentVariable("REDIS_URL") ?? "localhost:6379";
builder.Services.AddSingleton<IConnectionMultiplexer>(ConnectionMultiplexer.Connect(redisUrl));

// External API key — used for third-party notification service
var externalApiKey = Environment.GetEnvironmentVariable("EXTERNAL_API_KEY")
    ?? throw new InvalidOperationException("EXTERNAL_API_KEY must be set");

builder.Services.AddCors(options =>
    options.AddDefaultPolicy(policy => policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

var app = builder.Build();
app.UseCors();

app.MapGet("/api/health", () => Results.Ok(new { status = "healthy" }));

app.MapGet("/api/items", async (BoardDbContext db) =>
    await db.BoardItems.OrderByDescending(i => i.CreatedAt).ToListAsync());

app.MapPost("/api/items", async (BoardDbContext db, BoardItem item) =>
{
    db.BoardItems.Add(item);
    await db.SaveChangesAsync();
    return Results.Created($"/api/items/{item.Id}", item);
});

app.MapGet("/api/items/{id}", async (BoardDbContext db, int id) =>
    await db.BoardItems.FindAsync(id) is { } item ? Results.Ok(item) : Results.NotFound());

app.MapGet("/api/cached-count", async (IConnectionMultiplexer redis) =>
{
    var db = redis.GetDatabase();
    var cached = await db.StringGetAsync("item-count");
    return Results.Ok(new { count = (string?)cached ?? "not cached" });
});

app.MapPost("/api/notify", (HttpContext ctx) =>
{
    // Stub: would call external notification service using EXTERNAL_API_KEY
    return Results.Ok(new { sent = true, provider = "external", keyPrefix = externalApiKey[..8] + "..." });
});

app.Run();
