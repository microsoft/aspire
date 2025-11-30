using Microsoft.Net.Http.Headers;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// Get paths from command line arguments or configuration
// --contentroot: The content root of the Blazor WASM app (used to set WebRootPath)
// --staticwebassets: Path to the static web assets runtime manifest (.staticwebassets.runtime.json)
// --staticwebassetsendpoints: Path to the static web assets endpoints manifest (.staticwebassets.endpoints.json)
var contentRoot = builder.Configuration["contentroot"];
var staticWebAssetsManifest = builder.Configuration["staticwebassets"];

// Set the content root and web root from the Blazor WASM app
if (!string.IsNullOrEmpty(contentRoot))
{
    builder.Environment.ContentRootPath = contentRoot;
    builder.Environment.WebRootPath = Path.Combine(contentRoot, "wwwroot");
}

// Configure static web assets if manifest is provided
if (!string.IsNullOrEmpty(staticWebAssetsManifest) && File.Exists(staticWebAssetsManifest))
{
    builder.Configuration[WebHostDefaults.StaticWebAssetsKey] = staticWebAssetsManifest;
    builder.WebHost.UseStaticWebAssets();
}

var app = builder.Build();

app.MapDefaultEndpoints();

// UseBlazorFrameworkFiles() serves /_framework files and sets Blazor-Environment header
app.UseBlazorFrameworkFiles();

// Serve static files from wwwroot (like DevServer does)
app.UseStaticFiles(new StaticFileOptions
{
    // In development, serve everything, as there's no other way to configure it.
    // In production, developers are responsible for configuring their own production server
    ServeUnknownFileTypes = true,
});

app.UseRouting();

app.MapFallbackToFile("index.html", new StaticFileOptions
{
    OnPrepareResponse = fileContext =>
    {
        // Avoid caching index.html during development.
        // When hot reload is enabled, a middleware injects a hot reload script into the response HTML.
        fileContext.Context.Response.Headers[HeaderNames.CacheControl] = "no-store";
    }
});

app.Run();
