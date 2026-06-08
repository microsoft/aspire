// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREJAVASCRIPT001

using System.Runtime.CompilerServices;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.JavaScript.Tests;

public class WorkspaceValidationTests
{
    [Fact]
    public async Task PublishFlagsMemberNameTypo()
    {
        using var tempDir = new TestTempDirectory();
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
        using var tempDir = new TestTempDirectory();
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
    public async Task PublishFlagsMissingScript()
    {
        using var tempDir = new TestTempDirectory();
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
        using var tempDir = new TestTempDirectory();
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
        using var tempDir = new TestTempDirectory();
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);

        var root = CreatePnpmWorkspace(tempDir.Path);

        var workspace = builder.AddPnpmWorkspace("ws", root);
        workspace.AddJavaScriptApp("web", "web-typo", "packages/web").WithBuildScript("build");

        using var app = builder.Build();

        var ex = await Assert.ThrowsAsync<DistributedApplicationException>(() => ExecuteBeforeStartHooksAsync(app, CancellationToken.None));

        Assert.Contains("web-typo", ex.Message);
        Assert.Contains("not a declared workspace member", ex.Message);
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
