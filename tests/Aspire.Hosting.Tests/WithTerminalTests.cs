// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.Json;
using Aspire.Hosting.Testing;
using Aspire.Hosting.Utils;
using Aspire.Shared.TerminalHost;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Tests;

public class WithTerminalTests
{
    [Fact]
    public void TerminalImplementationTypesAreInternal()
    {
        Assert.True(typeof(TerminalAnnotation).IsNotPublic);
        Assert.True(typeof(TerminalHostResource).IsNotPublic);
        Assert.True(typeof(TerminalHostLayout).IsNotPublic);
    }

    [Fact]
    public void TerminalOptionsIsExperimental()
    {
        var attribute = Assert.Single(typeof(TerminalOptions).GetCustomAttributes<ExperimentalAttribute>());

        Assert.Equal("ASPIRETERMINAL001", attribute.DiagnosticId);
        Assert.Equal("https://aka.ms/aspire/diagnostics/{0}", attribute.UrlFormat);
    }

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
        });

        var annotation = resource.Resource.Annotations.OfType<TerminalAnnotation>().SingleOrDefault();
        Assert.NotNull(annotation);
        Assert.Equal(200, annotation.Options.Columns);
        Assert.Equal(50, annotation.Options.Rows);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void WithTerminalRejectsNonPositiveColumns(int columns)
    {
        // Aspire.TerminalHost rejects a PTY width below 1, so an invalid Columns value must
        // fail at the WithTerminal() call site rather than as a hidden terminal-host startup
        // failure that can block the parent resource.
        using var builder = TestDistributedApplicationBuilder.Create();
        var resource = builder.AddExecutable("myapp", "myapp", ".");

        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => resource.WithTerminal(options => options.Columns = columns));
        Assert.Equal("value", ex.ParamName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void WithTerminalRejectsNonPositiveRows(int rows)
    {
        // Aspire.TerminalHost rejects a PTY height below 1, so an invalid Rows value must
        // fail at the WithTerminal() call site rather than as a hidden terminal-host startup
        // failure that can block the parent resource.
        using var builder = TestDistributedApplicationBuilder.Create();
        var resource = builder.AddExecutable("myapp", "myapp", ".");

        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => resource.WithTerminal(options => options.Rows = rows));
        Assert.Equal("value", ex.ParamName);
    }

    [Fact]
    public void TerminalOptionsRejectNonPositiveDimensions()
    {
        var options = new TerminalOptions();

        Assert.Throws<ArgumentOutOfRangeException>(() => options.Columns = 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => options.Rows = -5);

        // A valid assignment still succeeds, and the boundary value 1 is accepted.
        options.Columns = 1;
        options.Rows = 1;
        Assert.Equal(1, options.Columns);
        Assert.Equal(1, options.Rows);
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
        // Now: MaterializeTerminalHostsAsync writes a metadata sidecar at BeforeStartEvent
        // time, and the ApplicationStopped callback deletes the four known files for each
        // replica owned by this run.
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
            // placeholders so the cleanup path has the full set of four known files
            // to delete. A regression that only deleted metadata would otherwise slip
            // past this test and leak stale sockets, causing UDS bind failures next run.
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

                // Touch the three socket-shaped files so cleanup has to delete them too.
                // These are regular files rather than real UDS endpoints.
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
            CleanUpTerminalHostFiles(resource);
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

            Assert.Equal(TerminalHostMetadata.CurrentSchemaVersion, root.GetProperty("schemaVersion").GetInt32());
            Assert.Equal(host.Layout.ReplicaId, root.GetProperty("replicaId").GetString());
            Assert.Equal("myapp", root.GetProperty("resourceName").GetString());
            Assert.Equal(0, root.GetProperty("replicaIndex").GetInt32());
            Assert.Equal(Environment.ProcessId, root.GetProperty("appHostPid").GetInt32());
            Assert.Equal(
                ProcessStartTimeHelper.GetCurrentProcessStartTimeUnixMilliseconds(),
                root.GetProperty("appHostProcessStartTimeUnixMilliseconds").GetInt64());
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
            CleanUpTerminalHostFiles(resource);
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task WithTerminalSweepsOrphanedFilesFromDeadAppHost()
    {
        // Regression for https://github.com/microsoft/aspire/issues/19302 (startup-GC half): an
        // AppHost that exits ungracefully (SIGKILL / crash) can strand {id}.dcp.sock /
        // {id}.host.sock and its metadata sidecar in the shared ~/.aspire/trmnl/ forever. On the
        // next AppHost start, MaterializeTerminalHostsAsync sweeps sidecars whose owning PID is no
        // longer alive.
        var homeDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var trmnlDirectory = TerminalHostPaths.GetTrmnlDirectory(homeDirectory);
        Directory.CreateDirectory(trmnlDirectory);

        // Unique, test-owned replica id so we only ever assert on files WE created and never
        // collide with a real terminal on the developer's machine.
        var orphanId = CreateTestReplicaId("orphan");
        var orphanMetadataPath = Path.Combine(trmnlDirectory, $"{orphanId}.{TerminalHostPaths.MetadataSuffix}");
        var orphanProducerPath = Path.Combine(trmnlDirectory, $"{orphanId}.{TerminalHostPaths.ProducerSockPurpose}.sock");
        var orphanConsumerPath = Path.Combine(trmnlDirectory, $"{orphanId}.{TerminalHostPaths.ConsumerSockPurpose}.sock");
        var orphanControlPath = Path.Combine(trmnlDirectory, $"{orphanId}.{TerminalHostPaths.ControlSockPurpose}.sock");
        var orphanLockPath = GetReplicaLockPath(trmnlDirectory, orphanId);

        // int.MaxValue is not a live process on any supported platform (it exceeds Linux pid_max
        // and is an invalid — odd — Windows PID), so the sweep's liveness check classifies the
        // owner as dead and reclaims the files.
        WriteSidecar(
            orphanMetadataPath,
            fileReplicaId: orphanId,
            metadataReplicaId: orphanId,
            appHostPid: int.MaxValue,
            appHostProcessStartTimeUnixMilliseconds: 1,
            schemaVersion: TerminalHostMetadata.CurrentSchemaVersion);
        File.WriteAllText(orphanProducerPath, string.Empty);
        File.WriteAllText(orphanConsumerPath, string.Empty);
        File.WriteAllText(orphanControlPath, string.Empty);

        using var builder = TestDistributedApplicationBuilder.Create();
        var resource = builder.AddExecutable("myapp", "myapp", ".");
        resource.WithTerminal();

        var app = builder.Build();
        try
        {
            var model = app.Services.GetRequiredService<DistributedApplicationModel>();
            await builder.Eventing.PublishAsync(new BeforeStartEvent(app.Services, model));

            Assert.False(File.Exists(orphanMetadataPath), "Dead-owner sidecar should be swept on startup.");
            Assert.False(File.Exists(orphanProducerPath), "Dead-owner producer socket should be swept on startup.");
            Assert.False(File.Exists(orphanConsumerPath), "Dead-owner consumer socket should be swept on startup.");
            Assert.False(File.Exists(orphanControlPath), "Dead-owner control socket should be swept on startup.");
        }
        finally
        {
            // Belt and braces: remove the orphan placeholders (in case the sweep regressed) and
            // this run's own materialized terminal-host files so we don't pollute the shared dir.
            DeleteIfExists(orphanMetadataPath, orphanProducerPath, orphanConsumerPath, orphanControlPath, orphanLockPath);
            CleanUpTerminalHostFiles(resource);
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task WithTerminalDoesNotSweepFilesFromLiveAppHost()
    {
        // The startup sweep must NEVER delete files whose owning AppHost is still alive — otherwise
        // a second AppHost start would rip the sockets out from under a running terminal. A child
        // process exercises the Process.GetProcessById liveness branch rather than the special case
        // that preserves sidecars owned by this test process.
        var homeDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var trmnlDirectory = TerminalHostPaths.GetTrmnlDirectory(homeDirectory);
        Directory.CreateDirectory(trmnlDirectory);

        var liveId = CreateTestReplicaId("live");
        var liveMetadataPath = Path.Combine(trmnlDirectory, $"{liveId}.{TerminalHostPaths.MetadataSuffix}");
        var liveProducerPath = Path.Combine(trmnlDirectory, $"{liveId}.{TerminalHostPaths.ProducerSockPurpose}.sock");
        var liveLockPath = GetReplicaLockPath(trmnlDirectory, liveId);

        using var liveProcess = TestProcesses.StartLongRunning();
        var liveProcessStartTimeUnixMilliseconds =
            ProcessStartTimeHelper.TryGetProcessStartTimeUnixMilliseconds(liveProcess.Id);
        Assert.True(liveProcessStartTimeUnixMilliseconds.HasValue);
        WriteSidecar(
            liveMetadataPath,
            fileReplicaId: liveId,
            metadataReplicaId: liveId,
            appHostPid: liveProcess.Id,
            appHostProcessStartTimeUnixMilliseconds: liveProcessStartTimeUnixMilliseconds.Value,
            schemaVersion: TerminalHostMetadata.CurrentSchemaVersion);
        File.WriteAllText(liveProducerPath, string.Empty);

        using var builder = TestDistributedApplicationBuilder.Create();
        var resource = builder.AddExecutable("myapp", "myapp", ".");
        resource.WithTerminal();

        var app = builder.Build();
        try
        {
            var model = app.Services.GetRequiredService<DistributedApplicationModel>();
            await builder.Eventing.PublishAsync(new BeforeStartEvent(app.Services, model));

            Assert.True(File.Exists(liveMetadataPath), "Live-owner sidecar must be preserved by the sweep.");
            Assert.True(File.Exists(liveProducerPath), "Live-owner socket must be preserved by the sweep.");
        }
        finally
        {
            DeleteIfExists(liveMetadataPath, liveProducerPath, liveLockPath);
            CleanUpTerminalHostFiles(resource);

            if (!liveProcess.HasExited)
            {
                liveProcess.Kill(entireProcessTree: true);
                await liveProcess.WaitForExitAsync();
            }

            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task WithTerminalSweepReclaimsFilesFromReusedProcessId()
    {
        var trmnlDirectory = GetTerminalDirectory();
        var replicaId = CreateTestReplicaId("reused-pid");
        var metadataPath = Path.Combine(trmnlDirectory, $"{replicaId}.{TerminalHostPaths.MetadataSuffix}");
        var producerPath = Path.Combine(trmnlDirectory, $"{replicaId}.{TerminalHostPaths.ProducerSockPurpose}.sock");
        var lockPath = GetReplicaLockPath(trmnlDirectory, replicaId);
        var staleStartTimeUnixMilliseconds =
            ProcessStartTimeHelper.GetCurrentProcessStartTimeUnixMilliseconds() - (long)TimeSpan.FromMinutes(1).TotalMilliseconds;

        WriteSidecar(
            metadataPath,
            replicaId,
            replicaId,
            appHostPid: Environment.ProcessId,
            appHostProcessStartTimeUnixMilliseconds: staleStartTimeUnixMilliseconds,
            schemaVersion: TerminalHostMetadata.CurrentSchemaVersion);
        File.WriteAllText(producerPath, string.Empty);

        try
        {
            await RunStartupSweepAsync();

            Assert.False(File.Exists(metadataPath));
            Assert.False(File.Exists(producerPath));
        }
        finally
        {
            DeleteIfExists(metadataPath, producerPath, lockPath);
        }
    }

    [Fact]
    public async Task WithTerminalSweepDoesNotTrustReplicaIdFromMetadata()
    {
        var trmnlDirectory = GetTerminalDirectory();
        var fileReplicaId = CreateTestReplicaId("mismatch");
        var metadataPath = Path.Combine(trmnlDirectory, $"{fileReplicaId}.{TerminalHostPaths.MetadataSuffix}");
        var maliciousPrefix = "terminal-sweep-" + Guid.NewGuid().ToString("N");
        var sentinelPath = Path.Combine(trmnlDirectory, maliciousPrefix + "-sentinel.tmp");
        var lockPath = GetReplicaLockPath(trmnlDirectory, fileReplicaId);

        WriteSidecar(
            metadataPath,
            fileReplicaId,
            metadataReplicaId: maliciousPrefix + "*",
            appHostPid: int.MaxValue,
            appHostProcessStartTimeUnixMilliseconds: 1,
            schemaVersion: TerminalHostMetadata.CurrentSchemaVersion);
        File.WriteAllText(sentinelPath, string.Empty);

        try
        {
            await RunStartupSweepAsync();

            Assert.True(File.Exists(metadataPath), "A filename/content mismatch must not authorize deletion.");
            Assert.True(File.Exists(sentinelPath), "Metadata content must never be used as a file search pattern.");
        }
        finally
        {
            DeleteIfExists(metadataPath, sentinelPath, lockPath);
        }
    }

    [Fact]
    public async Task WithTerminalSweepIgnoresInvalidReplicaIdFilename()
    {
        var trmnlDirectory = GetTerminalDirectory();
        var invalidReplicaIdCharacters = CreateTestReplicaId("invalid").ToCharArray();
        invalidReplicaIdCharacters[5] = '%';
        var invalidReplicaId = new string(invalidReplicaIdCharacters);
        var metadataPath = Path.Combine(trmnlDirectory, $"{invalidReplicaId}.{TerminalHostPaths.MetadataSuffix}");
        var producerPath = Path.Combine(trmnlDirectory, $"{invalidReplicaId}.{TerminalHostPaths.ProducerSockPurpose}.sock");

        WriteSidecar(
            metadataPath,
            invalidReplicaId,
            invalidReplicaId,
            appHostPid: int.MaxValue,
            appHostProcessStartTimeUnixMilliseconds: 1,
            schemaVersion: TerminalHostMetadata.CurrentSchemaVersion);
        File.WriteAllText(producerPath, string.Empty);

        try
        {
            await RunStartupSweepAsync();

            Assert.True(File.Exists(metadataPath));
            Assert.True(File.Exists(producerPath));
        }
        finally
        {
            DeleteIfExists(metadataPath, producerPath);
        }
    }

    [Fact]
    public async Task WithTerminalSweepIgnoresUnknownMetadataSchema()
    {
        var trmnlDirectory = GetTerminalDirectory();
        var replicaId = CreateTestReplicaId("schema");
        var metadataPath = Path.Combine(trmnlDirectory, $"{replicaId}.{TerminalHostPaths.MetadataSuffix}");
        var producerPath = Path.Combine(trmnlDirectory, $"{replicaId}.{TerminalHostPaths.ProducerSockPurpose}.sock");
        var lockPath = GetReplicaLockPath(trmnlDirectory, replicaId);

        WriteSidecar(
            metadataPath,
            replicaId,
            replicaId,
            appHostPid: int.MaxValue,
            appHostProcessStartTimeUnixMilliseconds: 1,
            schemaVersion: TerminalHostMetadata.CurrentSchemaVersion + 1);
        File.WriteAllText(producerPath, string.Empty);

        try
        {
            await RunStartupSweepAsync();

            Assert.True(File.Exists(metadataPath));
            Assert.True(File.Exists(producerPath));
        }
        finally
        {
            DeleteIfExists(metadataPath, producerPath, lockPath);
        }
    }

    [Fact]
    public async Task WithTerminalSweepSkipsReplicaLockedByAnotherWriter()
    {
        var trmnlDirectory = GetTerminalDirectory();
        var replicaId = CreateTestReplicaId("locked");
        var metadataPath = Path.Combine(trmnlDirectory, $"{replicaId}.{TerminalHostPaths.MetadataSuffix}");
        var producerPath = Path.Combine(trmnlDirectory, $"{replicaId}.{TerminalHostPaths.ProducerSockPurpose}.sock");
        var lockPath = GetReplicaLockPath(trmnlDirectory, replicaId);

        WriteSidecar(
            metadataPath,
            replicaId,
            replicaId,
            appHostPid: int.MaxValue,
            appHostProcessStartTimeUnixMilliseconds: 1,
            schemaVersion: TerminalHostMetadata.CurrentSchemaVersion);
        File.WriteAllText(producerPath, string.Empty);

        try
        {
            using (new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                await RunStartupSweepAsync();
            }

            Assert.True(File.Exists(metadataPath));
            Assert.True(File.Exists(producerPath));
        }
        finally
        {
            DeleteIfExists(metadataPath, producerPath, lockPath);
        }
    }

    private static async Task RunStartupSweepAsync()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var resource = builder.AddExecutable("sweep-" + Guid.NewGuid().ToString("N"), "myapp", ".");
        resource.WithTerminal();

        var app = builder.Build();
        try
        {
            var model = app.Services.GetRequiredService<DistributedApplicationModel>();
            await builder.Eventing.PublishAsync(new BeforeStartEvent(app.Services, model));
        }
        finally
        {
            CleanUpTerminalHostFiles(resource);
            await app.DisposeAsync();
        }
    }

    private static void WriteSidecar(
        string metadataPath,
        string fileReplicaId,
        string metadataReplicaId,
        int appHostPid,
        long appHostProcessStartTimeUnixMilliseconds,
        int schemaVersion)
    {
        // The socket-path fields are required by the schema but never read by the sweep,
        // so simple placeholders are sufficient for these metadata validation tests.
        var metadata = new TerminalHostMetadata
        {
            SchemaVersion = schemaVersion,
            ReplicaId = metadataReplicaId,
            ResourceName = "orphan",
            ReplicaIndex = 0,
            AppHostPath = "/does/not/matter",
            AppHostPid = appHostPid,
            AppHostProcessStartTimeUnixMilliseconds = appHostProcessStartTimeUnixMilliseconds,
            CreatedAtUtc = DateTime.UtcNow,
            Columns = 80,
            Rows = 24,
            ControlSocketPath = fileReplicaId + ".ctrl.sock",
            ConsumerSocketPath = fileReplicaId + ".host.sock",
        };

        File.WriteAllText(metadataPath, JsonSerializer.Serialize(metadata));
    }

    private static string CreateTestReplicaId(string resourceName)
        => TerminalHostPaths.ComputeReplicaId(
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")),
            resourceName,
            replicaIndex: 0);

    private static string GetTerminalDirectory()
    {
        var homeDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var trmnlDirectory = TerminalHostPaths.GetTrmnlDirectory(homeDirectory);
        Directory.CreateDirectory(trmnlDirectory);
        return trmnlDirectory;
    }

    private static string GetReplicaLockPath(string trmnlDirectory, string replicaId)
        => Path.Combine(trmnlDirectory, $"{replicaId}.{TerminalHostPaths.LockSuffix}");

    private static void CleanUpTerminalHostFiles(IResourceBuilder<ExecutableResource> resource)
    {
        var annotation = resource.Resource.Annotations.OfType<TerminalAnnotation>().FirstOrDefault();
        if (annotation is null || !annotation.IsInitialized)
        {
            return;
        }

        foreach (var host in annotation.TerminalHosts)
        {
            DeleteIfExists(
                host.Layout.MetadataPath,
                host.Layout.ProducerUdsPath,
                host.Layout.ConsumerUdsPath,
                host.Layout.ControlUdsPath,
                GetReplicaLockPath(Path.GetDirectoryName(host.Layout.MetadataPath)!, host.Layout.ReplicaId));
        }
    }

    private static void CleanUpTerminalHostLocks(DistributedApplicationModel model)
    {
        foreach (var host in model.Resources.OfType<TerminalHostResource>())
        {
            DeleteIfExists(GetReplicaLockPath(
                Path.GetDirectoryName(host.Layout.MetadataPath)!,
                host.Layout.ReplicaId));
        }
    }

    private static void DeleteIfExists(params string[] paths)
    {
        foreach (var path in paths)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
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
            // DCP allocates the PTY for the resource's own process, and its TerminalSpec
            // has no shell field. The terminal host therefore receives no shell argument.
            Assert.DoesNotContain("--shell", args);
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
        try
        {
            await builder.Eventing.PublishAsync(new BeforeStartEvent(app.Services, model));
        }
        finally
        {
            CleanUpTerminalHostLocks(model);
        }
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
        try
        {
            await builder.Eventing.PublishAsync(new BeforeStartEvent(app.Services, model));
            return model;
        }
        finally
        {
            CleanUpTerminalHostLocks(model);
        }
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
