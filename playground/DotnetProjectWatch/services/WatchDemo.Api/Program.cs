using WatchDemo.Shared;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.Logger.LogInformation("api started (GREETING_SOURCE={Source})", Environment.GetEnvironmentVariable("GREETING_SOURCE"));

app.MapGet("/", () => $"api v1 | {Greeter.Message}");

app.Run();
