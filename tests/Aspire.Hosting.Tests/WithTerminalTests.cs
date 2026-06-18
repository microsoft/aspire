// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.Hosting.Testing;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Tests;

public class WithTerminalTests
{
    [Fact]
    public async Task WithTerminalAddsTerminalAnnotation()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var resource = builder.AddExecutable("myapp", "myapp", ".");

        resource.WithTerminal();

        var annotation = resource.Resource.Annotations.OfType<TerminalAnnotation>().SingleOrDefault();
        Assert.NotNull(annotation);
        Assert.Equal(120, annotation.Options.Columns);
        Assert.Equal(30, annotation.Options.Rows);
        Assert.Null(annotation.Options.Shell);

        // Until BeforeStartEvent fires the per-replica hosts are not yet materialized:
        // TerminalHosts is empty and IsInitialized is false. This deferral is what
        // allows WithReplicas(N) to be honoured even when called AFTER WithTerminal().
        Assert.False(annotation.IsInitialized);
        Assert.Empty(annotation.TerminalHosts);

        await PublishBeforeStartAsync(builder);

        Assert.True(annotation.IsInitialized);
        Assert.Single(annotation.TerminalHosts);
    }

    [Fact]
    public void WithTerminalOptionsCallbackUpdatesAnnotation()
    {
        // Scope: this test verifies only the TerminalAnnotation captured on the parent
        // resource by the options callback. End-to-end propagation of those options into
        // every per-replica TerminalHostResource (and onto the DCP TerminalSpec) is
        // covered by TerminalHostHasCommandLineArgsForLayoutPaths and the spec mapping
        // tests in TerminalHostEventingSubscriberTests.
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
    public async Task WithTerminalCreatesPerReplicaHiddenTerminalHostResources()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var resource = builder.AddExecutable("myapp", "myapp", ".");

        resource.WithTerminal();

        var model = await BuildAndPublishBeforeStartAsync(builder);

        var hosts = model.Resources.OfType<TerminalHostResource>().ToList();
        var single = Assert.Single(hosts);
        // Default name pattern is "{parent}-terminalhost-{i}" where i is the parent
        // replica index. With the default replica count of 1, the only host is index 0.
        Assert.Equal("myapp-terminalhost-0", single.Name);
        Assert.Same(resource.Resource, single.Parent);
        Assert.Equal(0, single.ParentReplicaIndex);
    }

    [Fact]
    public async Task WithTerminalLinksAnnotationToHostResources()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var resource = builder.AddExecutable("myapp", "myapp", ".");

        resource.WithTerminal();

        var model = await BuildAndPublishBeforeStartAsync(builder);

        var annotation = resource.Resource.Annotations.OfType<TerminalAnnotation>().Single();
        var hostFromModel = model.Resources.OfType<TerminalHostResource>().Single();
        Assert.Same(hostFromModel, Assert.Single(annotation.TerminalHosts));
    }

    [Fact]
    public async Task WithTerminalAddsWaitAnnotationForEachTerminalHost()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var resource = builder.AddExecutable("myapp", "myapp", ".");

        resource.WithTerminal();

        await PublishBeforeStartAsync(builder);

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
    public async Task WithTerminalWorksOnContainerResources()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var container = builder.AddContainer("mycontainer", "myimage");

        container.WithTerminal();

        var annotation = container.Resource.Annotations.OfType<TerminalAnnotation>().SingleOrDefault();
        Assert.NotNull(annotation);

        var model = await BuildAndPublishBeforeStartAsync(builder);

        var hosts = model.Resources.OfType<TerminalHostResource>().ToList();
        var single = Assert.Single(hosts);
        Assert.Equal("mycontainer-terminalhost-0", single.Name);
    }

    [Fact]
    public async Task TerminalHostResourcesAreExcludedFromManifest()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var resource = builder.AddExecutable("myapp", "myapp", ".");

        resource.WithTerminal();

        var model = await BuildAndPublishBeforeStartAsync(builder);

        foreach (var host in model.Resources.OfType<TerminalHostResource>())
        {
            // Merely having a ManifestPublishingCallbackAnnotation is not what excludes a
            // resource from the manifest — being the singleton `Ignore` instance is. The
            // previous assertion would pass for any custom publishing callback, including
            // one that *does* emit the resource into the manifest.
            var manifestAnnotation = host.Annotations.OfType<ManifestPublishingCallbackAnnotation>().SingleOrDefault();
            Assert.NotNull(manifestAnnotation);
            Assert.Same(ManifestPublishingCallbackAnnotation.Ignore, manifestAnnotation);
        }
    }

    [Fact]
    public async Task TerminalHostsAreHiddenByDefault()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var resource = builder.AddExecutable("myapp", "myapp", ".")
            .WithAnnotation(new ReplicaAnnotation(2));
        resource.WithTerminal();

        var model = await BuildAndPublishBeforeStartAsync(builder);

        foreach (var host in model.Resources.OfType<TerminalHostResource>())
        {
            var snapshot = host.Annotations.OfType<ResourceSnapshotAnnotation>().Single();
            Assert.True(snapshot.InitialSnapshot.IsHidden,
                $"'{host.Name}' should be hidden by default.");
        }
    }

    [Fact]
    public async Task ShowTerminalHostOptionMakesTerminalHostsVisible()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var resource = builder.AddExecutable("myapp", "myapp", ".")
            .WithAnnotation(new ReplicaAnnotation(2));
        resource.WithTerminal(options => options.ShowTerminalHost = true);

        var model = await BuildAndPublishBeforeStartAsync(builder);

        var hosts = model.Resources.OfType<TerminalHostResource>().ToList();
        Assert.Equal(2, hosts.Count);
        foreach (var host in hosts)
        {
            var snapshot = host.Annotations.OfType<ResourceSnapshotAnnotation>().Single();
            Assert.False(snapshot.InitialSnapshot.IsHidden,
                $"'{host.Name}' should be visible when ShowTerminalHost=true.");

            // Visibility is the only thing that should change — exclusion from the
            // manifest is unconditional (terminal hosts are never user-deployable).
            Assert.Same(
                ManifestPublishingCallbackAnnotation.Ignore,
                host.Annotations.OfType<ManifestPublishingCallbackAnnotation>().Single());
        }
    }

    [Fact]
    public async Task WithTerminalCleansUpPerReplicaFilesOnApplicationStopped()
    {
        // Regression: prior to wiring an ApplicationStopped callback, every AppHost run
        // left stale UDS sockets and metadata sidecars behind in ~/.aspire/trmnl/.
        // Now: MaterializeTerminalHosts writes a metadata sidecar at BeforeStartEvent
        // time, and the ApplicationStopped callback deletes every file whose name starts
        // with one of OUR replica ids.
        using var builder = TestDistributedApplicationBuilder.Create();
        var resource = builder.AddExecutable("myapp", "myapp", ".");
        resource.WithTerminal();

        var app = builder.Build();
        try
        {
            var model = app.Services.GetRequiredService<DistributedApplicationModel>();
            await builder.Eventing.PublishAsync(new BeforeStartEvent(app.Services, model));

            var annotation = resource.Resource.Annotations.OfType<TerminalAnnotation>().Single();
            Assert.True(annotation.IsInitialized);
            Assert.NotEmpty(annotation.TerminalHosts);

            // BeforeStartEvent should have written the production metadata sidecar.
            // The three .sock files are normally created at runtime by DCP / the
            // terminal-host process (neither runs here), so we synthesise empty
            // placeholders so the cleanup glob ({replicaId}.*) has the full set of
            // four files to match against — that is the contract the cleanup must
            // honour in production. A regression that narrowed the glob (e.g. only
            // deleting *.metadata.json) would otherwise slip past this test and
            // leak stale sockets, which causes UDS bind failures on the next run.
            var allPaths = annotation.TerminalHosts
                .SelectMany(h => new[]
                {
                    h.Layout.MetadataPath,
                    h.Layout.ProducerUdsPath,
                    h.Layout.ConsumerUdsPath,
                    h.Layout.ControlUdsPath,
                })
                .ToArray();

            foreach (var host in annotation.TerminalHosts)
            {
                Assert.True(
                    File.Exists(host.Layout.MetadataPath),
                    $"Metadata sidecar '{host.Layout.MetadataPath}' should exist after BeforeStartEvent.");

                // Touch the three socket-shaped files so the cleanup glob has to
                // delete them too. These are regular files (not real UDS) — the
                // cleanup path treats every {replicaId}.* match the same way.
                File.WriteAllText(host.Layout.ProducerUdsPath, string.Empty);
                File.WriteAllText(host.Layout.ConsumerUdsPath, string.Empty);
                File.WriteAllText(host.Layout.ControlUdsPath, string.Empty);
            }

            await app.StopAsync(CancellationToken.None);

            foreach (var path in allPaths)
            {
                Assert.False(File.Exists(path), $"Expected '{path}' to be deleted after ApplicationStopped.");
            }
        }
        finally
        {
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task WithTerminalWritesMetadataSidecarWithExpectedShape()
    {
        // The sidecar lets external tools (CLI `aspire terminal ps`, dashboard) discover
        // live terminals by listing ~/.aspire/trmnl/*.metadata.json. The on-disk schema
        // must match TerminalHostMetadata exactly — older readers refuse unknown schemas.
        using var builder = TestDistributedApplicationBuilder.Create();
        var resource = builder.AddExecutable("myapp", "myapp", ".")
            .WithTerminal(options =>
            {
                options.Columns = 137;
                options.Rows = 41;
            });

        var app = builder.Build();
        try
        {
            var model = app.Services.GetRequiredService<DistributedApplicationModel>();
            await builder.Eventing.PublishAsync(new BeforeStartEvent(app.Services, model));

            var annotation = resource.Resource.Annotations.OfType<TerminalAnnotation>().Single();
            var host = Assert.Single(annotation.TerminalHosts);

            Assert.True(File.Exists(host.Layout.MetadataPath));

            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(host.Layout.MetadataPath));
            var root = doc.RootElement;

            Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
            Assert.Equal(host.Layout.ReplicaId, root.GetProperty("replicaId").GetString());
            Assert.Equal("myapp", root.GetProperty("resourceName").GetString());
            Assert.Equal(0, root.GetProperty("replicaIndex").GetInt32());
            Assert.Equal(Environment.ProcessId, root.GetProperty("appHostPid").GetInt32());
            Assert.Equal(137, root.GetProperty("columns").GetInt32());
            Assert.Equal(41, root.GetProperty("rows").GetInt32());
            Assert.Equal(host.Layout.ControlUdsPath, root.GetProperty("controlSocketPath").GetString());
            Assert.Equal(host.Layout.ConsumerUdsPath, root.GetProperty("consumerSocketPath").GetString());
            Assert.False(string.IsNullOrEmpty(root.GetProperty("appHostPath").GetString()));
            Assert.NotEqual(default, root.GetProperty("createdAtUtc").GetDateTime());

            if (!OperatingSystem.IsWindows())
            {
                // 0600 — defense-in-depth; parent dir is already 0700.
                var mode = File.GetUnixFileMode(host.Layout.MetadataPath);
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
            }
        }
        finally
        {
            await app.DisposeAsync();
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
    public async Task WithTerminalDefaultsToOneTerminalHost()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var resource = builder.AddExecutable("myapp", "myapp", ".");

        resource.WithTerminal();

        await PublishBeforeStartAsync(builder);

        var hosts = resource.Resource.Annotations.OfType<TerminalAnnotation>().Single().TerminalHosts;
        var single = Assert.Single(hosts);
        Assert.Equal(0, single.ParentReplicaIndex);
        Assert.NotEmpty(single.Layout.ProducerUdsPath);
        Assert.NotEmpty(single.Layout.ConsumerUdsPath);
        Assert.NotEmpty(single.Layout.ControlUdsPath);
    }

    [Fact]
    public async Task WithTerminalAfterWithReplicasCreatesOneTerminalHostPerReplica()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var resource = builder.AddExecutable("myapp", "myapp", ".")
            .WithAnnotation(new ReplicaAnnotation(3));

        resource.WithTerminal();

        await PublishBeforeStartAsync(builder);

        var hosts = resource.Resource.Annotations.OfType<TerminalAnnotation>().Single().TerminalHosts;
        Assert.Equal(3, hosts.Count);

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(i, hosts[i].ParentReplicaIndex);
            // The parent replica index is folded into the per-replica id, so the four
            // files for replica i share a distinct `{id}.` prefix and never collide with
            // replica j's files in the shared ~/.aspire/trmnl/ directory.
            Assert.NotEmpty(hosts[i].Layout.ReplicaId);
            Assert.StartsWith(hosts[i].Layout.ReplicaId + ".", Path.GetFileName(hosts[i].Layout.ProducerUdsPath));
            Assert.StartsWith(hosts[i].Layout.ReplicaId + ".", Path.GetFileName(hosts[i].Layout.ConsumerUdsPath));
            Assert.Equal($"myapp-terminalhost-{i}", hosts[i].Name);
        }
    }

    [Fact]
    public async Task WithReplicasAfterWithTerminalCreatesOneTerminalHostPerReplica()
    {
        // Regression test for the original ordering bug: previously WithTerminal() read
        // the parent's ReplicaAnnotation eagerly, so calling WithReplicas(N) AFTER
        // WithTerminal() resulted in only one terminal host being created. With deferred
        // host materialization in BeforeStartEvent, the order is now irrelevant.
        using var builder = TestDistributedApplicationBuilder.Create();
        var resource = builder.AddExecutable("myapp", "myapp", ".");

        resource.WithTerminal();
        resource.WithAnnotation(new ReplicaAnnotation(3));

        await PublishBeforeStartAsync(builder);

        var hosts = resource.Resource.Annotations.OfType<TerminalAnnotation>().Single().TerminalHosts;
        Assert.Equal(3, hosts.Count);

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(i, hosts[i].ParentReplicaIndex);
            Assert.Equal($"myapp-terminalhost-{i}", hosts[i].Name);
        }
    }

    [Fact]
    public async Task TerminalHostLayoutPathsAreUnderTheSameTrmnlDirectoryWithDistinctReplicaIds()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var resource = builder.AddExecutable("myapp", "myapp", ".")
            .WithAnnotation(new ReplicaAnnotation(2));

        resource.WithTerminal();

        await PublishBeforeStartAsync(builder);

        var hosts = resource.Resource.Annotations.OfType<TerminalAnnotation>().Single().TerminalHosts;
        var expectedDirectory = Aspire.Shared.TerminalHost.TerminalHostPaths.GetTrmnlDirectory(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

        var seenReplicaIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var host in hosts)
        {
            Assert.Equal(expectedDirectory, Path.GetDirectoryName(host.Layout.ProducerUdsPath));
            Assert.Equal(expectedDirectory, Path.GetDirectoryName(host.Layout.ConsumerUdsPath));
            Assert.Equal(expectedDirectory, Path.GetDirectoryName(host.Layout.ControlUdsPath));
            Assert.Equal(expectedDirectory, Path.GetDirectoryName(host.Layout.MetadataPath));

            Assert.StartsWith(host.Layout.ReplicaId + ".", Path.GetFileName(host.Layout.ProducerUdsPath));
            Assert.StartsWith(host.Layout.ReplicaId + ".", Path.GetFileName(host.Layout.ConsumerUdsPath));
            Assert.StartsWith(host.Layout.ReplicaId + ".", Path.GetFileName(host.Layout.ControlUdsPath));
            Assert.StartsWith(host.Layout.ReplicaId + ".", Path.GetFileName(host.Layout.MetadataPath));

            // Distinct replica ids across the parent's replicas (so per-replica file
            // groups don't collide).
            Assert.True(seenReplicaIds.Add(host.Layout.ReplicaId), $"Duplicate replica id '{host.Layout.ReplicaId}'.");
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

        await PublishBeforeStartAsync(builder);

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
    public async Task TerminalHostResourcesHaveUnresolvedCommandUntilTerminalHostPathIsConfigured()
    {
        // The host process binary path is filled in by TerminalHostEventingSubscriber
        // from DcpOptions during BeforeStartEvent. The test environment doesn't ship a
        // real terminalhost binary, so the placeholder remains after the event fires.
        using var builder = TestDistributedApplicationBuilder.Create();
        var resource = builder.AddExecutable("myapp", "myapp", ".");

        resource.WithTerminal();

        await PublishBeforeStartAsync(builder);

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

    private static async Task PublishBeforeStartAsync(IDistributedApplicationTestingBuilder builder)
    {
        // BeforeStartEvent is the seam where WithTerminal() now materializes its per-replica
        // hosts. Tests that observe TerminalHosts/host annotations have to publish it manually
        // because the test harness doesn't go through DistributedApplication.RunApplicationAsync.
        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        await builder.Eventing.PublishAsync(new BeforeStartEvent(app.Services, model));
    }

    /// <summary>
    /// Builds the application, publishes <see cref="BeforeStartEvent"/> (the seam that
    /// materializes per-replica terminal hosts), then disposes the
    /// <see cref="DistributedApplication"/> before returning. The returned
    /// <see cref="DistributedApplicationModel"/> is the same instance the eventing
    /// handlers ran against and is safe to inspect after the app is disposed (its
    /// Resources collection is owned by the model, not the app's host).
    /// </summary>
    /// <remarks>
    /// Previously this returned (app, model) and callers discarded the app as `_`,
    /// which leaked the DistributedApplication's background services, DCP-related
    /// objects, and pooled handles until finalization. The sibling helper
    /// <see cref="PublishBeforeStartAsync"/> already used `using var app`; this brings
    /// the two helpers into alignment.
    /// </remarks>
    private static async Task<DistributedApplicationModel> BuildAndPublishBeforeStartAsync(IDistributedApplicationTestingBuilder builder)
    {
        await using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        await builder.Eventing.PublishAsync(new BeforeStartEvent(app.Services, model));
        return model;
    }

    [Fact]
    public void WithTerminalForcesProcessExecution()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        var resource = builder.AddProject<TestProject>("myproj", options => { options.ExcludeLaunchProfile = true; });

        resource.WithTerminal();

        Assert.True(resource.Resource.HasAnnotationOfType<ForceProcessExecutionAnnotation>());
    }

    private sealed class TestProject : IProjectMetadata
    {
        public string ProjectPath => "another-path";
    }
}
