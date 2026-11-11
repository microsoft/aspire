#:sdk Microsoft.NET.Sdk.Web
#:property PublishAot=false

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapGet("/", () => "Aspire CLI testing");
app.MapGet("/health", () => Results.Ok());

app.Run();
