var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();
app.MapGet("/", () => "Custom Debug Service (simulates Azure Functions / AWS Lambda)");
app.Run();
