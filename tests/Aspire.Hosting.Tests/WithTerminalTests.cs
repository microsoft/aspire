// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Tests;

public class WithTerminalTests
{
    [Fact]
    public void WithTerminalAddsTerminalAnnotation()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var resource = builder.AddExecutable("myapp", "myapp", ".");

        resource.WithTerminal();

        var annotation = resource.Resource.Annotations.OfType<TerminalAnnotation>().SingleOrDefault();
        Assert.NotNull(annotation);
        Assert.Equal(120, annotation.Options.Columns);
        Assert.Equal(30, annotation.Options.Rows);
        Assert.Null(annotation.Options.Shell);
    }

    [Fact]
    public void WithTerminalAcceptsCustomOptions()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var resource = builder.AddExecutable("myapp", "myapp", ".");

        resource.WithTerminal(options =>
        {
            options.Columns = 200;
            options.Rows = 50;
            options.Shell = "/bin/bash";
        });

        var annotation = resource.Resource.Annotations.OfType<TerminalAnnotation>().SingleOrDefault();
        Assert.NotNull(annotation);
        Assert.Equal(200, annotation.Options.Columns);
        Assert.Equal(50, annotation.Options.Rows);
        Assert.Equal("/bin/bash", annotation.Options.Shell);
    }

    [Fact]
    public void WithTerminalCreatesHiddenTerminalHostResource()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var resource = builder.AddExecutable("myapp", "myapp", ".");

        resource.WithTerminal();

        var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var terminalHost = model.Resources.OfType<TerminalHostResource>().SingleOrDefault();
        Assert.NotNull(terminalHost);
        Assert.Equal("myapp-terminal-host", terminalHost.Name);
        Assert.Same(resource.Resource, terminalHost.Parent);
    }

    [Fact]
    public void WithTerminalAddsWaitAnnotationForWaitSupportedResources()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var resource = builder.AddExecutable("myapp", "myapp", ".");

        resource.WithTerminal();

        var waitAnnotations = resource.Resource.Annotations.OfType<WaitAnnotation>().ToList();
        var terminalWait = waitAnnotations.FirstOrDefault(w => w.Resource is TerminalHostResource);
        Assert.NotNull(terminalWait);
        Assert.Equal(WaitType.WaitUntilStarted, terminalWait.WaitType);
    }

    [Fact]
    public void WithTerminalCanBeChained()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var resource = builder.AddExecutable("myapp", "myapp", ".");

        var result = resource.WithTerminal();

        Assert.Same(resource, result);
    }

    [Fact]
    public void WithTerminalWorksOnContainerResources()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var container = builder.AddContainer("mycontainer", "myimage");

        container.WithTerminal();

        var annotation = container.Resource.Annotations.OfType<TerminalAnnotation>().SingleOrDefault();
        Assert.NotNull(annotation);

        var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var terminalHost = model.Resources.OfType<TerminalHostResource>().SingleOrDefault();
        Assert.NotNull(terminalHost);
        Assert.Equal("mycontainer-terminal-host", terminalHost.Name);
    }

    [Fact]
    public void TerminalHostResourceIsExcludedFromManifest()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var resource = builder.AddExecutable("myapp", "myapp", ".");

        resource.WithTerminal();

        var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var terminalHost = model.Resources.OfType<TerminalHostResource>().SingleOrDefault();
        Assert.NotNull(terminalHost);

        var manifestAnnotation = terminalHost.Annotations.OfType<ManifestPublishingCallbackAnnotation>().SingleOrDefault();
        Assert.NotNull(manifestAnnotation);
    }

    [Fact]
    public void WithTerminalThrowsForNullBuilder()
    {
        IResourceBuilder<ExecutableResource> builder = null!;

        Assert.Throws<ArgumentNullException>(() => builder.WithTerminal());
    }
}
