// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.JavaScript.Tests;

public class AddViteAppWithPnpmTests
{
    [Fact]
    public void AddViteApp_WithPnpm_DoesNotIncludeSeparator()
    {
        var builder = DistributedApplication.CreateBuilder();

        var viteApp = builder.AddViteApp("test-app", "./test-app")
            .WithPnpm();

        using var app = builder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        // Verify the JavaScriptApp resource exists with pnpm command
        var nodeResource = Assert.Single(appModel.Resources.OfType<JavaScriptAppResource>());

        Assert.True(nodeResource.TryGetLastAnnotation<JavaScriptPackageManagerAnnotation>(out var packageManager));
        Assert.Equal("pnpm", packageManager.ExecutableName);
        Assert.Equal("run", packageManager.ScriptCommand);

        // Get the first command line args annotation (WithDebugSupport adds additional callbacks)
        var commandLineArgsAnnotation = nodeResource.Annotations.OfType<CommandLineArgsCallbackAnnotation>().First();
        var args = new List<object>();
        var context = new CommandLineArgsCallbackContext(args, nodeResource);
        commandLineArgsAnnotation.Callback(context);

        // Should be: ["run", "dev", "--port", "{port}"]
        // NOT: ["run", "dev", "--", "--port", "{port}"]
        // pnpm does not strip the -- separator, so we don't include it
        Assert.Collection(args,
            arg => Assert.Equal("run", arg),
            arg => Assert.Equal("dev", arg),
            arg => Assert.Equal("--port", arg),
            arg => { }); // port value is dynamic
    }

    [Fact]
    public void AddViteApp_WithBun_DoesNotIncludeSeparator()
    {
        var builder = DistributedApplication.CreateBuilder();

        var viteApp = builder.AddViteApp("test-app", "./test-app")
            .WithBun();

        using var app = builder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var nodeResource = Assert.Single(appModel.Resources.OfType<JavaScriptAppResource>());

        Assert.True(nodeResource.TryGetLastAnnotation<JavaScriptPackageManagerAnnotation>(out var packageManager));
        Assert.Equal("bun", packageManager.ExecutableName);
        Assert.Equal("run", packageManager.ScriptCommand);

        // Get the first command line args annotation (WithDebugSupport adds additional callbacks)
        var commandLineArgsAnnotation = nodeResource.Annotations.OfType<CommandLineArgsCallbackAnnotation>().First();
        var args = new List<object>();
        var context = new CommandLineArgsCallbackContext(args, nodeResource);
        commandLineArgsAnnotation.Callback(context);

        // bun supports passing script flags without the `--` separator.
        Assert.Collection(args,
            arg => Assert.Equal("run", arg),
            arg => Assert.Equal("dev", arg),
            arg => Assert.Equal("--port", arg),
            arg => { }); // port value is dynamic
    }

    [Fact]
    public void AddViteApp_WithNpm_IncludesSeparator()
    {
        var builder = DistributedApplication.CreateBuilder();

        var viteApp = builder.AddViteApp("test-app", "./test-app");

        using var app = builder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var nodeResource = Assert.Single(appModel.Resources.OfType<JavaScriptAppResource>());
        Assert.Equal("npm", nodeResource.Command);

        // Get the first command line args annotation (WithDebugSupport adds additional callbacks)
        var commandLineArgsAnnotation = nodeResource.Annotations.OfType<CommandLineArgsCallbackAnnotation>().First();
        var args = new List<object>();
        var context = new CommandLineArgsCallbackContext(args, nodeResource);
        commandLineArgsAnnotation.Callback(context);

        // Should be: ["run", "dev", "--", "--port", "{port}"]
        // npm strips the -- separator before passing to the script
        Assert.Collection(args,
            arg => Assert.Equal("run", arg),
            arg => Assert.Equal("dev", arg),
            arg => Assert.Equal("--", arg),
            arg => Assert.Equal("--port", arg),
            arg => { }); // port value is dynamic
    }

    [Fact]
    public void AddViteApp_WithYarn_IncludesSeparator()
    {
        var builder = DistributedApplication.CreateBuilder();

        var viteApp = builder.AddViteApp("test-app", "./test-app")
            .WithYarn();

        using var app = builder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var nodeResource = Assert.Single(appModel.Resources.OfType<JavaScriptAppResource>());
        Assert.True(nodeResource.TryGetLastAnnotation<JavaScriptPackageManagerAnnotation>(out var packageManager));
        Assert.Equal("yarn", packageManager.ExecutableName);
        Assert.Equal("run", packageManager.ScriptCommand);

        // Get the first command line args annotation (WithDebugSupport adds additional callbacks)
        var commandLineArgsAnnotation = nodeResource.Annotations.OfType<CommandLineArgsCallbackAnnotation>().First();
        var args = new List<object>();
        var context = new CommandLineArgsCallbackContext(args, nodeResource);
        commandLineArgsAnnotation.Callback(context);

        // Should be: ["run", "dev", "--port", "{port}"]
        // NOT: ["run", "dev", "--", "--port", "{port}"]
        // yarn does not strip the -- separator, so we don't include it
        Assert.Collection(args,
            arg => Assert.Equal("run", arg),
            arg => Assert.Equal("dev", arg),
            arg => Assert.Equal("--port", arg),
            arg => { }); // port value is dynamic
    }
}
