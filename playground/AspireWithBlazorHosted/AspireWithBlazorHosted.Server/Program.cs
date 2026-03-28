using AspireWithBlazorHosted.Components;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services.AddRazorComponents()
    .AddInteractiveWebAssemblyComponents();

// YARP for proxying service calls from the WASM client
builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"))
    .AddServiceDiscoveryDestinationResolver();

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

app.UseAntiforgery();

app.UseStaticFiles();
app.MapReverseProxy();

// Configuration endpoint — return the pre-built JSON from the hosting layer
app.MapGet(
    app.Configuration["Client:ConfigEndpointPath"] ?? "/_blazor/_configuration",
    (IConfiguration config) => Results.Content(config["Client:ConfigResponse"]!, "application/json"));

app.MapRazorComponents<App>()
    .AddInteractiveWebAssemblyRenderMode()
    .AddAdditionalAssemblies(typeof(AspireWithBlazorHosted.Client._Imports).Assembly);

app.Run();
