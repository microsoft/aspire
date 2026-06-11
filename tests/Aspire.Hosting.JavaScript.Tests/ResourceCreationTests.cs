// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.JavaScript.Tests;

public class ResourceCreationTests
{
    [Fact]
    public void DefaultViteAppUsesNpm()
    {
        var builder = DistributedApplication.CreateBuilder();

        builder.AddViteApp("vite", "vite");

        using var app = builder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var resource = appModel.Resources.OfType<JavaScriptAppResource>().SingleOrDefault();

        Assert.NotNull(resource);

        Assert.Equal("npm", resource.Command);
    }

    [Fact]
    public void ViteAppUsesSpecifiedWorkingDirectory()
    {
        var builder = DistributedApplication.CreateBuilder();

        builder.AddViteApp("vite", "test");

        using var app = builder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var resource = appModel.Resources.OfType<JavaScriptAppResource>().SingleOrDefault();

        Assert.NotNull(resource);

        Assert.Equal(Path.Combine(builder.AppHostDirectory, "test"), resource.WorkingDirectory);
    }

    [Fact]
    public void ViteAppHasExposedHttpEndpoints()
    {
        var builder = DistributedApplication.CreateBuilder();

        builder.AddViteApp("vite", "vite");

        using var app = builder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var resource = appModel.Resources.OfType<JavaScriptAppResource>().SingleOrDefault();

        Assert.NotNull(resource);

        Assert.True(resource.TryGetAnnotationsOfType<EndpointAnnotation>(out var endpoints));

        Assert.Contains(endpoints, e => e.UriScheme == "http");
    }

    [Fact]
    public void ViteAppDoesNotExposeExternalHttpEndpointsByDefault()
    {
        var builder = DistributedApplication.CreateBuilder();

        builder.AddViteApp("vite", "vite");

        using var app = builder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var resource = appModel.Resources.OfType<JavaScriptAppResource>().SingleOrDefault();

        Assert.NotNull(resource);

        Assert.True(resource.TryGetAnnotationsOfType<EndpointAnnotation>(out var endpoints));

        Assert.DoesNotContain(endpoints, e => e.IsExternal);
    }

    [Fact]
    public void WithNpmDefaultsToInstallCommand()
    {
        var builder = DistributedApplication.CreateBuilder();

        var nodeApp = builder.AddJavaScriptApp("test-app", "./test-app");

        // Add package installation with default settings (should use npm install)
        nodeApp.WithNpm(install: true);

        using var app = builder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        // Verify the NodeApp resource exists
        var nodeResource = Assert.Single(appModel.Resources.OfType<JavaScriptAppResource>());
        Assert.Equal("npm", nodeResource.Command);

        // Verify the installer resource was created
        var installerResource = Assert.Single(appModel.Resources.OfType<JavaScriptInstallerResource>());
        Assert.Equal("test-app-installer", installerResource.Name);

        // Verify the install command annotation
        Assert.True(nodeResource.TryGetLastAnnotation<JavaScriptPackageManagerAnnotation>(out var packageManager));
        Assert.Equal("npm", packageManager.ExecutableName);
        Assert.True(nodeResource.TryGetLastAnnotation<JavaScriptInstallCommandAnnotation>(out var installAnnotation));
        Assert.Equal(["install"], installAnnotation.Args);

        // Verify the parent-child relationship
        Assert.True(installerResource.TryGetAnnotationsOfType<ResourceRelationshipAnnotation>(out var relationships));
        var relationship = Assert.Single(relationships);
        Assert.Same(nodeResource, relationship.Resource);
        Assert.Equal("Parent", relationship.Type);

        // Verify the wait annotation on the parent
        Assert.True(nodeResource.TryGetAnnotationsOfType<WaitAnnotation>(out var waitAnnotations));
        var waitAnnotation = Assert.Single(waitAnnotations);
        Assert.Same(installerResource, waitAnnotation.Resource);
    }

    [Fact]
    public void ViteAppConfiguresPortFromEnvironment()
    {
        var builder = DistributedApplication.CreateBuilder();

        builder.AddViteApp("vite", "vite");

        using var app = builder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var resource = Assert.Single(appModel.Resources.OfType<JavaScriptAppResource>());

        // Verify that command line arguments callback is configured
        Assert.True(resource.TryGetAnnotationsOfType<CommandLineArgsCallbackAnnotation>(out var argsCallbackAnnotations));
        List<object> args = [];
        var ctx = new CommandLineArgsCallbackContext(args, resource);

        foreach (var annotation in argsCallbackAnnotations)
        {
            annotation.Callback(ctx);
        }

        Assert.Collection(args,
            arg => Assert.Equal("run", arg),
            arg => Assert.Equal("dev", arg),
            arg => Assert.Equal("--", arg),
            arg => Assert.Equal("--port", arg),
            arg => Assert.IsType<EndpointReferenceExpression>(arg)
        );
    }

    [Fact]
    public void WithNpmInstallFalseDoesNotCreateInstaller()
    {
        var builder = DistributedApplication.CreateBuilder();

        var nodeApp = builder.AddJavaScriptApp("test-app", "./test-app");

        // Configure npm without installing packages
        nodeApp.WithNpm(install: false);

        using var app = builder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        // Verify the NodeApp resource exists with npm command
        var nodeResource = Assert.Single(appModel.Resources.OfType<JavaScriptAppResource>());
        Assert.Equal("npm", nodeResource.Command);

        // Verify the package manager annotations are set
        Assert.True(nodeResource.TryGetLastAnnotation<JavaScriptInstallCommandAnnotation>(out var _));
        Assert.True(nodeResource.TryGetLastAnnotation<JavaScriptRunScriptAnnotation>(out var _));
        Assert.True(nodeResource.TryGetLastAnnotation<JavaScriptBuildScriptAnnotation>(out var _));

        // Verify installer resource WAS created (new behavior)
        var installerResource = Assert.Single(appModel.Resources.OfType<JavaScriptInstallerResource>());
        Assert.Equal("test-app-installer", installerResource.Name);

        // Verify it has explicit start annotation
        Assert.True(installerResource.TryGetLastAnnotation<ExplicitStartupAnnotation>(out _));

        // Verify no wait annotations were added to the app
        Assert.False(nodeResource.TryGetAnnotationsOfType<WaitAnnotation>(out _));
    }

    [Fact]
    public void InstallerResourceHasCertificateTrustScopeNone()
    {
        var builder = DistributedApplication.CreateBuilder();

        var nodeApp = builder.AddJavaScriptApp("test-app", "./test-app");
        nodeApp.WithNpm(install: true);

        using var app = builder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        // Verify the installer resource was created
        var installerResource = Assert.Single(appModel.Resources.OfType<JavaScriptInstallerResource>());
        Assert.Equal("test-app-installer", installerResource.Name);

        // Verify the installer has CertificateTrustScope.None
        Assert.True(installerResource.TryGetLastAnnotation<CertificateAuthorityCollectionAnnotation>(out var certAnnotation));
        Assert.Equal(CertificateTrustScope.None, certAnnotation.Scope);
    }
}
