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

// What the watcher built and how it launched this process (build configuration, build env, working directory, replica).
app.MapGet("/diag", () =>
{
    var assembly = typeof(Program).Assembly;
    string? Env(string name) => Environment.GetEnvironmentVariable(name);
    return Results.Json(new
    {
        pid = Environment.ProcessId,
        replica = Env("OTEL_SERVICE_INSTANCE_ID"),
        workingDirectory = Environment.CurrentDirectory,
        assemblyConfiguration = assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyConfigurationAttribute), false)
            .OfType<System.Reflection.AssemblyConfigurationAttribute>().FirstOrDefault()?.Configuration,
        buildMarker = assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
            .OfType<System.Reflection.AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "BuildMarker")?.Value,
        tempPath = Path.GetTempPath(),
        env = new
        {
            TMPDIR = Env("TMPDIR"),
            Configuration = Env("Configuration"),
            WATCHDEMO_BUILD_MARKER = Env("WATCHDEMO_BUILD_MARKER"),
            GREETING_SOURCE = Env("GREETING_SOURCE"),
        },
    });
});

// Exits with code 3 to try the crash flow: the resource shows Crashed; the next save (or Restart) relaunches it.
app.MapGet("/crash", () => { Environment.Exit(3); return ""; });

app.Run();
