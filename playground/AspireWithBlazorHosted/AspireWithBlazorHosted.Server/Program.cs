using AspireWithBlazorHosted.Components;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services.AddRazorComponents()
    .AddInteractiveWebAssemblyComponents();

// YARP for proxying service calls from the WASM client
builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"))
    .AddServiceDiscoveryDestinationResolver();

// Register the same named HttpClient used by the WASM client's Weather component.
// During prerendering the component runs on the server, so the server needs
// this registration to resolve "https+http://weatherapi" via service discovery.
builder.Services.AddHttpClient("weatherapi", client =>
{
    client.BaseAddress = new Uri("https+http://weatherapi");
});

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseWebAssemblyDebugging();
}
else
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseHttpsRedirection();

app.MapDefaultEndpoints();

// Serve the Blazor WASM client configuration endpoint.
// The Aspire host sets Client__ConfigEndpointPath and Client__ConfigResponse
// as environment variables; the server reads them and serves the JSON response.
var configEndpointPath = app.Configuration["Client:ConfigEndpointPath"];
var configResponse = app.Configuration["Client:ConfigResponse"];
if (!string.IsNullOrEmpty(configEndpointPath) && !string.IsNullOrEmpty(configResponse))
{
    app.MapGet(configEndpointPath, () => Results.Content(configResponse, "application/json"));
}

app.UseAntiforgery();

app.MapStaticAssets();
app.MapReverseProxy();

app.MapRazorComponents<App>()
    .AddInteractiveWebAssemblyRenderMode()
    .AddAdditionalAssemblies(typeof(AspireWithBlazorHosted.Client._Imports).Assembly);

app.Run();
