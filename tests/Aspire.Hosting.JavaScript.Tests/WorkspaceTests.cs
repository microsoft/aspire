// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREJAVASCRIPT001

using System.Diagnostics;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;
using Aspire.TestUtilities;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.JavaScript.Tests;

public class WorkspaceTests
{
    [Fact]
    public async Task VerifyPnpmWorkspaceDockerfileCopiesMemberManifests()
    {
        using var tempDir = new TestTempDirectory();
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: tempDir.Path).WithResourceCleanUp(true);

        // A two-member pnpm workspace on disk: packages/web and packages/api, each with a
        // package.json (so the expander resolves them as members), plus the root pnpm-workspace.yaml
        // declaring the glob and a lockfile so the manifest layer copies it too.
        var root = Path.Combine(tempDir.Path, "ws");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "package.json"), """{ "name": "root" }""");
        File.WriteAllText(Path.Combine(root, "pnpm-workspace.yaml"), "packages:\n  - \"packages/*\"\n");
        File.WriteAllText(Path.Combine(root, "pnpm-lock.yaml"), string.Empty);

        var webDir = Path.Combine(root, "packages", "web");
        Directory.CreateDirectory(webDir);
        File.WriteAllText(Path.Combine(webDir, "package.json"), """{ "name": "web", "scripts": { "build": "vite build" } }""");

        var apiDir = Path.Combine(root, "packages", "api");
        Directory.CreateDirectory(apiDir);
        File.WriteAllText(Path.Combine(apiDir, "package.json"), """{ "name": "api", "scripts": { "build": "tsc" } }""");

        var workspace = builder.AddPnpmWorkspace("ws", root);
        var webApp = workspace.AddJavaScriptApp("web", "web", "packages/web").WithBuildScript("build");

        await ManifestUtils.GetManifest(webApp.Resource, tempDir.Path);

        var dockerfilePath = Path.Combine(tempDir.Path, "web.Dockerfile");
        var dockerfile = await File.ReadAllTextAsync(dockerfilePath);

        await Verify(dockerfile);
    }

    [Fact]
    public async Task VerifyNpmWorkspaceDockerfileBuildsMember()
    {
        using var tempDir = new TestTempDirectory();
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: tempDir.Path).WithResourceCleanUp(true);

        // An npm workspace declares members via the root package.json "workspaces" array.
        // A package-lock.json switches the install to `npm ci` (reproducible) in publish mode.
        var root = Path.Combine(tempDir.Path, "ws");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "package.json"), """{ "name": "root", "workspaces": ["packages/*"] }""");
        File.WriteAllText(Path.Combine(root, "package-lock.json"), string.Empty);

        var webDir = Path.Combine(root, "packages", "web");
        Directory.CreateDirectory(webDir);
        File.WriteAllText(Path.Combine(webDir, "package.json"), """{ "name": "web", "scripts": { "build": "vite build" } }""");

        var apiDir = Path.Combine(root, "packages", "api");
        Directory.CreateDirectory(apiDir);
        File.WriteAllText(Path.Combine(apiDir, "package.json"), """{ "name": "api", "scripts": { "build": "tsc" } }""");

        var workspace = builder.AddNpmWorkspace("ws", root);
        var webApp = workspace.AddJavaScriptApp("web", "web", "packages/web").WithBuildScript("build");

        await ManifestUtils.GetManifest(webApp.Resource, tempDir.Path);

        var dockerfile = await File.ReadAllTextAsync(Path.Combine(tempDir.Path, "web.Dockerfile"));

        // npm places the workspace selector AFTER the script: `npm run build --workspace=web`.
        await Verify(dockerfile);
    }

    [Fact]
    public async Task VerifyBunWorkspaceDockerfileBuildsMember()
    {
        using var tempDir = new TestTempDirectory();
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: tempDir.Path).WithResourceCleanUp(true);

        var root = Path.Combine(tempDir.Path, "ws");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "package.json"), """{ "name": "root", "workspaces": ["packages/*"] }""");
        File.WriteAllText(Path.Combine(root, "bun.lock"), string.Empty);

        var webDir = Path.Combine(root, "packages", "web");
        Directory.CreateDirectory(webDir);
        File.WriteAllText(Path.Combine(webDir, "package.json"), """{ "name": "web", "scripts": { "build": "vite build" } }""");

        var apiDir = Path.Combine(root, "packages", "api");
        Directory.CreateDirectory(apiDir);
        File.WriteAllText(Path.Combine(apiDir, "package.json"), """{ "name": "api", "scripts": { "build": "tsc" } }""");

        var workspace = builder.AddBunWorkspace("ws", root);
        var webApp = workspace.AddJavaScriptApp("web", "web", "packages/web").WithBuildScript("build");

        await ManifestUtils.GetManifest(webApp.Resource, tempDir.Path);

        var dockerfile = await File.ReadAllTextAsync(Path.Combine(tempDir.Path, "web.Dockerfile"));

        // bun selects the member with `--filter web` before `run build`, on the oven/bun:1 base image.
        await Verify(dockerfile);
    }

    [Fact]
    public async Task VerifyYarnWorkspaceDockerfileBuildsMember()
    {
        using var tempDir = new TestTempDirectory();
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: tempDir.Path).WithResourceCleanUp(true);

        var root = Path.Combine(tempDir.Path, "ws");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "package.json"), """{ "name": "root", "workspaces": ["packages/*"] }""");
        File.WriteAllText(Path.Combine(root, "yarn.lock"), string.Empty);

        var webDir = Path.Combine(root, "packages", "web");
        Directory.CreateDirectory(webDir);
        File.WriteAllText(Path.Combine(webDir, "package.json"), """{ "name": "web", "scripts": { "build": "vite build" } }""");

        var apiDir = Path.Combine(root, "packages", "api");
        Directory.CreateDirectory(apiDir);
        File.WriteAllText(Path.Combine(apiDir, "package.json"), """{ "name": "api", "scripts": { "build": "tsc" } }""");

        var workspace = builder.AddYarnWorkspace("ws", root);
        var webApp = workspace.AddJavaScriptApp("web", "web", "packages/web").WithBuildScript("build");

        await ManifestUtils.GetManifest(webApp.Resource, tempDir.Path);

        var dockerfile = await File.ReadAllTextAsync(Path.Combine(tempDir.Path, "web.Dockerfile"));

        // Classic yarn selects the member with `yarn workspace web run build`.
        await Verify(dockerfile);
    }

    [Fact]
    public async Task VerifyYarnPnPWorkspaceDockerfileReRunsInstallAfterSourceCopy()
    {
        using var tempDir = new TestTempDirectory();
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: tempDir.Path).WithResourceCleanUp(true);

        // Yarn Berry / PnP: the presence of .yarnrc.yml + .yarn dir + .pnp.cjs switches the build to
        // a project-local .yarn/cache mount and re-runs `yarn install` after `COPY . .` so the
        // .pnp.cjs (which embeds absolute paths) is regenerated with the container's paths.
        var root = Path.Combine(tempDir.Path, "ws");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "package.json"), """{ "name": "root", "workspaces": ["packages/*"] }""");
        File.WriteAllText(Path.Combine(root, "yarn.lock"), string.Empty);
        File.WriteAllText(Path.Combine(root, ".yarnrc.yml"), "nodeLinker: pnp\n");
        File.WriteAllText(Path.Combine(root, ".pnp.cjs"), string.Empty);
        Directory.CreateDirectory(Path.Combine(root, ".yarn"));
        File.WriteAllText(Path.Combine(root, ".yarn", "placeholder.txt"), string.Empty);

        var webDir = Path.Combine(root, "packages", "web");
        Directory.CreateDirectory(webDir);
        File.WriteAllText(Path.Combine(webDir, "package.json"), """{ "name": "web", "scripts": { "build": "vite build" } }""");

        var workspace = builder.AddYarnWorkspace("ws", root);
        var webApp = workspace.AddJavaScriptApp("web", "web", "packages/web").WithBuildScript("build");

        await ManifestUtils.GetManifest(webApp.Resource, tempDir.Path);

        var dockerfile = await File.ReadAllTextAsync(Path.Combine(tempDir.Path, "web.Dockerfile"));

        await Verify(dockerfile);
    }

    [Fact]
    public void AddYarnWorkspaceCreatesResource()
    {
        var builder = DistributedApplication.CreateBuilder();

        builder.AddYarnWorkspace("yarn", "yarn-workspace");

        using var app = builder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var resource = Assert.Single(appModel.Resources.OfType<YarnWorkspaceResource>());

        Assert.NotNull(resource);
        Assert.Equal("yarn", resource.Name);
        Assert.Equal(Path.Combine(builder.AppHostDirectory, "yarn-workspace"), resource.WorkingDirectory);
    }

    [Fact]
    public void AddPnpmWorkspaceCreatesResource()
    {
        var builder = DistributedApplication.CreateBuilder();

        builder.AddPnpmWorkspace("pnpm", "pnpm-workspace");

        using var app = builder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var resource = Assert.Single(appModel.Resources.OfType<PnpmWorkspaceResource>());

        Assert.NotNull(resource);
        Assert.Equal("pnpm", resource.Name);
        Assert.Equal(Path.Combine(builder.AppHostDirectory, "pnpm-workspace"), resource.WorkingDirectory);
    }

    [Fact]
    public void AddNpmWorkspaceCreatesResource()
    {
        var builder = DistributedApplication.CreateBuilder();

        builder.AddNpmWorkspace("npm", "npm-workspace");

        using var app = builder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var resource = Assert.Single(appModel.Resources.OfType<NpmWorkspaceResource>());

        Assert.NotNull(resource);
        Assert.Equal("npm", resource.Name);
        Assert.Equal(Path.Combine(builder.AppHostDirectory, "npm-workspace"), resource.WorkingDirectory);
    }

    [Fact]
    public void AddBunWorkspaceCreatesResource()
    {
        var builder = DistributedApplication.CreateBuilder();

        builder.AddBunWorkspace("bun", "bun-workspace");

        using var app = builder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var resource = Assert.Single(appModel.Resources.OfType<BunWorkspaceResource>());

        Assert.NotNull(resource);
        Assert.Equal("bun", resource.Name);
        Assert.Equal(Path.Combine(builder.AppHostDirectory, "bun-workspace"), resource.WorkingDirectory);
    }

    [Fact]
    public void NpmWorkspaceGetRunScriptCommand()
    {
        var workspace = new NpmWorkspaceResource("test", "/tmp");
        // npm places the workspace selector AFTER the script name (it does not fit the prefix-before-run model).
        Assert.Equal(["npm", "run", "build", "--workspace=my-app"], workspace.GetRunScriptCommand("my-app", "build", []));
    }

    [Fact]
    public void BunWorkspaceGetRunScriptCommand()
    {
        var workspace = new BunWorkspaceResource("test", "/tmp");
        // bun requires the attached "--filter=<name>" form; the space-separated form fails to match
        // the member once "run" follows (observed on bun 1.3.14).
        Assert.Equal(["bun", "--filter=my-app", "run", "build"], workspace.GetRunScriptCommand("my-app", "build", []));
    }

    [Fact]
    public void AddJavaScriptAppToNpmWorkspaceCreatesResource()
    {
        var builder = DistributedApplication.CreateBuilder();

        var workspace = builder.AddNpmWorkspace("npm", "npm-workspace");
        workspace.AddJavaScriptApp("app1", "project1", "packages/web");

        using var app = builder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var workspaceResource = Assert.Single(appModel.Resources.OfType<NpmWorkspaceResource>());
        var appResource = Assert.Single(appModel.Resources.OfType<JavaScriptAppResource>());

        Assert.Equal("app1", appResource.Name);
        Assert.Equal(workspaceResource.WorkingDirectory, appResource.WorkingDirectory);

        Assert.True(appResource.TryGetLastAnnotation<JavaScriptWorkspaceContextAnnotation>(out var wsCtx));
        Assert.Same(workspaceResource, wsCtx.Workspace);
        Assert.Equal("project1", wsCtx.WorkspaceProjectName);

        AssertWorkspaceAppWiring(appResource, workspaceResource, "project1", "packages/web");

        // Verify the full npm workspace argv: selector trails the script (no `--` since dev takes no args).
        Assert.True(appResource.TryGetAnnotationsOfType<CommandLineArgsCallbackAnnotation>(out var argsCallbackAnnotations));
        List<object> args = [];
        var ctx = new CommandLineArgsCallbackContext(args, appResource);

        foreach (var annotation in argsCallbackAnnotations)
        {
            annotation.Callback(ctx);
        }

        Assert.Collection(args,
            arg => Assert.Equal("npm", arg),
            arg => Assert.Equal("run", arg),
            arg => Assert.Equal("dev", arg),
            arg => Assert.Equal("--workspace=project1", arg)
        );
    }

    [Fact]
    public void AddJavaScriptAppToBunWorkspaceCreatesResource()
    {
        var builder = DistributedApplication.CreateBuilder();

        var workspace = builder.AddBunWorkspace("bun", "bun-workspace");
        workspace.AddJavaScriptApp("app1", "project1", "packages/web");

        using var app = builder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var workspaceResource = Assert.Single(appModel.Resources.OfType<BunWorkspaceResource>());
        var appResource = Assert.Single(appModel.Resources.OfType<JavaScriptAppResource>());

        Assert.Equal("app1", appResource.Name);
        Assert.Equal(workspaceResource.WorkingDirectory, appResource.WorkingDirectory);

        Assert.True(appResource.TryGetLastAnnotation<JavaScriptWorkspaceContextAnnotation>(out var wsCtx));
        Assert.Same(workspaceResource, wsCtx.Workspace);
        Assert.Equal("project1", wsCtx.WorkspaceProjectName);

        AssertWorkspaceAppWiring(appResource, workspaceResource, "project1", "packages/web");

        Assert.True(appResource.TryGetAnnotationsOfType<CommandLineArgsCallbackAnnotation>(out var argsCallbackAnnotations));
        List<object> args = [];
        var ctx = new CommandLineArgsCallbackContext(args, appResource);

        foreach (var annotation in argsCallbackAnnotations)
        {
            annotation.Callback(ctx);
        }

        Assert.Collection(args,
            arg => Assert.Equal("bun", arg),
            arg => Assert.Equal("--filter=project1", arg),
            arg => Assert.Equal("run", arg),
            arg => Assert.Equal("dev", arg)
        );
    }

    [Fact]
    public void AddViteAppToNpmWorkspaceCreatesResource()
    {
        var builder = DistributedApplication.CreateBuilder();

        var workspace = builder.AddNpmWorkspace("npm", "npm-workspace");
        workspace.AddViteApp("web", "project1", "packages/web");

        using var app = builder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var workspaceResource = Assert.Single(appModel.Resources.OfType<NpmWorkspaceResource>());
        var appResource = Assert.Single(appModel.Resources.OfType<ViteAppResource>());

        Assert.Equal("web", appResource.Name);
        AssertWorkspaceAppWiring(appResource, workspaceResource, "project1", "packages/web");
    }

    [Fact]
    public void AddNodeAppToBunWorkspaceCreatesResource()
    {
        var builder = DistributedApplication.CreateBuilder();

        var workspace = builder.AddBunWorkspace("bun", "bun-workspace");
        workspace.AddNodeApp("api", "project1", "apps/api", "server.js");

        using var app = builder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var workspaceResource = Assert.Single(appModel.Resources.OfType<BunWorkspaceResource>());
        var appResource = Assert.Single(appModel.Resources.OfType<NodeAppResource>());

        Assert.Equal("api", appResource.Name);
        AssertWorkspaceAppWiring(appResource, workspaceResource, "project1", "apps/api");
    }

    [Fact]
    public void AddJavaScriptAppToYarnWorkspaceCreatesResource()
    {
        var builder = DistributedApplication.CreateBuilder();

        var workspace = builder.AddYarnWorkspace("yarn", "yarn-workspace");
        workspace.AddJavaScriptApp("app1", "project1", "packages/web");

        using var app = builder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var workspaceResource = Assert.Single(appModel.Resources.OfType<YarnWorkspaceResource>());
        var appResource = Assert.Single(appModel.Resources.OfType<JavaScriptAppResource>());

        Assert.Equal("app1", appResource.Name);
        Assert.Equal(workspaceResource.WorkingDirectory, appResource.WorkingDirectory);

        // Verify workspace context annotation is present
        Assert.True(appResource.TryGetLastAnnotation<JavaScriptWorkspaceContextAnnotation>(out var wsCtx));
        Assert.Same(workspaceResource, wsCtx.Workspace);
        Assert.Equal("project1", wsCtx.WorkspaceProjectName);

        AssertWorkspaceAppWiring(appResource, workspaceResource, "project1", "packages/web");

        // Verify the arguments are the full workspace argv (the resource owns the whole command).
        Assert.True(appResource.TryGetAnnotationsOfType<CommandLineArgsCallbackAnnotation>(out var argsCallbackAnnotations));
        List<object> args = [];
        var ctx = new CommandLineArgsCallbackContext(args, appResource);

        foreach (var annotation in argsCallbackAnnotations)
        {
            annotation.Callback(ctx);
        }

        Assert.Collection(args,
            arg => Assert.Equal("yarn", arg),
            arg => Assert.Equal("workspace", arg),
            arg => Assert.Equal("project1", arg),
            arg => Assert.Equal("run", arg),
            arg => Assert.Equal("dev", arg)
        );
    }

    [Fact]
    public void AddJavaScriptAppToPnpmWorkspaceCreatesResource()
    {
        var builder = DistributedApplication.CreateBuilder();

        var workspace = builder.AddPnpmWorkspace("pnpm", "pnpm-workspace");
        workspace.AddJavaScriptApp("app1", "project1", "packages/web");

        using var app = builder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var workspaceResource = Assert.Single(appModel.Resources.OfType<PnpmWorkspaceResource>());
        var appResource = Assert.Single(appModel.Resources.OfType<JavaScriptAppResource>());

        Assert.Equal("app1", appResource.Name);
        Assert.Equal(workspaceResource.WorkingDirectory, appResource.WorkingDirectory);

        // Verify workspace context annotation
        Assert.True(appResource.TryGetLastAnnotation<JavaScriptWorkspaceContextAnnotation>(out var wsCtx));
        Assert.Equal("project1", wsCtx.WorkspaceProjectName);

        AssertWorkspaceAppWiring(appResource, workspaceResource, "project1", "packages/web");

        // Verify the arguments are the full workspace argv, including pnpm's topological filter.
        Assert.True(appResource.TryGetAnnotationsOfType<CommandLineArgsCallbackAnnotation>(out var argsCallbackAnnotations));
        List<object> args = [];
        var ctx = new CommandLineArgsCallbackContext(args, appResource);

        foreach (var annotation in argsCallbackAnnotations)
        {
            annotation.Callback(ctx);
        }

        Assert.Collection(args,
            arg => Assert.Equal("pnpm", arg),
            arg => Assert.Equal("--filter", arg),
            arg => Assert.Equal("project1...", arg),
            arg => Assert.Equal("run", arg),
            arg => Assert.Equal("dev", arg)
        );
    }

    [Fact]
    public void WorkspaceAppWaitsForWorkspaceInstaller()
    {
        var builder = DistributedApplication.CreateBuilder();

        var workspace = builder.AddYarnWorkspace("yarn", "yarn-workspace");
        workspace.AddJavaScriptApp("app1", "project1", "packages/web");

        using var app = builder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var workspaceResource = Assert.Single(appModel.Resources.OfType<YarnWorkspaceResource>());
        var appResource = Assert.Single(appModel.Resources.OfType<JavaScriptAppResource>());

        // Verify the installer resource exists for the workspace
        var installerResource = Assert.Single(appModel.Resources.OfType<JavaScriptInstallerResource>());
        Assert.Equal("yarn-installer", installerResource.Name);

        // Verify the app resource waits for the installer
        Assert.True(appResource.TryGetAnnotationsOfType<WaitAnnotation>(out var waitAnnotations));
        var waitAnnotation = Assert.Single(waitAnnotations);
        Assert.Same(installerResource, waitAnnotation.Resource);
    }

    [Fact]
    public void WorkspaceAppDoesNotCreatePerAppInstaller()
    {
        var builder = DistributedApplication.CreateBuilder();

        var workspace = builder.AddYarnWorkspace("yarn", "yarn-workspace");
        workspace.AddJavaScriptApp("app1", "project1", "packages/web");

        using var app = builder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        // Only the workspace-level installer should exist, not a per-app installer
        var installers = appModel.Resources.OfType<JavaScriptInstallerResource>().ToList();
        Assert.Single(installers);
        Assert.Equal("yarn-installer", installers[0].Name);
    }

    [Fact]
    public void YarnWorkspaceGetRunScriptCommandReturnsFullArgv()
    {
        var workspace = new YarnWorkspaceResource("test", "/tmp");
        Assert.Equal(["yarn", "workspace", "my-app", "run", "build"], workspace.GetRunScriptCommand("my-app", "build", []));
    }

    [Fact]
    public void PnpmWorkspaceGetRunScriptCommandReturnsTopologicalFilterArgv()
    {
        var workspace = new PnpmWorkspaceResource("test", "/tmp");
        Assert.Equal(["pnpm", "--filter", "my-app...", "run", "build"], workspace.GetRunScriptCommand("my-app", "build", []));
    }

    [Fact]
    public void AddViteAppToYarnWorkspaceCreatesResource()
    {
        var builder = DistributedApplication.CreateBuilder();

        var workspace = builder.AddYarnWorkspace("yarn", "yarn-workspace");
        workspace.AddViteApp("web", "project1", "packages/web");

        using var app = builder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var workspaceResource = Assert.Single(appModel.Resources.OfType<YarnWorkspaceResource>());
        var appResource = Assert.Single(appModel.Resources.OfType<ViteAppResource>());

        Assert.Equal("web", appResource.Name);
        Assert.Equal(workspaceResource.WorkingDirectory, appResource.WorkingDirectory);
        AssertWorkspaceAppWiring(appResource, workspaceResource, "project1", "packages/web");
    }

    [Fact]
    public void AddViteAppToPnpmWorkspaceCreatesResource()
    {
        var builder = DistributedApplication.CreateBuilder();

        var workspace = builder.AddPnpmWorkspace("pnpm", "pnpm-workspace");
        workspace.AddViteApp("web", "project1", "packages/web");

        using var app = builder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var workspaceResource = Assert.Single(appModel.Resources.OfType<PnpmWorkspaceResource>());
        var appResource = Assert.Single(appModel.Resources.OfType<ViteAppResource>());

        Assert.Equal("web", appResource.Name);
        AssertWorkspaceAppWiring(appResource, workspaceResource, "project1", "packages/web");
    }

    [Fact]
    public void AddNextJsAppToYarnWorkspaceCreatesResource()
    {
        var builder = DistributedApplication.CreateBuilder();

        var workspace = builder.AddYarnWorkspace("yarn", "yarn-workspace");
        workspace.AddNextJsApp("web", "project1", "apps/web");

        using var app = builder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var workspaceResource = Assert.Single(appModel.Resources.OfType<YarnWorkspaceResource>());
        var appResource = Assert.Single(appModel.Resources.OfType<NextJsAppResource>());

        Assert.Equal("web", appResource.Name);
        Assert.Equal(workspaceResource.WorkingDirectory, appResource.WorkingDirectory);
        AssertWorkspaceAppWiring(appResource, workspaceResource, "project1", "apps/web");
    }

    [Fact]
    public void AddNextJsAppToPnpmWorkspaceCreatesResource()
    {
        var builder = DistributedApplication.CreateBuilder();

        var workspace = builder.AddPnpmWorkspace("pnpm", "pnpm-workspace");
        workspace.AddNextJsApp("web", "project1", "apps/web");

        using var app = builder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var workspaceResource = Assert.Single(appModel.Resources.OfType<PnpmWorkspaceResource>());
        var appResource = Assert.Single(appModel.Resources.OfType<NextJsAppResource>());

        Assert.Equal("web", appResource.Name);
        AssertWorkspaceAppWiring(appResource, workspaceResource, "project1", "apps/web");
    }

    [Fact]
    public void AddNodeAppToYarnWorkspaceCreatesResource()
    {
        var builder = DistributedApplication.CreateBuilder();

        var workspace = builder.AddYarnWorkspace("yarn", "yarn-workspace");
        workspace.AddNodeApp("api", "project1", "apps/api", "server.js");

        using var app = builder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var workspaceResource = Assert.Single(appModel.Resources.OfType<YarnWorkspaceResource>());
        var appResource = Assert.Single(appModel.Resources.OfType<NodeAppResource>());

        Assert.Equal("api", appResource.Name);
        Assert.Equal(workspaceResource.WorkingDirectory, appResource.WorkingDirectory);
        AssertWorkspaceAppWiring(appResource, workspaceResource, "project1", "apps/api");
    }

    [Fact]
    public void AddNodeAppToPnpmWorkspaceCreatesResource()
    {
        var builder = DistributedApplication.CreateBuilder();

        var workspace = builder.AddPnpmWorkspace("pnpm", "pnpm-workspace");
        workspace.AddNodeApp("api", "project1", "apps/api", "server.js");

        using var app = builder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var workspaceResource = Assert.Single(appModel.Resources.OfType<PnpmWorkspaceResource>());
        var appResource = Assert.Single(appModel.Resources.OfType<NodeAppResource>());

        Assert.Equal("api", appResource.Name);
        AssertWorkspaceAppWiring(appResource, workspaceResource, "project1", "apps/api");
    }

    [Fact]
    public async Task WorkspaceStaticWebsiteContainerFilesSourceMatchesDockerfileCopyFrom()
    {
        using var tempDir = new TestTempDirectory();
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: tempDir.Path).WithResourceCleanUp(true);

        var root = Path.Combine(tempDir.Path, "ws");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "package.json"), """{ "name": "root" }""");
        File.WriteAllText(Path.Combine(root, "pnpm-workspace.yaml"), "packages:\n  - \"packages/*\"\n");
        File.WriteAllText(Path.Combine(root, "pnpm-lock.yaml"), string.Empty);

        var webDir = Path.Combine(root, "packages", "web");
        Directory.CreateDirectory(webDir);
        File.WriteAllText(Path.Combine(webDir, "package.json"), """{ "name": "web", "scripts": { "build": "vite build" } }""");

        var workspace = builder.AddPnpmWorkspace("ws", root);
        var webApp = workspace.AddJavaScriptApp("web", "web", "packages/web")
            .WithBuildScript("build")
            .PublishAsStaticWebsite();

        await ManifestUtils.GetManifest(webApp.Resource, tempDir.Path);

        var dockerfile = await File.ReadAllTextAsync(Path.Combine(tempDir.Path, "web.Dockerfile"));

        // The container-files source must be the member-scoped build output so a consuming
        // resource copies from the same path the runtime stage copies from in the Dockerfile.
        var containerFilesSource = webApp.Resource.Annotations.OfType<ContainerFilesSourceAnnotation>().Single();
        Assert.Equal("/app/packages/web/dist", containerFilesSource.SourcePath);
        Assert.Contains($"COPY --from=build {containerFilesSource.SourcePath} /app/wwwroot", dockerfile);
    }

    [Fact]
    public async Task WorkspaceNodeServerContainerFilesSourceMatchesDockerfileCopyFrom()
    {
        using var tempDir = new TestTempDirectory();
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: tempDir.Path).WithResourceCleanUp(true);

        var root = Path.Combine(tempDir.Path, "ws");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "package.json"), """{ "name": "root" }""");
        File.WriteAllText(Path.Combine(root, "pnpm-workspace.yaml"), "packages:\n  - \"packages/*\"\n");
        File.WriteAllText(Path.Combine(root, "pnpm-lock.yaml"), string.Empty);

        var webDir = Path.Combine(root, "packages", "web");
        Directory.CreateDirectory(webDir);
        File.WriteAllText(Path.Combine(webDir, "package.json"), """{ "name": "web", "scripts": { "build": "vite build" } }""");

        var workspace = builder.AddPnpmWorkspace("ws", root);
        var webApp = workspace.AddJavaScriptApp("web", "web", "packages/web")
            .WithBuildScript("build")
            .PublishAsNodeServer("dist/server/index.mjs", "dist");

        await ManifestUtils.GetManifest(webApp.Resource, tempDir.Path);

        var dockerfile = await File.ReadAllTextAsync(Path.Combine(tempDir.Path, "web.Dockerfile"));

        var containerFilesSource = webApp.Resource.Annotations.OfType<ContainerFilesSourceAnnotation>().Single();
        Assert.Equal("/app/packages/web/dist", containerFilesSource.SourcePath);
        Assert.Contains($"COPY --from=build {containerFilesSource.SourcePath} {containerFilesSource.SourcePath}", dockerfile);
    }

    [Fact]
    public async Task ContainerFilesSourceResolvesWorkspacePathRegardlessOfConfigOrder()
    {
        using var tempDir = new TestTempDirectory();
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: tempDir.Path).WithResourceCleanUp(true);

        var root = Path.Combine(tempDir.Path, "ws");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "package.json"), """{ "name": "root" }""");
        File.WriteAllText(Path.Combine(root, "pnpm-workspace.yaml"), "packages:\n  - \"packages/*\"\n");
        File.WriteAllText(Path.Combine(root, "pnpm-lock.yaml"), string.Empty);

        var webDir = Path.Combine(root, "packages", "web");
        Directory.CreateDirectory(webDir);
        File.WriteAllText(Path.Combine(webDir, "package.json"), """{ "name": "web", "scripts": { "build": "vite build" } }""");

        var workspace = builder.AddPnpmWorkspace("ws", root);
        var webApp = workspace.AddJavaScriptApp("web", "web", "packages/web")
            .WithBuildScript("build")
            .PublishAsStaticWebsite();

        // Simulate the workspace-app-path annotation being established AFTER PublishAs* by
        // re-applying it last. Because the container-files source is resolved lazily at
        // Dockerfile-generation time, the resolved SourcePath must still be member-scoped
        // regardless of the order in which workspace config and PublishAs* ran.
        webApp.WithAnnotation(new JavaScriptWorkspaceAppPathAnnotation(webDir, "packages/web"));

        await ManifestUtils.GetManifest(webApp.Resource, tempDir.Path);

        var containerFilesSource = webApp.Resource.Annotations.OfType<ContainerFilesSourceAnnotation>().Single();
        Assert.Equal("/app/packages/web/dist", containerFilesSource.SourcePath);
    }

    [Fact]
    [RequiresFeature(TestFeature.Docker | TestFeature.DockerPluginBuildx)]
    [OuterloopTest("long-running docker build")]
    public async Task VerifyPnpmWorkspaceMemberDockerImageBuilds()
    {
        using var tempDir = new TestTempDirectory();
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: tempDir.Path).WithResourceCleanUp(true);

        // A real two-member pnpm workspace where `web` depends on the workspace library `shared`
        // (workspace:*). This is what exercises pnpm's topological filter `--filter web... run build`:
        // `shared` must be built before `web`. Build scripts avoid network installs so the image
        // build is deterministic in CI while still validating the generated Dockerfile end-to-end.
        var root = Path.Combine(tempDir.Path, "ws");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "package.json"), """{ "name": "root", "private": true }""");
        await File.WriteAllTextAsync(Path.Combine(root, "pnpm-workspace.yaml"), "packages:\n  - \"packages/*\"\n");
        // A real pnpm v9 lockfile that matches the two members below (web -> shared via workspace:*).
        // The validator requires a recognized lockfile, and the in-container `pnpm install --frozen-lockfile`
        // requires the importers' specifiers to match the member package.json files exactly. This lockfile
        // was generated by `pnpm install --lockfile-only` for this exact fixture shape (only the internal
        // workspace:* link, no registry dependencies, so the install needs no network).
        await File.WriteAllTextAsync(Path.Combine(root, "pnpm-lock.yaml"), """
            lockfileVersion: '9.0'

            settings:
              autoInstallPeers: true
              excludeLinksFromLockfile: false

            importers:

              .: {}

              packages/shared: {}

              packages/web:
                dependencies:
                  shared:
                    specifier: workspace:*
                    version: link:../shared

            """);

        var sharedDir = Path.Combine(root, "packages", "shared");
        Directory.CreateDirectory(sharedDir);
        await File.WriteAllTextAsync(Path.Combine(sharedDir, "package.json"), """{ "name": "shared", "version": "1.0.0", "scripts": { "build": "echo shared-built" } }""");

        var webDir = Path.Combine(root, "packages", "web");
        Directory.CreateDirectory(webDir);
        await File.WriteAllTextAsync(Path.Combine(webDir, "package.json"), """{ "name": "web", "version": "1.0.0", "dependencies": { "shared": "workspace:*" }, "scripts": { "build": "echo web-built" } }""");

        var webApp = builder.AddPnpmWorkspace("ws", root)
            .AddJavaScriptApp("web", "web", "packages/web")
            .WithBuildScript("build");

        var imageId = await GenerateDockerfileAndBuildImageAsync(webApp.Resource, root, "web", TestContext.Current.CancellationToken);

        Assert.False(string.IsNullOrWhiteSpace(imageId), "docker build should produce an image id");
    }

    [Fact]
    [RequiresFeature(TestFeature.Docker | TestFeature.DockerPluginBuildx)]
    [OuterloopTest("long-running docker build")]
    public async Task VerifyYarnPnPWorkspaceMemberDockerImageBuilds()
    {
        using var tempDir = new TestTempDirectory();
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: tempDir.Path).WithResourceCleanUp(true);

        // A Yarn Berry / PnP workspace (nodeLinker: pnp, .yarnrc.yml + .yarn dir + .pnp.cjs). The presence
        // of .pnp.cjs flips WithYarnWorkspaceDefaults into PnP mode, so the generated Dockerfile re-runs
        // `yarn install` after `COPY . .` to regenerate .pnp.cjs (which embeds absolute paths) with the
        // container's paths. The members declare only the workspace:* internal link (no registry deps),
        // so `yarn install --immutable` resolves offline against the matching lockfile below.
        var root = Path.Combine(tempDir.Path, "ws");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "package.json"), """{ "name": "root", "private": true, "workspaces": ["packages/*"], "packageManager": "yarn@4.5.0" }""");
        await File.WriteAllTextAsync(Path.Combine(root, ".yarnrc.yml"), "nodeLinker: pnp\nenableTelemetry: false\n");
        Directory.CreateDirectory(Path.Combine(root, ".yarn"));
        await File.WriteAllTextAsync(Path.Combine(root, ".yarn", ".gitkeep"), string.Empty);
        // A placeholder .pnp.cjs is enough to trigger PnP detection on the host; the container's
        // post-copy `yarn install` regenerates the real one for the build to run against.
        await File.WriteAllTextAsync(Path.Combine(root, ".pnp.cjs"), "// placeholder regenerated by yarn install in the container\n");

        // Matching yarn 4 lockfile (generated by `yarn install` for this fixture shape) so the
        // first --immutable install does not fail on lockfile drift.
        await File.WriteAllTextAsync(Path.Combine(root, "yarn.lock"), """
            # This file is generated by running "yarn install" inside your project.
            # Manual changes might be lost - proceed with caution!

            __metadata:
              version: 8
              cacheKey: 10c0

            "root@workspace:.":
              version: 0.0.0-use.local
              resolution: "root@workspace:."
              languageName: unknown
              linkType: soft

            "shared@workspace:*, shared@workspace:packages/shared":
              version: 0.0.0-use.local
              resolution: "shared@workspace:packages/shared"
              languageName: unknown
              linkType: soft

            "web@workspace:packages/web":
              version: 0.0.0-use.local
              resolution: "web@workspace:packages/web"
              dependencies:
                shared: "workspace:*"
              languageName: unknown
              linkType: soft

            """);

        var sharedDir = Path.Combine(root, "packages", "shared");
        Directory.CreateDirectory(sharedDir);
        await File.WriteAllTextAsync(Path.Combine(sharedDir, "package.json"), """{ "name": "shared", "version": "1.0.0", "scripts": { "build": "echo shared-built" } }""");

        var webDir = Path.Combine(root, "packages", "web");
        Directory.CreateDirectory(webDir);
        await File.WriteAllTextAsync(Path.Combine(webDir, "package.json"), """{ "name": "web", "version": "1.0.0", "dependencies": { "shared": "workspace:*" }, "scripts": { "build": "echo web-built" } }""");

        var webApp = builder.AddYarnWorkspace("ws", root)
            .AddJavaScriptApp("web", "web", "packages/web")
            .WithBuildScript("build");

        var imageId = await GenerateDockerfileAndBuildImageAsync(webApp.Resource, root, "web", TestContext.Current.CancellationToken);

        Assert.False(string.IsNullOrWhiteSpace(imageId), "docker build should produce an image id");
    }

    [Fact]
    [RequiresFeature(TestFeature.Docker | TestFeature.DockerPluginBuildx)]
    [OuterloopTest("long-running docker build")]
    public async Task VerifyNpmWorkspaceMemberDockerImageBuilds()
    {
        using var tempDir = new TestTempDirectory();
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: tempDir.Path).WithResourceCleanUp(true);

        // A real two-member npm workspace where `web` depends on the workspace library `shared`.
        // The T3 validator requires a recognized lockfile, and publish mode installs with `npm ci`,
        // which requires a package-lock.json consistent with the tree. The members declare only the
        // internal workspace link (no registry dependencies), so `npm ci` resolves entirely offline.
        var root = Path.Combine(tempDir.Path, "ws");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "package.json"), """{ "name": "root", "private": true, "workspaces": ["packages/*"] }""");
        // A real npm lockfileVersion 3 package-lock.json for this exact fixture (web -> shared via the
        // internal workspace link only). Generated by `npm install --package-lock-only` in a node:22-slim
        // container for this shape; verified that `npm ci` then `npm run build --workspace=web` runs with
        // --network=none (no registry access), so the in-container install is deterministic and offline.
        await File.WriteAllTextAsync(Path.Combine(root, "package-lock.json"), """
            {
              "name": "root",
              "lockfileVersion": 3,
              "requires": true,
              "packages": {
                "": {
                  "name": "root",
                  "workspaces": [
                    "packages/*"
                  ]
                },
                "node_modules/shared": {
                  "resolved": "packages/shared",
                  "link": true
                },
                "node_modules/web": {
                  "resolved": "packages/web",
                  "link": true
                },
                "packages/shared": {
                  "version": "1.0.0"
                },
                "packages/web": {
                  "version": "1.0.0",
                  "dependencies": {
                    "shared": "*"
                  }
                }
              }
            }
            """);

        var sharedDir = Path.Combine(root, "packages", "shared");
        Directory.CreateDirectory(sharedDir);
        await File.WriteAllTextAsync(Path.Combine(sharedDir, "package.json"), """{ "name": "shared", "version": "1.0.0", "scripts": { "build": "echo shared-built" } }""");

        var webDir = Path.Combine(root, "packages", "web");
        Directory.CreateDirectory(webDir);
        await File.WriteAllTextAsync(Path.Combine(webDir, "package.json"), """{ "name": "web", "version": "1.0.0", "dependencies": { "shared": "*" }, "scripts": { "build": "echo web-built" } }""");

        var webApp = builder.AddNpmWorkspace("ws", root)
            .AddJavaScriptApp("web", "web", "packages/web")
            .WithBuildScript("build");

        var imageId = await GenerateDockerfileAndBuildImageAsync(webApp.Resource, root, "web", TestContext.Current.CancellationToken);

        Assert.False(string.IsNullOrWhiteSpace(imageId), "docker build should produce an image id");
    }

    [Fact]
    [RequiresFeature(TestFeature.Docker | TestFeature.DockerPluginBuildx)]
    [OuterloopTest("long-running docker build")]
    public async Task VerifyBunWorkspaceMemberDockerImageBuilds()
    {
        using var tempDir = new TestTempDirectory();
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputPath: tempDir.Path).WithResourceCleanUp(true);

        // A real two-member bun workspace where `web` depends on the workspace library `shared`
        // (workspace:*). Members use the oven/bun:1 base image, and publish mode installs with
        // `bun install --frozen-lockfile` when a bun.lock is present. The members declare only the
        // internal workspace link (no registry dependencies), so the frozen install resolves offline.
        var root = Path.Combine(tempDir.Path, "ws");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "package.json"), """{ "name": "root", "private": true, "workspaces": ["packages/*"] }""");
        // A real bun.lock (text format, lockfileVersion 1) for this exact fixture, generated by
        // `bun install` in an oven/bun:1 container. Verified that `bun install --frozen-lockfile`
        // then `bun --filter=web run build` runs with --network=none, so the install is offline.
        await File.WriteAllTextAsync(Path.Combine(root, "bun.lock"), """
            {
              "lockfileVersion": 1,
              "configVersion": 1,
              "workspaces": {
                "": {
                  "name": "root",
                },
                "packages/shared": {
                  "name": "shared",
                  "version": "1.0.0",
                },
                "packages/web": {
                  "name": "web",
                  "version": "1.0.0",
                  "dependencies": {
                    "shared": "workspace:*",
                  },
                },
              },
              "packages": {
                "shared": ["shared@workspace:packages/shared"],

                "web": ["web@workspace:packages/web"],
              }
            }
            """);

        var sharedDir = Path.Combine(root, "packages", "shared");
        Directory.CreateDirectory(sharedDir);
        await File.WriteAllTextAsync(Path.Combine(sharedDir, "package.json"), """{ "name": "shared", "version": "1.0.0", "scripts": { "build": "echo shared-built" } }""");

        var webDir = Path.Combine(root, "packages", "web");
        Directory.CreateDirectory(webDir);
        await File.WriteAllTextAsync(Path.Combine(webDir, "package.json"), """{ "name": "web", "version": "1.0.0", "dependencies": { "shared": "workspace:*" }, "scripts": { "build": "echo web-built" } }""");

        var webApp = builder.AddBunWorkspace("ws", root)
            .AddJavaScriptApp("web", "web", "packages/web")
            .WithBuildScript("build");

        var imageId = await GenerateDockerfileAndBuildImageAsync(webApp.Resource, root, "web", TestContext.Current.CancellationToken);

        Assert.False(string.IsNullOrWhiteSpace(imageId), "docker build should produce an image id");
    }

    /// <summary>
    /// Materializes the publish-mode Dockerfile generated for a workspace member into its build
    /// context (the workspace root) and runs a real <c>docker build</c>, returning the built image id.
    /// </summary>
    private static async Task<string> GenerateDockerfileAndBuildImageAsync(
        IResource resource,
        string contextPath,
        string dockerfileName,
        CancellationToken cancellationToken)
    {
        // Build the model so the deferred PublishAsDockerFile callback attaches the
        // DockerfileBuildAnnotation (which carries the factory + build context path).
        await ManifestUtils.GetManifest(resource, contextPath);

        Assert.True(resource.TryGetLastAnnotation<DockerfileBuildAnnotation>(out var dockerfileBuild));

        // Emit the generated Dockerfile (and its .dockerignore sibling) into the workspace-root
        // context so `docker build <context>` sees the same relative COPY paths the Dockerfile uses.
        var dockerfilePath = Path.Combine(contextPath, $"{dockerfileName}.Dockerfile");
        var factoryContext = new DockerfileFactoryContext
        {
            Services = new ServiceCollection().BuildServiceProvider(),
            Resource = resource,
            CancellationToken = cancellationToken
        };
        await dockerfileBuild.EmitDockerfileArtifactsAsync(factoryContext, dockerfilePath);

        Assert.True(File.Exists(dockerfilePath), $"Dockerfile should exist at {dockerfilePath}");

        var imageName = $"aspire-ws-test-{Guid.NewGuid():N}";
        try
        {
            var buildResult = await RunDockerCommandAsync(
                $"build --network=host -t {imageName} -f \"{dockerfilePath}\" .",
                contextPath,
                cancellationToken);

            Assert.True(buildResult.ExitCode == 0, $"Docker build failed with exit code {buildResult.ExitCode}.\nStdout: {buildResult.Stdout}\nStderr: {buildResult.Stderr}");

            var inspect = await RunDockerCommandAsync($"image inspect -f \"{{{{.Id}}}}\" {imageName}", contextPath, cancellationToken);
            Assert.True(inspect.ExitCode == 0, $"docker image inspect failed: {inspect.Stderr}");

            return inspect.Stdout.Trim();
        }
        finally
        {
            await RunDockerCommandAsync($"rmi -f {imageName}", contextPath, cancellationToken);
        }
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunDockerCommandAsync(string arguments, string workingDirectory, CancellationToken cancellationToken)
    {
        var processStartInfo = new ProcessStartInfo
        {
            FileName = "docker",
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(processStartInfo);
        Assert.NotNull(process);

        // Read both streams concurrently to avoid deadlock when a pipe buffer fills.
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken);

        return (process.ExitCode, await stdoutTask, await stderrTask);
    }

    private static void AssertWorkspaceAppWiring(
        JavaScriptAppResource appResource,
        JavaScriptWorkspaceResource workspaceResource,
        string workspaceProjectName,
        string? expectedPackagePath = null)
    {
        // Workspace context annotation present and points at the right workspace/project
        Assert.True(appResource.TryGetLastAnnotation<JavaScriptWorkspaceContextAnnotation>(out var wsCtx));
        Assert.Same(workspaceResource, wsCtx.Workspace);
        Assert.Equal(workspaceProjectName, wsCtx.WorkspaceProjectName);

        // Package manager and install command inherited from workspace
        Assert.True(appResource.TryGetLastAnnotation<JavaScriptPackageManagerAnnotation>(out _));
        Assert.True(appResource.TryGetLastAnnotation<JavaScriptInstallCommandAnnotation>(out _));

        // Parent relationship points at the workspace
        Assert.True(appResource.TryGetAnnotationsOfType<ResourceRelationshipAnnotation>(out var relationships));
        Assert.Contains(relationships, r => r.Type == "Parent" && ReferenceEquals(r.Resource, workspaceResource));

        if (expectedPackagePath is not null)
        {
            Assert.True(appResource.TryGetLastAnnotation<JavaScriptWorkspaceAppPathAnnotation>(out var workspaceAppPath));
            Assert.Equal(expectedPackagePath, workspaceAppPath.PackagePath);

            var expectedAppDirectory = Path.Combine(
                [workspaceResource.WorkingDirectory, .. expectedPackagePath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries)]);

            Assert.Equal(expectedAppDirectory, workspaceAppPath.AppDirectory);
        }

        // Installer wait wired up
        Assert.True(appResource.TryGetAnnotationsOfType<WaitAnnotation>(out var waits));
        Assert.Contains(waits, w => w.Resource is JavaScriptInstallerResource);
    }
}
