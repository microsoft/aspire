// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.JavaScript.Tests;

public class PackageInstallationTests
{
    /// <summary>
    /// This test validates that the WithNpm method creates
    /// installer resources with proper arguments and relationships.
    /// </summary>
    [Fact]
    public void WithNpm_CanBeConfiguredWithInstall()
    {
        var builder = DistributedApplication.CreateBuilder();

        var nodeApp = builder.AddJavaScriptApp("nodeApp", "./test-app");
        var nodeApp2 = builder.AddJavaScriptApp("nodeApp2", "./test-app-2");

        // Test that both configurations can be set up without errors
        nodeApp.WithNpm(install: true); // Uses npm install
        nodeApp2.WithNpm(install: false);

        using var app = builder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var nodeResources = appModel.Resources.OfType<JavaScriptAppResource>().ToList();
        var installerResources = appModel.Resources.OfType<JavaScriptInstallerResource>().ToList();

        Assert.Equal(2, nodeResources.Count);
        // Both should have installer resources now
        Assert.Equal(2, installerResources.Count);
        Assert.All(nodeResources, resource => Assert.Equal("npm", resource.Command));

        // Verify the installer exists for nodeApp
        var nodeAppInstallResource = installerResources.Single(r => r.Name == "nodeApp-installer");
        Assert.NotNull(nodeAppInstallResource);
        // Should NOT have explicit start annotation
        Assert.False(nodeAppInstallResource.TryGetLastAnnotation<ExplicitStartupAnnotation>(out _));

        // Verify installer exists for nodeApp2 but with explicit start
        var nodeApp2InstallResource = installerResources.Single(r => r.Name == "nodeApp2-installer");
        Assert.NotNull(nodeApp2InstallResource);
        // Should have explicit start annotation
        Assert.True(nodeApp2InstallResource.TryGetLastAnnotation<ExplicitStartupAnnotation>(out _));
    }

    [Fact]
    public void WithNpm_ExcludedFromPublishMode()
    {
        var builder = DistributedApplication.CreateBuilder(["Publishing:Publisher=manifest", "Publishing:OutputPath=./publish"]);

        var nodeApp = builder.AddJavaScriptApp("test-app", "./test-app");
        nodeApp.WithNpm(install: true);

        using var app = builder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        // Verify the JavaScriptApp resource exists
        var nodeResource = Assert.Single(appModel.Resources, r => r.Name == "test-app");

        // Verify NO installer resource was created in publish mode
        var installerResources = appModel.Resources.OfType<JavaScriptInstallerResource>().ToList();
        Assert.Empty(installerResources);

        // Verify no wait annotations were added
        Assert.False(nodeResource.TryGetAnnotationsOfType<WaitAnnotation>(out _));
    }

    [Fact]
    public async Task WithYarn_CreatesInstallerWhenInstallIsTrue()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);

        var nodeApp = builder.AddJavaScriptApp("test-app", "./test-app");
        nodeApp.WithYarn(install: true);

        using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, CancellationToken.None);

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        // Verify the JavaScriptApp resource exists with yarn command
        var nodeResource = Assert.Single(appModel.Resources.OfType<JavaScriptAppResource>());
        Assert.Equal("yarn", nodeResource.Command);

        // Verify the package manager annotation
        Assert.True(nodeResource.TryGetLastAnnotation<JavaScriptPackageManagerAnnotation>(out var packageManager));
        Assert.Equal("yarn", packageManager.ExecutableName);
        Assert.Equal("run", packageManager.ScriptCommand);

        // Verify the install command annotation
        Assert.True(nodeResource.TryGetLastAnnotation<JavaScriptInstallCommandAnnotation>(out var installAnnotation));
        Assert.Equal(["install"], installAnnotation.Args);

        // Verify the run command annotation
        Assert.True(nodeResource.TryGetLastAnnotation<JavaScriptRunScriptAnnotation>(out var runAnnotation));
        Assert.Equal("dev", runAnnotation.ScriptName);

        // Verify the build command annotation
        Assert.True(nodeResource.TryGetLastAnnotation<JavaScriptBuildScriptAnnotation>(out var buildAnnotation));
        Assert.Equal("build", buildAnnotation.ScriptName);

        // Verify the installer resource was created
        var installerResource = Assert.Single(appModel.Resources.OfType<JavaScriptInstallerResource>());
        Assert.Equal("test-app-installer", installerResource.Name);
    }

    [Fact]
    public async Task WithYarn_DoesNotCreateInstallerWhenInstallIsFalse()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);

        var nodeApp = builder.AddJavaScriptApp("test-app", "./test-app");
        nodeApp.WithYarn(install: false);

        using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, CancellationToken.None);

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        // Verify the JavaScriptApp resource exists with yarn command
        var nodeResource = Assert.Single(appModel.Resources.OfType<JavaScriptAppResource>());
        Assert.Equal("yarn", nodeResource.Command);

        // Verify annotations are set
        Assert.True(nodeResource.TryGetLastAnnotation<JavaScriptInstallCommandAnnotation>(out var _));
        Assert.True(nodeResource.TryGetLastAnnotation<JavaScriptRunScriptAnnotation>(out var _));
        Assert.True(nodeResource.TryGetLastAnnotation<JavaScriptBuildScriptAnnotation>(out var _));

        // Verify installer resource WAS created (new behavior)
        var installerResource = Assert.Single(appModel.Resources.OfType<JavaScriptInstallerResource>());
        Assert.Equal("test-app-installer", installerResource.Name);
        
        // Verify it has explicit start annotation
        Assert.True(installerResource.TryGetLastAnnotation<ExplicitStartupAnnotation>(out _));
    }

    [Fact]
    public async Task WithPnpm_CreatesInstallerWhenInstallIsTrue()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);

        var nodeApp = builder.AddJavaScriptApp("test-app", "./test-app");
        nodeApp.WithPnpm(install: true);

        using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, CancellationToken.None);

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        // Verify the JavaScriptApp resource exists with pnpm command
        var nodeResource = Assert.Single(appModel.Resources.OfType<JavaScriptAppResource>());
        Assert.Equal("pnpm", nodeResource.Command);

        Assert.True(nodeResource.TryGetLastAnnotation<JavaScriptPackageManagerAnnotation>(out var packageManager));
        Assert.Equal("pnpm", packageManager.ExecutableName);
        Assert.Equal("run", packageManager.ScriptCommand);

        // Verify the install command annotation
        Assert.True(nodeResource.TryGetLastAnnotation<JavaScriptInstallCommandAnnotation>(out var installAnnotation));
        Assert.Equal(["install"], installAnnotation.Args);

        // Verify the run command annotation
        Assert.True(nodeResource.TryGetLastAnnotation<JavaScriptRunScriptAnnotation>(out var runAnnotation));
        Assert.Equal("dev", runAnnotation.ScriptName);

        // Verify the build command annotation
        Assert.True(nodeResource.TryGetLastAnnotation<JavaScriptBuildScriptAnnotation>(out var buildAnnotation));
        Assert.Equal("build", buildAnnotation.ScriptName);
        Assert.Empty(buildAnnotation.Args);

        // Verify the installer resource was created
        var installerResource = Assert.Single(appModel.Resources.OfType<JavaScriptInstallerResource>());
        Assert.Equal("test-app-installer", installerResource.Name);
    }

    [Fact]
    public async Task WithPnpm_DoesNotCreateInstallerWhenInstallIsFalse()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);

        var nodeApp = builder.AddJavaScriptApp("test-app", "./test-app");
        nodeApp.WithPnpm(install: false);

        using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, CancellationToken.None);

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        // Verify the JavaScriptApp resource exists with pnpm command
        var nodeResource = Assert.Single(appModel.Resources.OfType<JavaScriptAppResource>());
        Assert.Equal("pnpm", nodeResource.Command);

        // Verify annotations are set
        Assert.True(nodeResource.TryGetLastAnnotation<JavaScriptInstallCommandAnnotation>(out var _));
        Assert.True(nodeResource.TryGetLastAnnotation<JavaScriptRunScriptAnnotation>(out var _));
        Assert.True(nodeResource.TryGetLastAnnotation<JavaScriptBuildScriptAnnotation>(out var _));

        // Verify installer resource WAS created (new behavior)
        var installerResource = Assert.Single(appModel.Resources.OfType<JavaScriptInstallerResource>());
        Assert.Equal("test-app-installer", installerResource.Name);
        
        // Verify it has explicit start annotation
        Assert.True(installerResource.TryGetLastAnnotation<ExplicitStartupAnnotation>(out _));
    }

    [Fact]
    public void WithNpm_CreatesInstallerWithCustomCommand()
    {
        var builder = DistributedApplication.CreateBuilder();

        var nodeApp = builder.AddJavaScriptApp("test-app", "./test-app");
        nodeApp.WithNpm(installCommand: "ci", installArgs: ["--no-fund"]);

        using var app = builder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        // Verify the JavaScriptApp resource exists
        var nodeResource = Assert.Single(appModel.Resources.OfType<JavaScriptAppResource>());

        // Verify the install command annotation with custom command
        Assert.True(nodeResource.TryGetLastAnnotation<JavaScriptInstallCommandAnnotation>(out var installAnnotation));
        Assert.Equal(["ci", "--no-fund"], installAnnotation.Args);

        // Verify the installer resource was created
        var installerResource = Assert.Single(appModel.Resources.OfType<JavaScriptInstallerResource>());
        Assert.Equal("test-app-installer", installerResource.Name);
    }

    [Fact]
    public void WithBuildScript_SetsCustomBuildCommand()
    {
        var builder = DistributedApplication.CreateBuilder();

        var nodeApp = builder.AddJavaScriptApp("test-app", "./test-app");
        nodeApp.WithBuildScript("bun", ["run", "build:prod"]);

        using var app = builder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        // Verify the JavaScriptApp resource exists
        var nodeResource = Assert.Single(appModel.Resources.OfType<JavaScriptAppResource>());

        // Verify the build command annotation with custom command
        Assert.True(nodeResource.TryGetLastAnnotation<JavaScriptBuildScriptAnnotation>(out var buildAnnotation));
        Assert.Equal("bun", buildAnnotation.ScriptName);
        Assert.Equal(["run", "build:prod"], buildAnnotation.Args);
    }

    [Fact]
    public async Task WithRunScript_SetsCustomRunCommand()
    {
        var builder = DistributedApplication.CreateBuilder();

        var nodeApp = builder.AddJavaScriptApp("test-app", "./test-app");
        nodeApp.WithRunScript("start", ["--my-args"]);

        using var app = builder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        // Verify the JavaScriptApp resource exists
        var nodeResource = Assert.Single(appModel.Resources.OfType<JavaScriptAppResource>());

        var args = await ArgumentEvaluator.GetArgumentListAsync(nodeResource);

        Assert.Collection(args,
            arg => Assert.Equal("run", arg),
            arg => Assert.Equal("start", arg),
            arg => Assert.Equal("--my-args", arg));
    }

    [Fact]
    public void WithNpmInstallWithYarnNoInstall()
    {
        var builder = DistributedApplication.CreateBuilder();

        builder.AddViteApp("test-app", "./test-app")
            .WithNpm(install: true)
            .WithYarn(install: false);

        using var app = builder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        // Verify the JavaScriptApp resource exists
        var nodeResource = Assert.Single(appModel.Resources.OfType<JavaScriptAppResource>());

        Assert.True(nodeResource.TryGetLastAnnotation<JavaScriptPackageManagerAnnotation>(out var packageManager));
        Assert.Equal("yarn", packageManager.ExecutableName);

        // Verify the install command annotation is correct - it should still be there
        Assert.True(nodeResource.TryGetLastAnnotation<JavaScriptInstallCommandAnnotation>(out var installAnnotation));
        Assert.Equal(["install"], installAnnotation.Args);

        // the installer resource SHOULD be created (new behavior)
        var installerResource = Assert.Single(appModel.Resources.OfType<JavaScriptInstallerResource>());
        
        // Verify it has explicit start annotation
        Assert.True(installerResource.TryGetLastAnnotation<ExplicitStartupAnnotation>(out _));
    }

    [Fact]
    public void WithNpmNoInstallWithYarnInstall()
    {
        var builder = DistributedApplication.CreateBuilder();

        builder.AddViteApp("test-app", "./test-app")
            .WithNpm(install: false)
            .WithYarn(install: true);

        using var app = builder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        // Verify the JavaScriptApp resource exists
        var nodeResource = Assert.Single(appModel.Resources.OfType<JavaScriptAppResource>());

        Assert.True(nodeResource.TryGetLastAnnotation<JavaScriptPackageManagerAnnotation>(out var packageManager));
        Assert.Equal("yarn", packageManager.ExecutableName);

        // Verify the install command annotation is correct - it should still be there
        Assert.True(nodeResource.TryGetLastAnnotation<JavaScriptInstallCommandAnnotation>(out var installAnnotation));
        Assert.Equal(["install"], installAnnotation.Args);

        // the installer resource should be created
        Assert.Single(appModel.Resources.OfType<JavaScriptInstallerResource>());
    }

    [Fact]
    public async Task WithNpmInstallWithYarnInstall()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);

        builder.AddViteApp("test-app", "./test-app")
            .WithNpm(install: true)
            .WithYarn(install: true);

        using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, CancellationToken.None);

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        // Verify the JavaScriptApp resource exists
        var nodeResource = Assert.Single(appModel.Resources.OfType<JavaScriptAppResource>());

        Assert.True(nodeResource.TryGetLastAnnotation<JavaScriptPackageManagerAnnotation>(out var packageManager));
        Assert.Equal("yarn", packageManager.ExecutableName);

        // Verify the install command annotation is correct - it should still be there
        Assert.True(nodeResource.TryGetLastAnnotation<JavaScriptInstallCommandAnnotation>(out var installAnnotation));
        Assert.Equal(["install"], installAnnotation.Args);

        // a single installer resource should be created
        var installer = Assert.Single(appModel.Resources.OfType<JavaScriptInstallerResource>());
        Assert.Equal("yarn", installer.Command);
    }

    [Fact]
    public void WithNpm_DefaultInstallsPackages()
    {
        var builder = DistributedApplication.CreateBuilder();

        var nodeApp = builder.AddJavaScriptApp("test-app", "./test-app");
        nodeApp.WithNpm(); // Using default parameter (should be install: true)

        using var app = builder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        // Verify the JavaScriptApp resource exists with npm command
        var nodeResource = Assert.Single(appModel.Resources.OfType<JavaScriptAppResource>());
        Assert.Equal("npm", nodeResource.Command);

        // Verify the installer resource was created by default
        var installerResource = Assert.Single(appModel.Resources.OfType<JavaScriptInstallerResource>());
        Assert.Equal("test-app-installer", installerResource.Name);
    }

    [Fact]
    public async Task WithYarn_DefaultInstallsPackages()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);

        var nodeApp = builder.AddJavaScriptApp("test-app", "./test-app");
        nodeApp.WithYarn(); // Using default parameter (should be install: true)

        using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, CancellationToken.None);

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        // Verify the JavaScriptApp resource exists with yarn command
        var nodeResource = Assert.Single(appModel.Resources.OfType<JavaScriptAppResource>());
        Assert.Equal("yarn", nodeResource.Command);

        // Verify the installer resource was created by default
        var installerResource = Assert.Single(appModel.Resources.OfType<JavaScriptInstallerResource>());
        Assert.Equal("test-app-installer", installerResource.Name);
    }

    [Fact]
    public async Task WithPnpm_DefaultInstallsPackages()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);

        var nodeApp = builder.AddJavaScriptApp("test-app", "./test-app");
        nodeApp.WithPnpm(); // Using default parameter (should be install: true)

        using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, CancellationToken.None);

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        // Verify the JavaScriptApp resource exists with pnpm command
        var nodeResource = Assert.Single(appModel.Resources.OfType<JavaScriptAppResource>());
        Assert.Equal("pnpm", nodeResource.Command);

        // Verify the installer resource was created by default
        var installerResource = Assert.Single(appModel.Resources.OfType<JavaScriptInstallerResource>());
        Assert.Equal("test-app-installer", installerResource.Name);
    }

    [Fact]
    public void AddViteApp_DefaultInstallsPackages()
    {
        var builder = DistributedApplication.CreateBuilder();

        builder.AddViteApp("test-app", "./test-app");

        using var app = builder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        // Verify the JavaScriptApp resource exists with npm command (default package manager)
        var nodeResource = Assert.Single(appModel.Resources.OfType<JavaScriptAppResource>());
        Assert.Equal("npm", nodeResource.Command);

        // Verify the installer resource was created by default for ViteApp
        var installerResource = Assert.Single(appModel.Resources.OfType<JavaScriptInstallerResource>());
        Assert.Equal("test-app-installer", installerResource.Name);
    }

    [Fact]
    public void WithNpm_DefaultsArgsInPublishMode()
    {
        using var tempDir = new TestTempDirectory();
        File.WriteAllText(Path.Combine(tempDir.Path, "package-lock.json"), "empty");

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var app = builder.AddViteApp("test-app", tempDir.Path)
            .WithNpm();

        Assert.True(app.Resource.TryGetLastAnnotation<JavaScriptInstallCommandAnnotation>(out var installCommand));
        Assert.Equal(["ci"], installCommand.Args);
    }

    [Fact]
    public void WithNpm_CanChangeInstallCommandAndArgs()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var app = builder.AddViteApp("test-app", "./test-app")
            .WithNpm(installCommand: "myinstall", installArgs: ["--no-fund"]);

        Assert.True(app.Resource.TryGetLastAnnotation<JavaScriptInstallCommandAnnotation>(out var installCommand));
        Assert.Equal(["myinstall", "--no-fund"], installCommand.Args);
    }

    [Fact]
    public void WithYarn_DefaultsArgsInPublishMode()
    {
        using var tempDir = new TestTempDirectory();
        File.WriteAllText(Path.Combine(tempDir.Path, "yarn.lock"), "empty");

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var app = builder.AddViteApp("test-app", tempDir.Path)
            .WithYarn();

        Assert.True(app.Resource.TryGetLastAnnotation<JavaScriptInstallCommandAnnotation>(out var installCommand));
        Assert.Equal(["install", "--frozen-lockfile"], installCommand.Args);

        var app2 = builder.AddViteApp("test-app2", tempDir.Path)
            .WithYarn(installArgs: ["--immutable-cache"]);

        Assert.True(app2.Resource.TryGetLastAnnotation<JavaScriptInstallCommandAnnotation>(out installCommand));
        Assert.Equal(["install", "--immutable-cache"], installCommand.Args);
    }

    [Fact]
    public void WithYarn_ReturnsImmutable_WhenYarnRcYmlExists()
    {
        using var tempDir = new TestTempDirectory();
        File.WriteAllText(Path.Combine(tempDir.Path, "yarn.lock"), "empty");
        File.WriteAllText(Path.Combine(tempDir.Path, ".yarnrc.yml"), "empty");

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var app = builder.AddViteApp("test-app", tempDir.Path)
            .WithYarn();

        Assert.True(app.Resource.TryGetLastAnnotation<JavaScriptInstallCommandAnnotation>(out var installCommand));
        Assert.Equal(["install", "--immutable"], installCommand.Args);
    }

    [Fact]
    public void WithYarn_ReturnsImmutable_WhenYarnReleasesDirExists()
    {
        using var tempDir = new TestTempDirectory();
        File.WriteAllText(Path.Combine(tempDir.Path, "yarn.lock"), "empty");
        Directory.CreateDirectory(Path.Combine(tempDir.Path, ".yarn", "releases"));

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var app = builder.AddViteApp("test-app", tempDir.Path)
            .WithYarn();

        Assert.True(app.Resource.TryGetLastAnnotation<JavaScriptInstallCommandAnnotation>(out var installCommand));
        Assert.Equal(["install", "--immutable"], installCommand.Args);
    }

    [Fact]
    public void WithPnpm_DefaultsArgsInPublishMode()
    {
        using var tempDir = new TestTempDirectory();
        File.WriteAllText(Path.Combine(tempDir.Path, "pnpm-lock.yaml"), "empty");

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var app = builder.AddViteApp("test-app", tempDir.Path)
            .WithPnpm();

        Assert.True(app.Resource.TryGetLastAnnotation<JavaScriptInstallCommandAnnotation>(out var installCommand));
        Assert.Equal(["install", "--frozen-lockfile"], installCommand.Args);
    }

    [Fact]
    public void WithBun_DefaultsArgsInPublishMode()
    {
        using var tempDir = new TestTempDirectory();
        File.WriteAllText(Path.Combine(tempDir.Path, "bun.lock"), "empty");

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var app = builder.AddViteApp("test-app", tempDir.Path)
            .WithBun();

        Assert.True(app.Resource.TryGetLastAnnotation<JavaScriptInstallCommandAnnotation>(out var installCommand));
        Assert.Equal(["install", "--frozen-lockfile"], installCommand.Args);
    }

    [Fact]
    public void InstallerResourceHasNameValidationPolicyAnnotation()
    {
        var builder = DistributedApplication.CreateBuilder();

        builder.AddJavaScriptApp("nodeApp", "./test-app")
            .WithNpm(install: true);

        using var app = builder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var installerResource = Assert.Single(appModel.Resources.OfType<JavaScriptInstallerResource>());
        Assert.True(installerResource.TryGetLastAnnotation<NameValidationPolicyAnnotation>(out var policy));
        Assert.Same(NameValidationPolicyAnnotation.None, policy);
    }

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "ExecuteBeforeStartHooksAsync")]
    private static extern Task ExecuteBeforeStartHooksAsync(DistributedApplication app, CancellationToken cancellationToken);
}
