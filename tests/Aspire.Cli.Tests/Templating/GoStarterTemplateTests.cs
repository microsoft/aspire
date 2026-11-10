// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.Cli.Templating;

namespace Aspire.Cli.Tests.Templating;

public class GoStarterTemplateTests
{
    [Fact]
    public async Task GoStarterAppHost_MatchesTheScaffoldedSource()
    {
        await Verify(File.ReadAllText(GetGoStarterAppHostPath()), extension: "go");
    }

    [Fact]
    public void GoStarterTemplate_DeclaresHostingPackagesForTheApiAndFrontend()
    {
        var path = Path.Combine(GetGoStarterDirectory(), "aspire.config.json");

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var packages = document.RootElement
            .GetProperty("packages")
            .EnumerateObject()
            .Select(package => package.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["Aspire.Hosting.Go", "Aspire.Hosting.JavaScript"], packages);
    }

    [Fact]
    public void GoStarterTemplate_FrontendReferencesAndWaitsForTheApi()
    {
        var appHost = File.ReadAllText(GetGoStarterAppHostPath());
        Assert.Contains("builder.AddGoApp(\"api\", \"./api\")", appHost, StringComparison.Ordinal);
        Assert.Contains("builder.AddViteApp(\"frontend\", \"./frontend\", nil)", appHost, StringComparison.Ordinal);
        Assert.Contains("WithReference(api).", appHost, StringComparison.Ordinal);
        Assert.Contains("WaitFor(api)", appHost, StringComparison.Ordinal);
        Assert.Contains("WithHttpEndpoint(aspire.HttpEndpointEnv(\"PORT\"))", appHost, StringComparison.Ordinal);
        Assert.Contains("WithHttpHealthCheck(aspire.HttpHealthCheckPath(\"/health\"))", appHost, StringComparison.Ordinal);
        Assert.DoesNotContain(".Err()", appHost, StringComparison.Ordinal);
        Assert.Contains("api.PublishWithContainerFiles(frontend, \"./static\")", appHost, StringComparison.Ordinal);

        var viteConfig = File.ReadAllText(Path.Combine(GetGoStarterDirectory(), "frontend", "vite.config.ts"));
        Assert.Contains("process.env.API_HTTPS || process.env.API_HTTP", viteConfig, StringComparison.Ordinal);

        var api = File.ReadAllText(Path.Combine(GetGoStarterDirectory(), "api", "main.go"));
        Assert.Contains("mux.Handle(\"/\", http.FileServer(http.Dir(\"./static\")))", api, StringComparison.Ordinal);
        Assert.Contains("os.Getenv(\"CACHE_URI\")", api, StringComparison.Ordinal);
        Assert.DoesNotContain("ConnectionStrings__cache", api, StringComparison.Ordinal);
        Assert.DoesNotContain("parseAspireRedisConnString", api, StringComparison.Ordinal);
        Assert.Contains("redisotel.InstrumentTracing(cache)", api, StringComparison.Ordinal);
        Assert.Contains("redisotel.InstrumentMetrics(cache)", api, StringComparison.Ordinal);
        Assert.Contains("runtime.GOOS == \"windows\"", api, StringComparison.Ordinal);
        Assert.Contains("host = \"127.0.0.1\"", api, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void GoStarterTemplate_IncludesRedisApiOnlyWhenSelected(bool useRedis)
    {
        var conditions = new Dictionary<string, bool>
        {
            ["redis"] = useRedis,
            ["no-redis"] = !useRedis,
        };
        var apiDirectory = Path.Combine(GetGoStarterDirectory(), "api");
        var main = ConditionalBlockProcessor.Process(File.ReadAllText(Path.Combine(apiDirectory, "main.go")), conditions);
        var goMod = ConditionalBlockProcessor.Process(File.ReadAllText(Path.Combine(apiDirectory, "go.mod")), conditions);
        var goSum = ConditionalBlockProcessor.Process(File.ReadAllText(Path.Combine(apiDirectory, "go.sum")), conditions);

        Assert.Equal(useRedis, main.Contains("redisotel.InstrumentTracing(cache)", StringComparison.Ordinal));
        Assert.Equal(useRedis, main.Contains("os.Getenv(\"CACHE_URI\")", StringComparison.Ordinal));
        Assert.Equal(useRedis, goMod.Contains("github.com/redis/go-redis/extra/redisotel/v9", StringComparison.Ordinal));
        Assert.Equal(useRedis, goSum.Contains("github.com/redis/go-redis/extra/redisotel/v9", StringComparison.Ordinal));
    }

    [Fact]
    public void GoStarterTemplate_ShipsBuildableFrontendDependencies()
    {
        var frontendDirectory = Path.Combine(GetGoStarterDirectory(), "frontend");

        Assert.True(File.Exists(Path.Combine(frontendDirectory, "package.json")));
        Assert.True(File.Exists(Path.Combine(frontendDirectory, "package-lock.json")));
        Assert.True(File.Exists(Path.Combine(frontendDirectory, "src", "App.tsx")));
        Assert.True(File.Exists(Path.Combine(frontendDirectory, "vite.config.ts")));

        var logo = File.ReadAllBytes(Path.Combine(frontendDirectory, "public", "Aspire.png"));
        Assert.Equal([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], logo[..8]);
    }

    [Fact]
    public void GoStarterTemplate_BridgesStandardLogsToOpenTelemetry()
    {
        var apiDirectory = Path.Combine(GetGoStarterDirectory(), "api");

        var goMod = File.ReadAllText(Path.Combine(apiDirectory, "go.mod"));
        Assert.Contains("go.opentelemetry.io/contrib/bridges/otelslog v0.18.0", goMod, StringComparison.Ordinal);

        var telemetry = File.ReadAllText(Path.Combine(apiDirectory, "telemetry.go"));
        Assert.Contains("otelslog.NewHandler(serviceName, otelslog.WithLoggerProvider(lp))", telemetry, StringComparison.Ordinal);
        Assert.Contains("log.SetOutput(io.MultiWriter(os.Stderr", telemetry, StringComparison.Ordinal);
        Assert.Contains("res := resource.Default()", telemetry, StringComparison.Ordinal);
        Assert.DoesNotContain("semconv.ServiceName", telemetry, StringComparison.Ordinal);
    }

    private static string GetGoStarterAppHostPath()
    {
        var path = Path.Combine(GetGoStarterDirectory(), "apphost.go");
        Assert.True(File.Exists(path), $"Expected the go-starter AppHost at {path}");

        return path;
    }

    private static string GetGoStarterDirectory()
        => Path.Combine(GetRepoRoot(), "src", "Aspire.Cli", "Templating", "Templates", "go-starter");

    private static string GetRepoRoot()
        => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
}
