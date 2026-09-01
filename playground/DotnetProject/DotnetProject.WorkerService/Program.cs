using DotnetProject.SharedLibrary;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// "apiservice" is resolved by service discovery (configured in ServiceDefaults) to the ApiService
// endpoint that the app host injects via WithReference.
builder.Services.AddHttpClient("apiservice", client => client.BaseAddress = new("http://apiservice"));

var app = builder.Build();

app.MapDefaultEndpoints();

app.MapGet("/", async (IHttpClientFactory factory) =>
{
    var client = factory.CreateClient("apiservice");
    var upstream = await client.GetStringAsync("/");
    return $"{Greeter.Greet("workerservice")}{Environment.NewLine}Upstream apiservice said: {upstream}";
});

app.Run();
