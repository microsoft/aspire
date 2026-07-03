// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRECOMMAND001 // RequiredCommandAnnotation is for evaluation purposes only

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.JavaScript.Tests;

// Package-manager selection (WithNpm/WithBun/WithYarn/WithPnpm) is last-wins via
// JavaScriptPackageManagerAnnotation, and the integration-managed required commands are resolved in a BeforeStart
// hook (so a later selection fully replaces an earlier one). Run-script apps use the package manager as their
// runtime; direct-script apps preserve their fixed runtime and add the package manager for install. Without that,
// package-manager changes could surface false "missing required command" banners or hide real runtime
// prerequisites. See https://github.com/microsoft/aspire/issues/18625.
//
// Required commands are materialized on BeforeStartEvent, so every test builds the app and publishes that
// event (via GetRequiredCommandsAsync) before inspecting the resulting RequiredCommandAnnotations.
public class RequiredCommandTests
{
    [Fact]
    public async Task AddNodeApp_DefaultsToNode()
    {
        using var tempDir = new TestTempDirectory();
        using var builder = TestDistributedApplicationBuilder.Create();

        var app = builder.AddNodeApp("app", tempDir.Path, "server.js");

        Assert.Equal(["node"], await GetRequiredCommandsAsync(builder, app.Resource));
    }

    [Fact]
    public async Task AddNodeApp_WithBun_RequiresNodeAndBun()
    {
        using var tempDir = new TestTempDirectory();
        using var builder = TestDistributedApplicationBuilder.Create();

        // WithBun selects Bun for install, but a Node direct-script app still launches as `node server.js`.
        var app = builder.AddNodeApp("app", tempDir.Path, "server.js")
            .WithBun();

        Assert.Equal(["bun", "node"], await GetRequiredCommandsAsync(builder, app.Resource));
    }

    [Fact]
    public async Task AddNodeApp_WithNpm_RequiresNodeAndNpm()
    {
        using var tempDir = new TestTempDirectory();
        using var builder = TestDistributedApplicationBuilder.Create();

        var app = builder.AddNodeApp("app", tempDir.Path, "server.js")
            .WithNpm();

        Assert.Equal(["node", "npm"], await GetRequiredCommandsAsync(builder, app.Resource));
    }

    [Fact]
    public async Task AddNodeApp_WithRunScript_WithBun_RequiresOnlyBun()
    {
        using var tempDir = new TestTempDirectory();
        using var builder = TestDistributedApplicationBuilder.Create();

        // WithRunScript routes launch through the package manager, so Bun replaces the Node runtime.
        var app = builder.AddNodeApp("app", tempDir.Path, "server.js")
            .WithRunScript("start")
            .WithBun();

        Assert.Equal(["bun"], await GetRequiredCommandsAsync(builder, app.Resource));
    }

    [Fact]
    public async Task AddViteApp_DefaultsToNodeAndNpm()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var vite = builder.AddViteApp("vite", AppContext.BaseDirectory);

        Assert.Equal(["node", "npm"], await GetRequiredCommandsAsync(builder, vite.Resource));
    }

    [Fact]
    public async Task AddViteApp_WithBun_RequiresOnlyBun()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var vite = builder.AddViteApp("vite", AppContext.BaseDirectory)
            .WithBun();

        Assert.Equal(["bun"], await GetRequiredCommandsAsync(builder, vite.Resource));
    }

    [Fact]
    public async Task AddViteApp_WithNpm_RequiresNodeAndNpm()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var vite = builder.AddViteApp("vite", AppContext.BaseDirectory)
            .WithNpm();

        Assert.Equal(["node", "npm"], await GetRequiredCommandsAsync(builder, vite.Resource));
    }

    [Fact]
    public async Task AddViteApp_WithYarn_RequiresNodeAndYarn()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var vite = builder.AddViteApp("vite", AppContext.BaseDirectory)
            .WithYarn();

        Assert.Equal(["node", "yarn"], await GetRequiredCommandsAsync(builder, vite.Resource));
    }

    [Fact]
    public async Task AddViteApp_WithPnpm_RequiresNodeAndPnpm()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var vite = builder.AddViteApp("vite", AppContext.BaseDirectory)
            .WithPnpm();

        Assert.Equal(["node", "pnpm"], await GetRequiredCommandsAsync(builder, vite.Resource));
    }

    [Fact]
    public async Task AddJavaScriptApp_WithBun_RequiresOnlyBun()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var app = builder.AddJavaScriptApp("app", AppContext.BaseDirectory)
            .WithBun();

        Assert.Equal(["bun"], await GetRequiredCommandsAsync(builder, app.Resource));
    }

    [Fact]
    public async Task AddBunApp_RequiresOnlyBun()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var app = builder.AddBunApp("app", AppContext.BaseDirectory, "server.ts");

        Assert.Equal(["bun"], await GetRequiredCommandsAsync(builder, app.Resource));
    }

    [Fact]
    public async Task AddBunApp_WithPackageJson_RequiresOnlyBunWithoutDuplicates()
    {
        using var tempDir = new TestTempDirectory();
        using var builder = TestDistributedApplicationBuilder.Create();

        // A package.json in the app directory makes AddBunApp opt the resource into the Bun package
        // manager (via WithBun) in addition to the WithBunDefaults baseline. Since both resolve to "bun"
        // from the same package-manager annotation, the materialized set must still be a single "bun".
        File.WriteAllText(Path.Combine(tempDir.Path, "package.json"), "{}");

        var app = builder.AddBunApp("app", tempDir.Path, "server.ts");

        Assert.Equal(["bun"], await GetRequiredCommandsAsync(builder, app.Resource));
    }

    [Fact]
    public async Task AddBunApp_WithNpm_RequiresBunNodeAndNpm()
    {
        using var tempDir = new TestTempDirectory();
        using var builder = TestDistributedApplicationBuilder.Create();

        var app = builder.AddBunApp("app", tempDir.Path, "server.ts")
            .WithNpm();

        Assert.Equal(["bun", "node", "npm"], await GetRequiredCommandsAsync(builder, app.Resource));
    }

    [Fact]
    public async Task WithBun_ThenWithNpm_LastSelectionWins()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var vite = builder.AddViteApp("vite", AppContext.BaseDirectory)
            .WithBun()
            .WithNpm();

        Assert.Equal(["node", "npm"], await GetRequiredCommandsAsync(builder, vite.Resource));
    }

    [Fact]
    public async Task WithNpm_ThenWithBun_LastSelectionWins()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var vite = builder.AddViteApp("vite", AppContext.BaseDirectory)
            .WithNpm()
            .WithBun();

        Assert.Equal(["bun"], await GetRequiredCommandsAsync(builder, vite.Resource));
    }

    // Builds the app and publishes BeforeStartEvent so the integration's deferred required-command hook runs,
    // then returns the resource's required commands sorted for order-independent comparison.
    private static async Task<string[]> GetRequiredCommandsAsync(IDistributedApplicationBuilder builder, IResource resource)
    {
        using var app = builder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        await builder.Eventing.PublishAsync(new BeforeStartEvent(app.Services, appModel));

        return resource.Annotations
            .OfType<RequiredCommandAnnotation>()
            .Select(a => a.Command)
            .OrderBy(command => command, StringComparer.Ordinal)
            .ToArray();
    }
}
