// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREJAVASCRIPT001

using System.Runtime.CompilerServices;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.JavaScript.Tests;

public class WorkspaceValidationTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task PublishFlagsMemberNameTypo()
    {
        using var tempDir = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: tempDir.Path).WithResourceCleanUp(true);

        var root = CreatePnpmWorkspace(tempDir.Path);

        var workspace = builder.AddPnpmWorkspace("ws", root);
        // 'web-typo' is not a declared member (the member package.json declares "web").
        var app = workspace.AddJavaScriptApp("web", "web-typo", "packages/web").WithBuildScript("build");

        var ex = await Assert.ThrowsAsync<DistributedApplicationException>(() => ManifestUtils.GetManifest(app.Resource, tempDir.Path));

        Assert.Contains("web-typo", ex.Message);
        Assert.Contains("not a declared workspace member", ex.Message);
        // The message lists the declared member names so the user can fix the typo.
        Assert.Contains("web", ex.Message);
    }

    [Fact]
    public async Task PublishFlagsPackageManagerLockfileMismatch()
    {
        using var tempDir = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: tempDir.Path).WithResourceCleanUp(true);

        // A pnpm workspace whose only lockfile is yarn.lock — the configured PM (pnpm) disagrees.
        var root = Path.Combine(tempDir.Path, "ws");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "package.json"), """{ "name": "root", "workspaces": ["packages/*"] }""");
        File.WriteAllText(Path.Combine(root, "pnpm-workspace.yaml"), "packages:\n  - \"packages/*\"\n");
        File.WriteAllText(Path.Combine(root, "yarn.lock"), string.Empty);
        var webDir = Path.Combine(root, "packages", "web");
        Directory.CreateDirectory(webDir);
        File.WriteAllText(Path.Combine(webDir, "package.json"), """{ "name": "web", "scripts": { "build": "vite build" } }""");

        var workspace = builder.AddPnpmWorkspace("ws", root);
        var app = workspace.AddJavaScriptApp("web", "web", "packages/web").WithBuildScript("build");

        var ex = await Assert.ThrowsAsync<DistributedApplicationException>(() => ManifestUtils.GetManifest(app.Resource, tempDir.Path));

        Assert.Contains("yarn lockfile", ex.Message);
        Assert.Contains("pnpm", ex.Message);
    }

    [Fact]
    public async Task PublishToleratesExtraLockfileWhenConfiguredManagerMatches()
    {
        using var tempDir = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: tempDir.Path).WithResourceCleanUp(true);

        // A pnpm workspace whose root also carries a stray yarn.lock. The configured PM (pnpm) has its own
        // matching lockfile, so the extra one is tolerated: pnpm ignores yarn.lock at install time.
        var root = Path.Combine(tempDir.Path, "ws");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "package.json"), """{ "name": "root" }""");
        File.WriteAllText(Path.Combine(root, "pnpm-workspace.yaml"), "packages:\n  - \"packages/*\"\n");
        File.WriteAllText(Path.Combine(root, "pnpm-lock.yaml"), string.Empty);
        File.WriteAllText(Path.Combine(root, "yarn.lock"), string.Empty);
        var webDir = Path.Combine(root, "packages", "web");
        Directory.CreateDirectory(webDir);
        File.WriteAllText(Path.Combine(webDir, "package.json"), """{ "name": "web", "scripts": { "build": "vite build" } }""");

        var workspace = builder.AddPnpmWorkspace("ws", root);
        var app = workspace.AddJavaScriptApp("web", "web", "packages/web").WithBuildScript("build");

        // Should not throw — validation passes because the configured pnpm lockfile is present.
        var manifest = await ManifestUtils.GetManifest(app.Resource, tempDir.Path);
        Assert.NotNull(manifest);
    }

    [Fact]
    public async Task PublishFlagsLockfilesWhenNoneMatchConfiguredManager()
    {
        using var tempDir = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: tempDir.Path).WithResourceCleanUp(true);

        // A pnpm workspace whose only lockfiles are yarn.lock + bun.lock — neither matches the configured
        // pnpm manager, so a --frozen-lockfile install would have no valid lockfile to honor.
        var root = Path.Combine(tempDir.Path, "ws");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "package.json"), """{ "name": "root" }""");
        File.WriteAllText(Path.Combine(root, "pnpm-workspace.yaml"), "packages:\n  - \"packages/*\"\n");
        File.WriteAllText(Path.Combine(root, "yarn.lock"), string.Empty);
        File.WriteAllText(Path.Combine(root, "bun.lock"), string.Empty);
        var webDir = Path.Combine(root, "packages", "web");
        Directory.CreateDirectory(webDir);
        File.WriteAllText(Path.Combine(webDir, "package.json"), """{ "name": "web", "scripts": { "build": "vite build" } }""");

        var workspace = builder.AddPnpmWorkspace("ws", root);
        var app = workspace.AddJavaScriptApp("web", "web", "packages/web").WithBuildScript("build");

        var ex = await Assert.ThrowsAsync<DistributedApplicationException>(() => ManifestUtils.GetManifest(app.Resource, tempDir.Path));

        Assert.Contains("none match the configured package manager", ex.Message);
        Assert.Contains("pnpm", ex.Message);
    }

    [Fact]
    public async Task PublishFlagsMissingScript()
    {
        using var tempDir = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: tempDir.Path).WithResourceCleanUp(true);

        // The member declares only a "build" script, but the app references "compile".
        var root = Path.Combine(tempDir.Path, "ws");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "package.json"), """{ "name": "root" }""");
        File.WriteAllText(Path.Combine(root, "pnpm-workspace.yaml"), "packages:\n  - \"packages/*\"\n");
        File.WriteAllText(Path.Combine(root, "pnpm-lock.yaml"), string.Empty);
        var webDir = Path.Combine(root, "packages", "web");
        Directory.CreateDirectory(webDir);
        File.WriteAllText(Path.Combine(webDir, "package.json"), """{ "name": "web", "scripts": { "build": "vite build" } }""");

        var workspace = builder.AddPnpmWorkspace("ws", root);
        var app = workspace.AddJavaScriptApp("web", "web", "packages/web").WithBuildScript("compile");

        var ex = await Assert.ThrowsAsync<DistributedApplicationException>(() => ManifestUtils.GetManifest(app.Resource, tempDir.Path));

        Assert.Contains("compile", ex.Message);
        Assert.Contains("does not declare", ex.Message);
    }

    [Fact]
    public async Task PublishAggregatesMultipleProblems()
    {
        using var tempDir = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: tempDir.Path).WithResourceCleanUp(true);

        // Two problems at once: a yarn lockfile under a pnpm workspace AND a typo'd member name.
        var root = Path.Combine(tempDir.Path, "ws");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "package.json"), """{ "name": "root" }""");
        File.WriteAllText(Path.Combine(root, "pnpm-workspace.yaml"), "packages:\n  - \"packages/*\"\n");
        File.WriteAllText(Path.Combine(root, "yarn.lock"), string.Empty);
        var webDir = Path.Combine(root, "packages", "web");
        Directory.CreateDirectory(webDir);
        File.WriteAllText(Path.Combine(webDir, "package.json"), """{ "name": "web", "scripts": { "build": "vite build" } }""");

        var workspace = builder.AddPnpmWorkspace("ws", root);
        var app = workspace.AddJavaScriptApp("web", "web-typo", "packages/web").WithBuildScript("build");

        var ex = await Assert.ThrowsAsync<DistributedApplicationException>(() => ManifestUtils.GetManifest(app.Resource, tempDir.Path));

        // The workspace root is a per-run temp directory; replace it with a stable token so the
        // aggregated-message snapshot is deterministic across machines/runs.
        var scrubbed = ex.Message.Replace(workspace.Resource.WorkingDirectory, "{WorkspaceRoot}");
        await Verify(scrubbed);
    }

    [Fact]
    public async Task RunModeFlagsMemberNameTypoAtBeforeStart()
    {
        using var tempDir = TemporaryWorkspace.Create(outputHelper);
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);

        var root = CreatePnpmWorkspace(tempDir.Path);

        var workspace = builder.AddPnpmWorkspace("ws", root);
        workspace.AddJavaScriptApp("web", "web-typo", "packages/web").WithBuildScript("build");

        using var app = builder.Build();

        var ex = await Assert.ThrowsAsync<DistributedApplicationException>(() => ExecuteBeforeStartHooksAsync(app, CancellationToken.None));

        Assert.Contains("web-typo", ex.Message);
        Assert.Contains("not a declared workspace member", ex.Message);
    }

    [Theory]
    [InlineData("npm")]
    [InlineData("yarn")]
    [InlineData("pnpm")]
    [InlineData("bun")]
    public async Task RunModeConfiguresInstallerWithoutLockfile(string packageManager)
    {
        using var tempDir = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        var root = Path.Combine(tempDir.Path, "ws");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "package.json"), """{ "name": "root", "workspaces": ["packages/*"] }""");
        if (packageManager == "pnpm")
        {
            File.WriteAllText(Path.Combine(root, "pnpm-workspace.yaml"), "packages:\n  - \"packages/*\"\n");
        }
        var memberDir = Path.Combine(root, "packages", "web");
        Directory.CreateDirectory(memberDir);
        File.WriteAllText(Path.Combine(memberDir, "package.json"), """{ "name": "web", "scripts": { "dev": "vite" } }""");

        IResourceBuilder<JavaScriptWorkspaceResource> workspace = packageManager switch
        {
            "npm" => builder.AddNpmWorkspace("ws", root),
            "yarn" => builder.AddYarnWorkspace("ws", root),
            "pnpm" => builder.AddPnpmWorkspace("ws", root),
            "bun" => builder.AddBunWorkspace("ws", root),
            _ => throw new ArgumentOutOfRangeException(nameof(packageManager))
        };
        workspace.AddJavaScriptApp("web", "web", "packages/web");
        using var app = builder.Build();

        await ExecuteBeforeStartHooksAsync(app, CancellationToken.None);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var installer = Assert.Single(model.Resources.OfType<JavaScriptInstallerResource>());
        Assert.Equal(packageManager, installer.Command);
        Assert.Equal(root, installer.WorkingDirectory);
        Assert.Equal(["install"], await ArgumentEvaluator.GetArgumentListAsync(installer));
    }

    [Fact]
    public async Task PublishStillRequiresLockfile()
    {
        using var tempDir = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: tempDir.Path).WithResourceCleanUp(true);
        var root = CreatePnpmWorkspace(tempDir.Path);
        File.Delete(Path.Combine(root, "pnpm-lock.yaml"));
        var workspace = builder.AddPnpmWorkspace("ws", root);
        var app = workspace.AddJavaScriptApp("web", "web", "packages/web").WithBuildScript("build");

        var ex = await Assert.ThrowsAsync<DistributedApplicationException>(() => ManifestUtils.GetManifest(app.Resource, tempDir.Path));

        Assert.Contains("has no recognized lockfile", ex.Message);
    }

    private static string CreatePnpmWorkspace(string basePath)
    {
        var root = Path.Combine(basePath, "ws");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "package.json"), """{ "name": "root" }""");
        File.WriteAllText(Path.Combine(root, "pnpm-workspace.yaml"), "packages:\n  - \"packages/*\"\n");
        File.WriteAllText(Path.Combine(root, "pnpm-lock.yaml"), string.Empty);

        var webDir = Path.Combine(root, "packages", "web");
        Directory.CreateDirectory(webDir);
        File.WriteAllText(Path.Combine(webDir, "package.json"), """{ "name": "web", "scripts": { "build": "vite build", "dev": "vite" } }""");

        return root;
    }

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "ExecuteBeforeStartHooksAsync")]
    private static extern Task ExecuteBeforeStartHooksAsync(DistributedApplication app, CancellationToken cancellationToken);
}
