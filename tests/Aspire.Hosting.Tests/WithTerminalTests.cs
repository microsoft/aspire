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
        Assert.NotNull(annotation.TerminalHost);
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
        Assert.Equal("myapp-terminalhost", terminalHost.Name);
        Assert.Same(resource.Resource, terminalHost.Parent);
    }

    [Fact]
    public void WithTerminalLinksAnnotationToHostResource()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var resource = builder.AddExecutable("myapp", "myapp", ".");

        resource.WithTerminal();

        var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var annotation = resource.Resource.Annotations.OfType<TerminalAnnotation>().Single();
        var hostFromModel = model.Resources.OfType<TerminalHostResource>().Single();
        Assert.Same(hostFromModel, annotation.TerminalHost);
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
        Assert.Equal("mycontainer-terminalhost", terminalHost.Name);
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

    [Fact]
    public void WithTerminalThrowsWhenCalledTwiceOnSameResource()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var resource = builder.AddExecutable("myapp", "myapp", ".");

        resource.WithTerminal();

        Assert.Throws<InvalidOperationException>(() => resource.WithTerminal());
    }

    [Fact]
    public void WithTerminalDefaultsToOneReplicaWorthOfPaths()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var resource = builder.AddExecutable("myapp", "myapp", ".");

        resource.WithTerminal();

        var host = resource.Resource.Annotations.OfType<TerminalAnnotation>().Single().TerminalHost;
        Assert.Equal(1, host.Layout.ReplicaCount);
        Assert.Single(host.Layout.ProducerUdsPaths);
        Assert.Single(host.Layout.ConsumerUdsPaths);
        Assert.NotEmpty(host.Layout.ControlUdsPath);
    }

    [Fact]
    public void WithTerminalAfterWithReplicasCreatesPathPerReplica()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var resource = builder.AddExecutable("myapp", "myapp", ".")
            .WithAnnotation(new ReplicaAnnotation(3));

        resource.WithTerminal();

        var host = resource.Resource.Annotations.OfType<TerminalAnnotation>().Single().TerminalHost;
        Assert.Equal(3, host.Layout.ReplicaCount);
        Assert.Equal(3, host.Layout.ProducerUdsPaths.Count);
        Assert.Equal(3, host.Layout.ConsumerUdsPaths.Count);

        for (var i = 0; i < 3; i++)
        {
            Assert.EndsWith($"r{i}.sock", host.Layout.ProducerUdsPaths[i]);
            Assert.EndsWith($"r{i}.sock", host.Layout.ConsumerUdsPaths[i]);
        }
    }

    [Fact]
    public void TerminalHostLayoutPathsAreUnderTheSameTempBaseDirectory()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var resource = builder.AddExecutable("myapp", "myapp", ".")
            .WithAnnotation(new ReplicaAnnotation(2));

        resource.WithTerminal();

        var layout = resource.Resource.Annotations.OfType<TerminalAnnotation>().Single().TerminalHost.Layout;

        foreach (var path in layout.ProducerUdsPaths)
        {
            Assert.StartsWith(layout.BaseDirectory, path);
        }

        foreach (var path in layout.ConsumerUdsPaths)
        {
            Assert.StartsWith(layout.BaseDirectory, path);
        }

        Assert.StartsWith(layout.BaseDirectory, layout.ControlUdsPath);
    }

    [Fact]
    public async Task TerminalHostHasCommandLineArgsForLayoutPaths()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var resource = builder.AddExecutable("myapp", "myapp", ".")
            .WithAnnotation(new ReplicaAnnotation(2));

        resource.WithTerminal(options =>
        {
            options.Columns = 200;
            options.Rows = 50;
            options.Shell = "/bin/bash";
        });

        var host = resource.Resource.Annotations.OfType<TerminalAnnotation>().Single().TerminalHost;
        var args = await GetResolvedCommandLineArgsAsync(host);

        Assert.Contains("--replica-count", args);
        Assert.Contains("2", args);

        Assert.Equal(2, args.Count(a => a == "--producer-uds"));
        Assert.Equal(2, args.Count(a => a == "--consumer-uds"));
        Assert.Single(args, a => a == "--control-uds");

        foreach (var path in host.Layout.ProducerUdsPaths)
        {
            Assert.Contains(path, args);
        }

        foreach (var path in host.Layout.ConsumerUdsPaths)
        {
            Assert.Contains(path, args);
        }

        Assert.Contains(host.Layout.ControlUdsPath, args);

        Assert.Contains("--columns", args);
        Assert.Contains("200", args);
        Assert.Contains("--rows", args);
        Assert.Contains("50", args);
        Assert.Contains("--shell", args);
        Assert.Contains("/bin/bash", args);
    }

    [Fact]
    public void TerminalHostResourceHasUnresolvedCommandUntilBeforeStart()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var resource = builder.AddExecutable("myapp", "myapp", ".");

        resource.WithTerminal();

        var host = resource.Resource.Annotations.OfType<TerminalAnnotation>().Single().TerminalHost;
        Assert.Equal(TerminalHostResource.UnresolvedCommand, host.Command);
    }

    private static async Task<List<string>> GetResolvedCommandLineArgsAsync(TerminalHostResource host)
    {
        var argsList = new List<object>();
        foreach (var callbackAnnotation in host.Annotations.OfType<CommandLineArgsCallbackAnnotation>())
        {
            await callbackAnnotation.Callback(new CommandLineArgsCallbackContext(argsList, CancellationToken.None));
        }
        return argsList.Select(a => a?.ToString() ?? string.Empty).ToList();
    }
}
