using WatchDemo.Shared;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddRazorPages();
var app = builder.Build();

app.Logger.LogInformation("api started (GREETING_SOURCE={Source})", Environment.GetEnvironmentVariable("GREETING_SOURCE"));

// Serves wwwroot/hello.html; static file edits are picked up without a rebuild.
app.UseStaticFiles();

// Razor page at /hello: markup and @code edits are hot reloaded.
app.MapRazorPages();

app.MapGet("/", () => $"api v1 | {Greeter.Message}");

// Reads appsettings.json on every request: edit Demo:Greeting and the running app picks it up (no rebuild).
app.MapGet("/config", (IConfiguration config) => config["Demo:Greeting"] ?? "(not set)");

// Exits with code 3 to try the crash flow: the resource shows Crashed; the next save (or Restart) relaunches it.
app.MapGet("/crash", () => { Environment.Exit(3); return ""; });

app.Run();
