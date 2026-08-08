using System.Runtime.InteropServices;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();

var app = builder.Build();

app.MapGet("/", () => $"""
    👋🌍
    🏷️ Host: {Environment.MachineName}
    💻 OS: { RuntimeInformation.OSDescription }
    🪪 PID: {Environment.ProcessId}
    """);

app.Run();