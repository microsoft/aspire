var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHttpClient("functions", client =>
{
    client.BaseAddress = new Uri("https+http://azure-functions-service");
});
builder.AddServiceDefaults();

var app = builder.Build();

app.MapGet("/", () => "Standard Service");

app.MapGet("/call-functions", async (IHttpClientFactory httpClientFactory) =>
{
    var client = httpClientFactory.CreateClient("functions");
    var response = await client.GetStringAsync("/api/hello");
    return $"Functions response: {response}";
});

app.Run();
