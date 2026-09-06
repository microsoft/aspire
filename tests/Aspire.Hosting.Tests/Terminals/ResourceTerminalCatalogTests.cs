// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Terminals;
using Aspire.Hosting.Testing;
using Aspire.Hosting.Tests.Dcp;
using Aspire.Hosting.Utils;
using Aspire.Shared.TerminalHost;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

#pragma warning disable ASPIRETERMINAL002 // Test consumer of the experimental AppHost terminal API.

namespace Aspire.Hosting.Tests.Terminals;

/// <summary>
/// Guards the projection of terminal-enabled resources into the same shape as AppHost-owned terminals.
/// Nothing here connects to a terminal host: the catalog reads only the application model, which is what
/// lets a listing be produced without a running workload.
/// </summary>
[Trait("Partition", "2")]
public class ResourceTerminalCatalogTests : IAsyncLifetime
{
    // Leave room for the generated host socket name under macOS's 104-byte Unix socket path limit.
    private readonly string _terminalDirectory = Directory.CreateTempSubdirectory("aspire-tc-").FullName;

    [Fact]
    public void BuildIdRoundTripsThroughIsResourceTerminalId()
    {
        var id = ResourceTerminalCatalog.BuildId("shellbox", 2);

        Assert.Equal("resource:shellbox:2", id);
        Assert.True(ResourceTerminalCatalog.IsResourceTerminalId(id));
    }

    [Fact]
    public void AppHostTerminalIdIsNotMistakenForAResourceTerminal()
    {
        // AppHost terminal ids are opaque, so the prefix is the only thing separating the two id spaces.
        var service = TestTerminalService.Create();
        var terminal = service.CreateTerminal(new TerminalLaunchOptions
        {
            Title = "Shell",
            Command = new TerminalCommand("bash"),
        });

        Assert.False(ResourceTerminalCatalog.IsResourceTerminalId(terminal.Id));
    }

    [Fact]
    public async Task ListReturnsNothingBeforeTerminalHostsAreMaterialized()
    {
        // TerminalAnnotation.TerminalHosts stays empty until BeforeStartEvent, so a catalog built against a
        // model that hasn't started yet must report an empty list rather than throwing or inventing entries.
        using var builder = CreateBuilder();
        builder.AddExecutable("myapp", "myapp", ".").WithTerminal();

        await using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        await using var catalog = new ResourceTerminalCatalog(model, NullLogger.Instance);

        Assert.Empty(catalog.List());
    }

    [Fact]
    public async Task ListProjectsOneEntryPerReplica()
    {
        using var builder = CreateBuilder();
        builder.AddExecutable("myapp", "myapp", ".").WithAnnotation(new ReplicaAnnotation(3)).WithTerminal();

        await using var catalog = await CreateCatalogAsync(builder);

        var entries = catalog.List().OrderBy(e => e.ReplicaIndex).ToList();

        Assert.Collection(entries,
            e => Assert.Equal("resource:myapp:0", e.Id),
            e => Assert.Equal("resource:myapp:1", e.Id),
            e => Assert.Equal("resource:myapp:2", e.Id));
    }

    [Fact]
    public async Task SingleReplicaTitleIsNotQualifiedByReplicaIndex()
    {
        using var builder = CreateBuilder();
        builder.AddExecutable("myapp", "myapp", ".").WithTerminal();

        await using var catalog = await CreateCatalogAsync(builder);

        var entry = Assert.Single(catalog.List());

        Assert.Equal("myapp", entry.Title);
    }

    [Fact]
    public async Task EveryReplicaIsQualifiedWhenResourceHasMoreThanOne()
    {
        // Replica 0 has to be qualified too. Deciding on the index alone would leave the first replica of a
        // scaled-out resource labelled as if it were the only one.
        using var builder = CreateBuilder();
        builder.AddExecutable("myapp", "myapp", ".").WithAnnotation(new ReplicaAnnotation(2)).WithTerminal();

        await using var catalog = await CreateCatalogAsync(builder);

        var titles = catalog.List().OrderBy(e => e.ReplicaIndex).Select(e => e.Title).ToList();

        Assert.Equal(["myapp (replica 0)", "myapp (replica 1)"], titles);
    }

    [Fact]
    public async Task TryGetTerminalRejectsAnAppHostTerminalId()
    {
        using var builder = CreateBuilder();
        builder.AddExecutable("myapp", "myapp", ".").WithTerminal();

        await using var catalog = await CreateCatalogAsync(builder);

        Assert.False(catalog.TryGetTerminal("not-a-resource-terminal", out var terminal));
        Assert.Null(terminal);
    }

    [Fact]
    public async Task TryGetTerminalRejectsAnUnknownReplica()
    {
        using var builder = CreateBuilder();
        builder.AddExecutable("myapp", "myapp", ".").WithTerminal();

        await using var catalog = await CreateCatalogAsync(builder);

        Assert.False(catalog.TryGetTerminal(ResourceTerminalCatalog.BuildId("myapp", 7), out var terminal));
        Assert.Null(terminal);
    }

    [Fact]
    public async Task TryGetTerminalReturnsTheSameHandleForRepeatedLookups()
    {
        // Handles are cached so that repeated automation calls share one connection to the replica rather
        // than adding a peer to its terminal host per call.
        using var builder = CreateBuilder();
        builder.AddExecutable("myapp", "myapp", ".").WithTerminal();

        await using var catalog = await CreateCatalogAsync(builder);
        var id = ResourceTerminalCatalog.BuildId("myapp", 0);

        Assert.True(catalog.TryGetTerminal(id, out var first));
        Assert.True(catalog.TryGetTerminal(id, out var second));

        Assert.Same(first, second);
    }

    [Fact]
    public async Task TryGetTerminalReplacesADisposedHandle()
    {
        using var builder = CreateBuilder();
        builder.AddExecutable("myapp", "myapp", ".").WithTerminal();
        await using var catalog = await CreateCatalogAsync(builder);
        var id = ResourceTerminalCatalog.BuildId("myapp", 0);

        Assert.True(catalog.TryGetTerminal(id, out var first));
        await first!.DisposeAsync();

        Assert.True(catalog.TryGetTerminal(id, out var replacement));
        Assert.NotSame(first, replacement);
        Assert.Equal(id, replacement!.Id);
        Assert.False(Assert.IsType<ResourceAspireTerminal>(replacement).IsDisposed);
        Assert.True(catalog.TryGetTerminal(id, out var repeated));
        Assert.Same(replacement, repeated);
    }

    [Fact]
    public async Task ConcurrentLookupsShareTheReplacementHandle()
    {
        using var builder = CreateBuilder();
        builder.AddExecutable("myapp", "myapp", ".").WithTerminal();
        await using var catalog = await CreateCatalogAsync(builder);
        var id = ResourceTerminalCatalog.BuildId("myapp", 0);
        Assert.True(catalog.TryGetTerminal(id, out var first));
        await first!.DisposeAsync();

        var handles = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() =>
        {
            Assert.True(catalog.TryGetTerminal(id, out var handle));
            return handle;
        })));

        Assert.NotSame(first, handles[0]);
        Assert.All(handles, handle => Assert.Same(handles[0], handle));
    }

    [Fact]
    public async Task CatalogDisposalWaitsForAReplacedHandleToFinishDisconnecting()
    {
        using var builder = CreateBuilder();
        builder.AddExecutable("myapp", "myapp", ".").WithTerminal();
        var logger = new GatedLogger<ResourceAspireTerminal>("Connecting AppHost automation");
        await using var catalog = await CreateCatalogAsync(builder, logger);
        var id = ResourceTerminalCatalog.BuildId("myapp", 0);
        Assert.True(catalog.TryGetTerminal(id, out var first));

        var automation = first!.SendTextAsync("not-delivered");
        try
        {
            await logger.Blocked.DefaultTimeout();
            var firstDisposal = first.DisposeAsync().AsTask();
            Assert.True(catalog.TryGetTerminal(id, out var replacement));
            Assert.NotSame(first, replacement);
            await replacement!.DisposeAsync();

            // The current handle is already disposed. Only the replaced peer can keep shutdown pending.
            var catalogDisposal = catalog.DisposeAsync().AsTask();
            Assert.False(catalogDisposal.IsCompleted);
            logger.Release();
            await catalogDisposal.DefaultTimeout();
            Assert.True(firstDisposal.IsCompleted);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => automation).DefaultTimeout();
        }
        finally
        {
            logger.Release();
            await first.DisposeAsync();
        }
    }

    [Fact]
    public async Task ResourceTerminalReportsResourceOwnership()
    {
        using var builder = CreateBuilder();
        builder.AddExecutable("myapp", "myapp", ".").WithTerminal();

        await using var catalog = await CreateCatalogAsync(builder);

        Assert.True(catalog.TryGetTerminal(ResourceTerminalCatalog.BuildId("myapp", 0), out var terminal));

        Assert.Equal(TerminalOwner.Resource, terminal!.Owner);
        Assert.Equal(TerminalPlacement.ResourceView, terminal.Placement);
        Assert.Equal("myapp", terminal.Title);
    }

    [Fact]
    public async Task TryGetTerminalReturnsFalseAfterDisposal()
    {
        using var builder = CreateBuilder();
        builder.AddExecutable("myapp", "myapp", ".").WithTerminal();

        var catalog = await CreateCatalogAsync(builder);
        await catalog.DisposeAsync();

        Assert.False(catalog.TryGetTerminal(ResourceTerminalCatalog.BuildId("myapp", 0), out var terminal));
        Assert.Null(terminal);
    }

    /// <summary>
    /// Builds the application and publishes <see cref="BeforeStartEvent"/>, which is the seam where
    /// <c>WithTerminal()</c> materializes the per-replica terminal hosts the catalog reads.
    /// </summary>
    private static Task<ResourceTerminalCatalog> CreateCatalogAsync(IDistributedApplicationTestingBuilder builder)
        => CreateCatalogAsync(builder, NullLogger.Instance);

    private static async Task<ResourceTerminalCatalog> CreateCatalogAsync(IDistributedApplicationTestingBuilder builder, ILogger logger)
    {
        await using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        await builder.Eventing.PublishAsync(new BeforeStartEvent(app.Services, model));

        return new ResourceTerminalCatalog(model, logger);
    }

    private IDistributedApplicationTestingBuilder CreateBuilder()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        builder.Configuration[TerminalHostPaths.DirectoryOverrideConfigName] = _terminalDirectory;
        return builder;
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync()
    {
        try
        {
            Directory.Delete(_terminalDirectory, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }

        return ValueTask.CompletedTask;
    }
}
