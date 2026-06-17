var builder = WebApplication.CreateBuilder(args);

var app = builder.Build();

app.MapGet("/", () => "Hello from an Aspire project in an Azure sandbox.");

app.MapGet("/environment", () => new
{
    Value = Environment.GetEnvironmentVariable("SANDBOX_PROJECT_VALUE")
});

app.Run();
