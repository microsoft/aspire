var port = Environment.GetEnvironmentVariable("PORT") ?? "0";
var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls($"http://localhost:{port}");
var app = builder.Build();

app.MapGet("/", () => "Hello from FakeIntegrationLibrary");

app.Run();
