using DotnetProject.SharedLibrary;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

var app = builder.Build();

app.MapDefaultEndpoints();

app.MapGet("/", () => Greeter.Greet("apiservice"));

app.Run();
