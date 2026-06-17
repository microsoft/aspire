var builder = WebApplication.CreateBuilder(args);

var app = builder.Build();

app.MapGet("/", () => "Hello from an Aspire backend deployed to Azure App Service.");

app.MapGet("/api/message", () => new
{
    Message = "Hello from the App Service backend.",
    ComputeEnvironment = "Azure App Service",
    Time = DateTimeOffset.UtcNow
});

app.Run();
