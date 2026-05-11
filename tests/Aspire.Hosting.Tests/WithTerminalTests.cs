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
        // The annotation now carries the per-replica list of host resources rather
        // than a single TerminalHostResource. Default replica count is 1 (no
        // ReplicaAnnotation present), so exactly one host is created.
        Assert.NotNull(annotation.TerminalHosts);
        Assert.Single(annotation.TerminalHosts);
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
    public void WithTerminalCreatesPerReplicaHiddenTerminalHostResources()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var resource = builder.AddExecutable("myapp", "myapp", ".");

        resource.WithTerminal();

        var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var hosts = model.Resources.OfType<TerminalHostResource>().ToList();
        var single = Assert.Single(hosts);
        // Default name pattern is "{parent}-terminalhost-{i}" where i is the parent
        // replica index. With the default replica count of 1, the only host is index 0.
        Assert.Equal("myapp-terminalhost-0", single.Name);
        Assert.Same(resource.Resource, single.Parent);
        Assert.Equal(0, single.ParentReplicaIndex);
    }

    [Fact]
    public void WithTerminalLinksAnnotationToHostResources()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var resource = builder.AddExecutable("myapp", "myapp", ".");

        resource.WithTerminal();

        var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var annotation = resource.Resource.Annotations.OfType<TerminalAnnotation>().Single();
        var hostFromModel = model.Resources.OfType<TerminalHostResource>().Single();
        Assert.Same(hostFromModel, Assert.Single(annotation.TerminalHosts));
    }

    [Fact]
    public void WithTerminalAddsWaitAnnotationForEachTerminalHost()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var resource = builder.AddExecutable("myapp", "myapp", ".");

        resource.WithTerminal();

        var waitAnnotations = resource.Resource.Annotations.OfType<WaitAnnotation>()
            .Where(w => w.Resource is TerminalHostResource)
            .ToList();
        var single = Assert.Single(waitAnnotations);
        Assert.Equal(WaitType.WaitUntilStarted, single.WaitType);
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

        var hosts = model.Resources.OfType<TerminalHostResource>().ToList();
        var single = Assert.Single(hosts);
        Assert.Equal("mycontainer-terminalhost-0", single.Name);
    }

    [Fact]
    public void TerminalHostResourcesAreExcludedFromManifest()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var resource = builder.AddExecutable("myapp", "myapp", ".");

        resource.WithTerminal();

        var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        foreach (var host in model.Resources.OfType<TerminalHostResource>())
        {
            var manifestAnnotation = host.Annotations.OfType<ManifestPublishingCallbackAnnotation>().SingleOrDefault();
            Assert.NotNull(manifestAnnotation);
        }
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
    public void WithTerminalDefaultsToOneTerminalHost()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var resource = builder.AddExecutable("myapp", "myapp", ".");

        resource.WithTerminal();

        var hosts = resource.Resource.Annotations.OfType<TerminalAnnotation>().Single().TerminalHosts;
        var single = Assert.Single(hosts);
        Assert.Equal(0, single.ParentReplicaIndex);
        Assert.NotEmpty(single.Layout.ProducerUdsPath);
        Assert.NotEmpty(single.Layout.ConsumerUdsPath);
        Assert.NotEmpty(single.Layout.ControlUdsPath);
    }

    [Fact]
    public void WithTerminalAfterWithReplicasCreatesOneTerminalHostPerReplica()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var resource = builder.AddExecutable("myapp", "myapp", ".")
            .WithAnnotation(new ReplicaAnnotation(3));

        resource.WithTerminal();

        var hosts = resource.Resource.Annotations.OfType<TerminalAnnotation>().Single().TerminalHosts;
        Assert.Equal(3, hosts.Count);

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(i, hosts[i].ParentReplicaIndex);
            // The parent replica index is encoded into the per-replica directory of
            // the layout — DCP and viewers don't need to know that, but path uniqueness
            // is what keeps the per-replica hosts from colliding on the same UDS.
            Assert.Contains($"{Path.DirectorySeparatorChar}{i}{Path.DirectorySeparatorChar}", hosts[i].Layout.ProducerUdsPath);
            Assert.Contains($"{Path.DirectorySeparatorChar}{i}{Path.DirectorySeparatorChar}", hosts[i].Layout.ConsumerUdsPath);
            Assert.Equal($"myapp-terminalhost-{i}", hosts[i].Name);
        }
    }

    [Fact]
    public void TerminalHostLayoutPathsAreUnderTheSameTempBaseDirectory()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var resource = builder.AddExecutable("myapp", "myapp", ".")
            .WithAnnotation(new ReplicaAnnotation(2));

        resource.WithTerminal();

        var hosts = resource.Resource.Annotations.OfType<TerminalAnnotation>().Single().TerminalHosts;
        var sharedBase = hosts[0].Layout.BaseDirectory;

        foreach (var host in hosts)
        {
            // All per-replica hosts share the same per-target base directory so a
            // single recursive delete cleans up every replica's sockets.
            Assert.Equal(sharedBase, host.Layout.BaseDirectory);
            Assert.StartsWith(sharedBase, host.Layout.ProducerUdsPath);
            Assert.StartsWith(sharedBase, host.Layout.ConsumerUdsPath);
            Assert.StartsWith(sharedBase, host.Layout.ControlUdsPath);
        }
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

        var hosts = resource.Resource.Annotations.OfType<TerminalAnnotation>().Single().TerminalHosts;
        // Each per-replica host serves exactly one replica, so its argv carries
        // exactly one --producer-uds / --consumer-uds / --control-uds value.
        // --replica-count is intentionally absent in the new single-replica shape.
        foreach (var host in hosts)
        {
            var args = await GetResolvedCommandLineArgsAsync(host);

            Assert.DoesNotContain("--replica-count", args);
            Assert.Single(args, a => a == "--producer-uds");
            Assert.Single(args, a => a == "--consumer-uds");
            Assert.Single(args, a => a == "--control-uds");

            Assert.Contains(host.Layout.ProducerUdsPath, args);
            Assert.Contains(host.Layout.ConsumerUdsPath, args);
            Assert.Contains(host.Layout.ControlUdsPath, args);

            Assert.Contains("--columns", args);
            Assert.Contains("200", args);
            Assert.Contains("--rows", args);
            Assert.Contains("50", args);
            Assert.Contains("--shell", args);
            Assert.Contains("/bin/bash", args);
        }
    }

    [Fact]
    public void TerminalHostResourcesHaveUnresolvedCommandUntilBeforeStart()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var resource = builder.AddExecutable("myapp", "myapp", ".");

        resource.WithTerminal();

        foreach (var host in resource.Resource.Annotations.OfType<TerminalAnnotation>().Single().TerminalHosts)
        {
            Assert.Equal(TerminalHostResource.UnresolvedCommand, host.Command);
        }
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
