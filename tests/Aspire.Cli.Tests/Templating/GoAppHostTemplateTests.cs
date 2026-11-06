// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Tests.Templating;

public class GoAppHostTemplateTests
{
    [Fact]
    public void GoStarterTemplate_DeclaresFrontendHostingPackage()
    {
        var path = Path.Combine(GetGoStarterDirectory(), "aspire.config.json");

        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        var packages = document.RootElement
            .GetProperty("packages")
            .EnumerateObject()
            .Select(package => package.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["Aspire.Hosting.Go", "Aspire.Hosting.JavaScript"], packages);
    }

    [Fact]
    public void GoStarterTemplate_FrontendProxiesToTheApiResourceEndpointVariable()
    {
        var appHost = File.ReadAllText(Path.Combine(GetGoStarterDirectory(), "apphost.go"));
        Assert.Contains("AddGoApp(\"api\", \"./api\")", appHost, StringComparison.Ordinal);
        Assert.Contains("AddViteApp(\"frontend\", \"./frontend\"", appHost, StringComparison.Ordinal);

        var viteConfig = File.ReadAllText(Path.Combine(GetGoStarterDirectory(), "frontend", "vite.config.ts"));
        Assert.Contains("process.env.API_HTTPS || process.env.API_HTTP", viteConfig, StringComparison.Ordinal);
    }

    [Fact]
    public void GoStarterTemplate_ShipsResolvedApiDependencies()
    {
        var goSumPath = Path.Combine(GetGoStarterDirectory(), "api", "go.sum");

        Assert.True(File.Exists(goSumPath), $"Expected the Go starter API checksums at {goSumPath}");
        Assert.NotEmpty(File.ReadAllLines(goSumPath));
    }

    private static string GetGoStarterDirectory()
        => Path.Combine(GetRepoRoot(), "src", "Aspire.Cli", "Templating", "Templates", "go-starter");

    private static string GetRepoRoot()
        => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
}
