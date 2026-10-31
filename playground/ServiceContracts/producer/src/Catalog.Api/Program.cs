var builder = WebApplication.CreateBuilder(args);
var region = builder.Configuration["CATALOG_REGION"]
    ?? throw new InvalidOperationException("CATALOG_REGION is required.");
var apiKey = builder.Configuration["CATALOG_API_KEY"]
    ?? throw new InvalidOperationException("CATALOG_API_KEY is required.");
var app = builder.Build();

app.MapGet("/health", () => Results.Ok());
app.MapGet("/products", (HttpRequest request) =>
    request.Headers["X-Catalog-Key"] == apiKey
        ? Results.Ok(new { service = "catalog", contractVersion = 1, region, products = new[] { "tea", "coffee" } })
        : Results.Unauthorized());

app.Run();
