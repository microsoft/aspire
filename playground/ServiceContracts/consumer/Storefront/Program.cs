var builder = WebApplication.CreateBuilder(args);
var catalogUrl = builder.Configuration["services:catalog:http:0"]
    ?? throw new InvalidOperationException("The catalog HTTP endpoint reference is required.");
var apiKey = builder.Configuration["CATALOG_API_KEY"]
    ?? throw new InvalidOperationException("CATALOG_API_KEY is required.");
builder.Services.AddHttpClient("catalog", client =>
{
    client.BaseAddress = new Uri(catalogUrl);
    client.DefaultRequestHeaders.Add("X-Catalog-Key", apiKey);
});
var app = builder.Build();

app.MapGet("/shop", async (IHttpClientFactory clients, CancellationToken cancellationToken) =>
{
    using var response = await clients.CreateClient("catalog").GetAsync("/products", cancellationToken);
    response.EnsureSuccessStatusCode();
    return Results.Content(await response.Content.ReadAsStringAsync(cancellationToken), "application/json");
});

app.Run();
