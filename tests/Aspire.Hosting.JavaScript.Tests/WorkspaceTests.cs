// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREJAVASCRIPT001

using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.JavaScript.Tests;

public class WorkspaceTests
{
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
    public void AddJavaScriptAppToYarnWorkspaceCreatesResource()
    {
        var builder = DistributedApplication.CreateBuilder();

        var workspace = builder.AddYarnWorkspace("yarn", "yarn-workspace");
        workspace.AddJavaScriptApp("app1", "project1");

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
        Assert.Equal(["workspace", "project1"], wsCtx.CommandPrefix);

        // Verify the arguments include workspace prefix
        Assert.True(appResource.TryGetAnnotationsOfType<CommandLineArgsCallbackAnnotation>(out var argsCallbackAnnotations));
        List<object> args = [];
        var ctx = new CommandLineArgsCallbackContext(args, appResource);

        foreach (var annotation in argsCallbackAnnotations)
        {
            annotation.Callback(ctx);
        }

        Assert.Collection(args,
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
        workspace.AddJavaScriptApp("app1", "project1");

        using var app = builder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var workspaceResource = Assert.Single(appModel.Resources.OfType<PnpmWorkspaceResource>());
        var appResource = Assert.Single(appModel.Resources.OfType<JavaScriptAppResource>());

        Assert.Equal("app1", appResource.Name);
        Assert.Equal(workspaceResource.WorkingDirectory, appResource.WorkingDirectory);

        // Verify workspace context annotation
        Assert.True(appResource.TryGetLastAnnotation<JavaScriptWorkspaceContextAnnotation>(out var wsCtx));
        Assert.Equal(["--filter", "project1"], wsCtx.CommandPrefix);

        // Verify the arguments include workspace prefix
        Assert.True(appResource.TryGetAnnotationsOfType<CommandLineArgsCallbackAnnotation>(out var argsCallbackAnnotations));
        List<object> args = [];
        var ctx = new CommandLineArgsCallbackContext(args, appResource);

        foreach (var annotation in argsCallbackAnnotations)
        {
            annotation.Callback(ctx);
        }

        Assert.Collection(args,
            arg => Assert.Equal("--filter", arg),
            arg => Assert.Equal("project1", arg),
            arg => Assert.Equal("run", arg),
            arg => Assert.Equal("dev", arg)
        );
    }

    [Fact]
    public void WorkspaceAppWaitsForWorkspaceInstaller()
    {
        var builder = DistributedApplication.CreateBuilder();

        var workspace = builder.AddYarnWorkspace("yarn", "yarn-workspace");
        workspace.AddJavaScriptApp("app1", "project1");

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
        workspace.AddJavaScriptApp("app1", "project1");

        using var app = builder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        // Only the workspace-level installer should exist, not a per-app installer
        var installers = appModel.Resources.OfType<JavaScriptInstallerResource>().ToList();
        Assert.Single(installers);
        Assert.Equal("yarn-installer", installers[0].Name);
    }

    [Fact]
    public void YarnWorkspaceGetCommandPrefixReturnsCorrectPrefix()
    {
        var workspace = new YarnWorkspaceResource("test", "/tmp");
        Assert.Equal(["workspace", "my-app"], workspace.GetCommandPrefix("my-app"));
    }

    [Fact]
    public void PnpmWorkspaceGetCommandPrefixReturnsCorrectPrefix()
    {
        var workspace = new PnpmWorkspaceResource("test", "/tmp");
        Assert.Equal(["--filter", "my-app"], workspace.GetCommandPrefix("my-app"));
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
        AssertWorkspaceAppWiring(appResource, workspaceResource, "project1", ["workspace", "project1"], "packages/web");
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
        AssertWorkspaceAppWiring(appResource, workspaceResource, "project1", ["--filter", "project1"], "packages/web");
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
        AssertWorkspaceAppWiring(appResource, workspaceResource, "project1", ["workspace", "project1"], "apps/web");
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
        AssertWorkspaceAppWiring(appResource, workspaceResource, "project1", ["--filter", "project1"], "apps/web");
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
        AssertWorkspaceAppWiring(appResource, workspaceResource, "project1", ["workspace", "project1"], "apps/api");
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
        AssertWorkspaceAppWiring(appResource, workspaceResource, "project1", ["--filter", "project1"], "apps/api");
    }

    private static void AssertWorkspaceAppWiring(
        JavaScriptAppResource appResource,
        JavaScriptWorkspaceResource workspaceResource,
        string workspaceProjectName,
        string[] expectedCommandPrefix,
        string? expectedPackagePath = null)
    {
        // Workspace context annotation present and points at the right workspace/project
        Assert.True(appResource.TryGetLastAnnotation<JavaScriptWorkspaceContextAnnotation>(out var wsCtx));
        Assert.Same(workspaceResource, wsCtx.Workspace);
        Assert.Equal(workspaceProjectName, wsCtx.WorkspaceProjectName);
        Assert.Equal(expectedCommandPrefix, wsCtx.CommandPrefix);

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
