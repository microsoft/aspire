// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using System.Globalization;
using System.Reflection;
using System.Threading.Channels;
using Aspire.Hosting.Terminals;
using Aspire.Hosting.Tests.Dcp;
using Aspire.Hosting.Utils;
using Hex1b;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

#pragma warning disable ASPIRETERMINAL002 // Test consumer of the experimental AppHost terminal API.

namespace Aspire.Hosting.Tests.Terminals;

/// <summary>
/// Guards <see cref="TerminalService"/>'s registry and dock change fan-out. Most tests leave terminals lazy,
/// so creation, lookup, removal, and the dock subscription can be exercised without a PTY.
/// </summary>
[Trait("Partition", "2")]
public class TerminalServiceTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    [InlineData(128)]
    public async Task SubscribeDockTerminals_UsesConfiguredCapacityFromAppHost(int capacity)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Configuration[KnownConfigNames.TerminalWatchBufferCapacity] = capacity.ToString(CultureInfo.InvariantCulture);
        await using var app = builder.Build();
        var service = app.Services.GetRequiredService<TerminalService>();
        var terminal = CreateDockTerminal(service, "Terminal");
        using var subscription = service.SubscribeDockTerminals();
        var channel = Assert.Single(GetOutgoingChannels(service));
        terminal.Show();
        for (var i = 1; i < capacity; i++)
        {
            terminal.Retitle($"Revision {i}");
        }
        Assert.Equal(capacity, channel.Reader.Count);

        terminal.Retitle("Recovered");
        Assert.Equal(1, channel.Reader.Count);
        await using var updates = subscription.Subscription.GetAsyncEnumerator();
        Assert.True(await updates.MoveNextAsync().AsTask().DefaultTimeout());
        var snapshot = Assert.IsType<TerminalSnapshot>(updates.Current);
        Assert.Equal(terminal.Id, snapshot.ActivatedTerminalId);
        Assert.Equal(new TerminalDescriptor(terminal.Id, "Recovered"), Assert.Single(snapshot.Terminals));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("invalid")]
    [InlineData("2147483648")]
    [InlineData("")]
    public void Constructor_InvalidWatchBufferCapacity_Throws(string capacity)
    {
        using var configuration = new ConfigurationManager();
        configuration[KnownConfigNames.TerminalWatchBufferCapacity] = capacity;

        Assert.Throws<InvalidOperationException>(() => TestTerminalService.Create(configuration));
    }

    [Fact]
    public void CreateTerminal_NullOptions_Throws()
    {
        var service = TestTerminalService.Create();

        Assert.Throws<ArgumentNullException>(() => service.CreateTerminal(null!));
    }

    [Fact]
    public void CreateTerminal_NullCommand_Throws()
    {
        var service = TestTerminalService.Create();

        Assert.Throws<ArgumentNullException>(() => service.CreateTerminal(new TerminalLaunchOptions
        {
            Title = "Shell",
            Command = null!
        }));
    }

    [Theory]
    [InlineData(TerminalPlacement.ResourceView, false)]
    [InlineData(TerminalPlacement.ResourceView, true)]
    [InlineData((TerminalPlacement)(-1), false)]
    [InlineData((TerminalPlacement)(-1), true)]
    [InlineData((TerminalPlacement)4, false)]
    [InlineData((TerminalPlacement)4, true)]
    public async Task CreateTerminal_UnsupportedPlacement_ThrowsBeforeRegistration(TerminalPlacement placement, bool useBuilder)
    {
        await using var service = TestTerminalService.Create();

        var ex = Assert.Throws<ArgumentOutOfRangeException>(nameof(placement), () => CreateTerminal(service, placement, useBuilder));

        Assert.Equal(placement, ex.ActualValue);
        Assert.Empty(service.ListAll());
    }

    [Theory]
    [InlineData(TerminalPlacement.Dock, false)]
    [InlineData(TerminalPlacement.Dock, true)]
    [InlineData(TerminalPlacement.Dialog, false)]
    [InlineData(TerminalPlacement.Dialog, true)]
    [InlineData(TerminalPlacement.None, false)]
    [InlineData(TerminalPlacement.None, true)]
    public async Task CreateTerminal_SupportedPlacement_RegistersTerminal(TerminalPlacement placement, bool useBuilder)
    {
        await using var service = TestTerminalService.Create();
        await using var terminal = CreateTerminal(service, placement, useBuilder);

        Assert.Equal(TerminalOwner.AppHost, terminal.Owner);
        Assert.Equal(placement, terminal.Placement);
        Assert.True(service.TryGetTerminal(terminal.Id, out var registered));
        Assert.Same(terminal, registered);
        var listing = Assert.Single(service.ListAll());
        Assert.Equal(terminal.Id, listing.Id);
        Assert.Equal(placement, listing.Placement);

        using var subscription = service.SubscribeDockTerminals();
        if (placement == TerminalPlacement.Dock)
        {
            Assert.Equal(terminal.Id, Assert.Single(subscription.InitialState).Id);
        }
        else
        {
            Assert.Empty(subscription.InitialState);
        }
    }

    [Fact]
    public void CreateTerminal_RegistersTerminalUnderANonGuessableId()
    {
        var service = TestTerminalService.Create();

        var terminal = CreateInteractionTerminal(service, "Shell");

        Assert.Equal("Shell", terminal.Title);
        Assert.Equal(TerminalPlacement.Dialog, terminal.Placement);

        // Ids appear in websocket query strings, so they must not be a sequence number a caller could walk.
        Assert.Equal(32, terminal.Id.Length);
        Assert.True(Guid.TryParseExact(terminal.Id, "N", out _));

        Assert.True(service.TryGetTerminal(terminal.Id, out var found));
        Assert.Same(terminal, found);
    }

    [Fact]
    public void CreateTerminal_TwoTerminals_GetDistinctIds()
    {
        var service = TestTerminalService.Create();

        var first = CreateInteractionTerminal(service, "First");
        var second = CreateInteractionTerminal(service, "Second");

        Assert.NotEqual(first.Id, second.Id);
    }

    [Fact]
    public void TryGetTerminal_UnknownId_ReturnsFalse()
    {
        var service = TestTerminalService.Create();

        Assert.False(service.TryGetTerminal("does-not-exist", out var terminal));
        Assert.Null(terminal);
    }

    [Fact]
    public async Task Start_IsIdempotentAndThrowsOnceStopped()
    {
        var service = TestTerminalService.Create();
        var terminal = CreateInteractionTerminal(service, "Shell");

        // Callers decide when the workload spawns, so starting has to tolerate being called more than once --
        // a caller that starts explicitly and then attaches a viewer goes through this twice.
        terminal.Start();
        terminal.Start();

        await terminal.DisposeAsync().DefaultTimeout();

        Assert.Throws<InvalidOperationException>(terminal.Start);
    }

    [Fact]
    public async Task DisposeAsync_RemovesTerminalFromRegistry()
    {
        var service = TestTerminalService.Create();
        var terminal = CreateInteractionTerminal(service, "Shell");

        await terminal.DisposeAsync().DefaultTimeout();

        Assert.False(service.TryGetTerminal(terminal.Id, out _));
    }

    [Fact]
    public async Task AttachAsync_UnknownTerminal_Throws()
    {
        var service = TestTerminalService.Create();
        using var stream = new MemoryStream();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.AttachAsync("does-not-exist", stream, _ => Task.CompletedTask, CancellationToken.None)).DefaultTimeout();
    }

    [Fact]
    public void SubscribeDockTerminals_SnapshotExcludesInteractionTerminals()
    {
        var service = TestTerminalService.Create();
        var dock = CreateDockTerminal(service, "Dock");
        CreateInteractionTerminal(service, "Dialog");

        using var subscription = service.SubscribeDockTerminals();

        // An interaction terminal lives and dies with its dialog, so it must never appear as a dock tab.
        var descriptor = Assert.Single(subscription.InitialState);
        Assert.Equal(dock.Id, descriptor.Id);
    }

    [Fact]
    public async Task SubscribeDockTerminals_PublishesAddedDockTerminal()
    {
        var service = TestTerminalService.Create();
        using var subscription = service.SubscribeDockTerminals();

        Assert.Empty(subscription.InitialState);

        var dock = CreateDockTerminal(service, "Dock");

        await using var changes = subscription.Subscription.GetAsyncEnumerator(CancellationToken.None);
        Assert.True(await changes.MoveNextAsync().AsTask().DefaultTimeout());

        Assert.Equal(new TerminalChange(TerminalChangeType.Added, new(dock.Id, "Dock")), changes.Current);
    }

    [Fact]
    public async Task SubscribeDockTerminals_DoesNotPublishInteractionTerminal()
    {
        var service = TestTerminalService.Create();
        using var subscription = service.SubscribeDockTerminals();

        var dialog = Assert.IsType<Hex1bAspireTerminal>(CreateInteractionTerminal(service, "Dialog"));
        dialog.Retitle("Updated dialog");
        dialog.Show();
        var dock = CreateDockTerminal(service, "Dock");

        // The interaction terminal was created first, so if it were published at all it would arrive first.
        await using var changes = subscription.Subscription.GetAsyncEnumerator(CancellationToken.None);
        Assert.True(await changes.MoveNextAsync().AsTask().DefaultTimeout());

        Assert.Equal(new TerminalChange(TerminalChangeType.Added, new(dock.Id, "Dock")), changes.Current);
    }

    [Fact]
    public async Task SubscribeDockTerminals_OverflowReplacesBacklogWithCurrentSnapshot()
    {
        await using var service = TestTerminalService.Create();
        var first = CreateDockTerminal(service, "First");
        var removed = CreateDockTerminal(service, "Removed");
        CreateInteractionTerminal(service, "Dialog");
        using var subscription = service.SubscribeDockTerminals();
        var channel = Assert.Single(GetOutgoingChannels(service));

        first.Retitle("Updated");
        await removed.DisposeAsync();
        var added = CreateDockTerminal(service, "Added");
        for (var i = 3; i < TerminalService.DefaultDockUpdateBufferCapacity; i++)
        {
            first.Retitle($"Revision {i}");
        }
        Assert.Equal(TerminalService.DefaultDockUpdateBufferCapacity, channel.Reader.Count);

        first.Retitle("Latest");
        Assert.Equal(1, channel.Reader.Count);
        await using var updates = subscription.Subscription.GetAsyncEnumerator();
        Assert.True(await updates.MoveNextAsync().AsTask().DefaultTimeout());
        var snapshot = Assert.IsType<TerminalSnapshot>(updates.Current);
        Assert.Null(snapshot.ActivatedTerminalId);
        Assert.Equal(
            new[] { new TerminalDescriptor(first.Id, "Latest"), new TerminalDescriptor(added.Id, "Added") }.OrderBy(t => t.Id),
            snapshot.Terminals.OrderBy(t => t.Id));

        added.Retitle("After recovery");
        Assert.True(await updates.MoveNextAsync().AsTask().DefaultTimeout());
        Assert.Equal(new TerminalChange(TerminalChangeType.Retitled, new(added.Id, "After recovery")), updates.Current);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SubscribeDockTerminals_RepeatedOverflowPreservesLatestPendingActivation(bool activateAgain)
    {
        await using var service = TestTerminalService.Create();
        var first = CreateDockTerminal(service, "First");
        var second = CreateDockTerminal(service, "Second");
        using var subscription = service.SubscribeDockTerminals();
        var channel = Assert.Single(GetOutgoingChannels(service));
        first.Show();
        second.Show();

        for (var i = 0; i < TerminalService.DefaultDockUpdateBufferCapacity * 4; i++)
        {
            first.Retitle($"Revision {i}");
            if (activateAgain && i == TerminalService.DefaultDockUpdateBufferCapacity + 3)
            {
                first.Show();
            }
            Assert.InRange(channel.Reader.Count, 1, TerminalService.DefaultDockUpdateBufferCapacity);
        }

        await using var updates = subscription.Subscription.GetAsyncEnumerator();
        Assert.True(await updates.MoveNextAsync().AsTask().DefaultTimeout());
        var snapshot = Assert.IsType<TerminalSnapshot>(updates.Current);
        Assert.Equal(activateAgain ? first.Id : second.Id, snapshot.ActivatedTerminalId);
    }

    [Fact]
    public async Task SubscribeDockTerminals_ActivationThatOverflowsIsIncludedInSnapshot()
    {
        await using var service = TestTerminalService.Create();
        var terminal = CreateDockTerminal(service, "Terminal");
        using var subscription = service.SubscribeDockTerminals();
        for (var i = 0; i < TerminalService.DefaultDockUpdateBufferCapacity; i++)
        {
            terminal.Retitle($"Revision {i}");
        }

        terminal.Show();
        await using var updates = subscription.Subscription.GetAsyncEnumerator();
        Assert.True(await updates.MoveNextAsync().AsTask().DefaultTimeout());
        Assert.Equal(terminal.Id, Assert.IsType<TerminalSnapshot>(updates.Current).ActivatedTerminalId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SubscribeDockTerminals_OverflowRetainsRevealIntentWhenActivatedTerminalWasRemoved(bool keepAnotherTerminal)
    {
        await using var service = TestTerminalService.Create();
        var terminal = CreateDockTerminal(service, "Activated");
        var remaining = keepAnotherTerminal ? CreateDockTerminal(service, "Remaining") : null;
        using var subscription = service.SubscribeDockTerminals();
        terminal.Show();
        for (var i = 1; i < TerminalService.DefaultDockUpdateBufferCapacity; i++)
        {
            terminal.Retitle($"Revision {i}");
        }

        await terminal.DisposeAsync();
        await using var updates = subscription.Subscription.GetAsyncEnumerator();
        Assert.True(await updates.MoveNextAsync().AsTask().DefaultTimeout());
        var snapshot = Assert.IsType<TerminalSnapshot>(updates.Current);
        Assert.Equal(terminal.Id, snapshot.ActivatedTerminalId);
        Assert.Equal(remaining is null ? [] : new[] { new TerminalDescriptor(remaining.Id, "Remaining") }, snapshot.Terminals);
    }

    [Fact]
    public async Task SubscribeDockTerminals_SlowSubscriberDoesNotDisruptFastSubscriber()
    {
        await using var service = TestTerminalService.Create();
        var terminal = CreateDockTerminal(service, "Terminal");
        using var slow = service.SubscribeDockTerminals();
        using var fast = service.SubscribeDockTerminals();
        await using var fastUpdates = fast.Subscription.GetAsyncEnumerator();

        for (var i = 0; i < TerminalService.DefaultDockUpdateBufferCapacity * 4; i++)
        {
            var title = $"Revision {i}";
            terminal.Retitle(title);
            Assert.True(await fastUpdates.MoveNextAsync().AsTask().DefaultTimeout());
            Assert.Equal(new TerminalChange(TerminalChangeType.Retitled, new(terminal.Id, title)), fastUpdates.Current);
            Assert.All(GetOutgoingChannels(service),
                channel => Assert.InRange(channel.Reader.Count, 0, TerminalService.DefaultDockUpdateBufferCapacity));
        }

        await using var slowUpdates = slow.Subscription.GetAsyncEnumerator();
        Assert.True(await slowUpdates.MoveNextAsync().AsTask().DefaultTimeout());
        Assert.IsType<TerminalSnapshot>(slowUpdates.Current);
    }

    [Fact]
    public async Task SubscribeDockTerminals_CancellationAfterOverflowReleasesRegistration()
    {
        await using var service = TestTerminalService.Create();
        var terminal = CreateDockTerminal(service, "Terminal");
        using var subscription = service.SubscribeDockTerminals();
        for (var i = 0; i <= TerminalService.DefaultDockUpdateBufferCapacity; i++)
        {
            terminal.Show();
        }

        using var cts = new CancellationTokenSource();
        await using var updates = subscription.Subscription.GetAsyncEnumerator(cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => updates.MoveNextAsync().AsTask()).DefaultTimeout();
        Assert.Empty(GetOutgoingChannels(service));
    }

    [Fact]
    public async Task SubscribeDockTerminals_DisposalCompletesPendingRead()
    {
        await using var service = TestTerminalService.Create();
        using var subscription = service.SubscribeDockTerminals();
        await using var updates = subscription.Subscription.GetAsyncEnumerator();
        var next = updates.MoveNextAsync().AsTask();
        subscription.Dispose();

        Assert.False(await next.DefaultTimeout());
        Assert.Empty(GetOutgoingChannels(service));
    }

    [Fact]
    public async Task SubscribeDockTerminals_ShutdownWithBacklogStaysBoundedAndClearsInventory()
    {
        await using var service = TestTerminalService.Create();
        for (var i = 0; i < TerminalService.DefaultDockUpdateBufferCapacity + 5; i++)
        {
            CreateDockTerminal(service, $"Terminal {i}");
        }
        using var subscription = service.SubscribeDockTerminals();
        var channel = Assert.Single(GetOutgoingChannels(service));
        var inventory = subscription.InitialState.ToDictionary(t => t.Id);

        await service.DisposeAsync();
        Assert.InRange(channel.Reader.Count, 1, TerminalService.DefaultDockUpdateBufferCapacity);
        var recovered = false;
        await foreach (var update in subscription.Subscription)
        {
            if (update is TerminalSnapshot snapshot)
            {
                recovered = true;
                inventory = snapshot.Terminals.ToDictionary(t => t.Id);
            }
            else
            {
                var change = Assert.IsType<TerminalChange>(update);
                Assert.Equal(TerminalChangeType.Removed, change.ChangeType);
                inventory.Remove(change.Terminal.Id);
            }
        }
        Assert.True(recovered);
        Assert.Empty(inventory);
        Assert.Empty(GetOutgoingChannels(service));
    }

    [Fact]
    public void SubscribeDockTerminals_DisposedWithoutEnumerating_ReleasesItsChannelRegistration()
    {
        var service = TestTerminalService.Create();

        // The subscription registers its channel eagerly, but StreamChanges is an async iterator whose
        // finally only runs once someone calls MoveNextAsync. A caller that faults before it starts enumerating --
        // a viewer that disconnects while the snapshot is being written, for example -- would otherwise leave a
        // channel and its buffer registered for the lifetime of the AppHost.
        var subscription = service.SubscribeDockTerminals();
        Assert.Single(GetOutgoingChannels(service));

        subscription.Dispose();
        Assert.Empty(GetOutgoingChannels(service));

        // Removal is idempotent, so the iterator's finally and an explicit Dispose can both run.
        subscription.Dispose();
        Assert.Empty(GetOutgoingChannels(service));
    }

    [Fact]
    public async Task SubscribeDockTerminals_DisposedWithoutEnumerating_StopsReceivingChanges()
    {
        var service = TestTerminalService.Create();

        var abandoned = service.SubscribeDockTerminals();
        abandoned.Dispose();

        for (var i = 0; i < 5; i++)
        {
            CreateDockTerminal(service, $"Dock {i}");
        }

        // Nothing was written to the released channel, so the fan-out no longer holds those changes anywhere.
        Assert.Empty(GetOutgoingChannels(service));

        // A subscription taken afterwards still works, and sees the dock terminals in its snapshot rather than
        // replaying them as changes.
        using var live = service.SubscribeDockTerminals();
        Assert.Equal(5, live.InitialState.Length);

        var afterwards = CreateDockTerminal(service, "Later");

        await using var changes = live.Subscription.GetAsyncEnumerator(CancellationToken.None);
        Assert.True(await changes.MoveNextAsync().AsTask().DefaultTimeout());
        Assert.Equal(new TerminalChange(TerminalChangeType.Added, new(afterwards.Id, "Later")), changes.Current);
    }

    [Fact]
    public async Task DisposeAsync_TearsDownRegisteredTerminals()
    {
        var service = TestTerminalService.Create();
        var terminal = CreateInteractionTerminal(service, "Shell");

        await service.DisposeAsync().DefaultTimeout();

        Assert.False(service.TryGetTerminal(terminal.Id, out _));
    }

    [Fact]
    public async Task CreateTerminal_AfterDispose_Throws()
    {
        var service = TestTerminalService.Create();
        await service.DisposeAsync().DefaultTimeout();

        Assert.Throws<ObjectDisposedException>(() => CreateInteractionTerminal(service, "Shell"));
    }

    [Fact]
    public async Task SubscribeDockTerminals_DuringCreation_DoesNotReplaySnapshotAsAdded()
    {
        var logger = new GatedLogger<TerminalService>("Created Dock terminal");
        await using var service = new TerminalService(logger, new ConfigurationBuilder().Build());
        var create = Task.Run(() => CreateDockTerminal(service, "Dock"));
        try
        {
            // The log is a deterministic interleaving point. Registry mutation and publication must
            // already agree before any other code, including a logger, can subscribe.
            await logger.Blocked.DefaultTimeout();
            using var subscription = service.SubscribeDockTerminals();
            var descriptor = Assert.Single(subscription.InitialState);
            logger.Release();
            Assert.Equal(descriptor.Id, (await create.DefaultTimeout()).Id);

            await service.DisposeAsync();
            var changes = new List<TerminalUpdate>();
            await foreach (var change in subscription.Subscription)
            {
                changes.Add(change);
            }

            var removed = Assert.IsType<TerminalChange>(Assert.Single(changes));
            Assert.Equal(TerminalChangeType.Removed, removed.ChangeType);
            Assert.Equal(descriptor.Id, removed.Terminal.Id);
        }
        finally
        {
            logger.Release();
            await create.DefaultTimeout();
        }
    }

    [Fact]
    public async Task SubscribeDockTerminals_DuringRemoval_DoesNotReceiveRemovalForAnAbsentSnapshotEntry()
    {
        var logger = new GatedLogger<TerminalService>("Removed terminal");
        await using var service = new TerminalService(logger, new ConfigurationBuilder().Build());
        var terminal = CreateDockTerminal(service, "Dock");
        var remove = Task.Run(async () => await terminal.DisposeAsync());
        try
        {
            await logger.Blocked.DefaultTimeout();
            using var subscription = service.SubscribeDockTerminals();
            Assert.Empty(subscription.InitialState);
            logger.Release();
            await remove.DefaultTimeout();

            await service.DisposeAsync();
            await using var changes = subscription.Subscription.GetAsyncEnumerator();
            Assert.False(await changes.MoveNextAsync().AsTask().DefaultTimeout());
        }
        finally
        {
            logger.Release();
            await remove.DefaultTimeout();
        }
    }

    [Fact]
    public async Task CreateTerminal_ConcurrentWithShutdown_DoesNotLeaveARegisteredTerminal()
    {
        for (var i = 0; i < 100; i++)
        {
            await using var service = TestTerminalService.Create();
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var create = Task.Run(async () =>
            {
                await start.Task;
                try
                {
                    return CreateDockTerminal(service, "Dock");
                }
                catch (ObjectDisposedException)
                {
                    return null;
                }
            });
            var shutdown = Task.Run(async () =>
            {
                await start.Task;
                await service.DisposeAsync();
            });
            start.SetResult();
            await Task.WhenAll(create, shutdown).DefaultTimeout();

            try
            {
                Assert.Empty(service.ListAll());
                Assert.Throws<ObjectDisposedException>(() => CreateDockTerminal(service, "Late"));
            }
            finally
            {
                if (await create is { } terminal)
                {
                    await terminal.DisposeAsync().DefaultTimeout();
                }
            }
        }
    }

    [Fact]
    public async Task SubscribeDockTerminals_AfterShutdown_ReturnsCompletedEmptySubscription()
    {
        await using var service = TestTerminalService.Create();
        CreateDockTerminal(service, "Dock");
        await service.DisposeAsync();

        using var subscription = service.SubscribeDockTerminals();
        Assert.Empty(subscription.InitialState);
        Assert.Empty(GetOutgoingChannels(service));
        await using var changes = subscription.Subscription.GetAsyncEnumerator();
        Assert.False(await changes.MoveNextAsync().AsTask().DefaultTimeout());
    }

    [Fact]
    public void ListAll_IncludesTerminalsRegardlessOfPlacement()
    {
        // A terminal driven only through automation is never displayed, so a listing keyed off the dock would
        // miss it entirely. `aspire terminal ps` is meant to answer "what exists", not "what is on screen".
        var service = TestTerminalService.Create();
        var dock = CreateDockTerminal(service, "Dock");
        var dialog = CreateInteractionTerminal(service, "Dialog");
        var hidden = service.CreateTerminal(new TerminalLaunchOptions
        {
            Title = "Automation",
            Command = new TerminalCommand("bash"),
            Placement = TerminalPlacement.None
        });

        var listings = service.ListAll();

        Assert.Equal(
            new[] { dock.Id, dialog.Id, hidden.Id }.OrderBy(id => id, StringComparer.Ordinal),
            listings.Select(l => l.Id).OrderBy(id => id, StringComparer.Ordinal));
        Assert.All(listings, l => Assert.Equal(TerminalOwner.AppHost, l.Owner));
        Assert.All(listings, l => Assert.Null(l.ResourceName));
    }

    [Fact]
    public void ListAll_CarriesPlacementAndTitle()
    {
        var service = TestTerminalService.Create();
        CreateDockTerminal(service, "Build output");

        var listing = Assert.Single(service.ListAll());

        Assert.Equal("Build output", listing.Title);
        Assert.Equal(TerminalPlacement.Dock, listing.Placement);
    }

    [Fact]
    public async Task ListAll_DropsRemovedTerminals()
    {
        var service = TestTerminalService.Create();
        var terminal = CreateDockTerminal(service, "Dock");

        await terminal.DisposeAsync().DefaultTimeout();

        Assert.Empty(service.ListAll());
    }

    [Fact]
    public void ListAll_WithoutAResourceCatalogReturnsOnlyAppHostTerminals()
    {
        // ResourceTerminals is left null when the AppHost has no model yet, which must degrade to "no resource
        // terminals" rather than faulting the listing.
        var service = TestTerminalService.Create();
        CreateDockTerminal(service, "Dock");

        Assert.Null(service.ResourceTerminals);
        Assert.Single(service.ListAll());
    }

    private static IAspireTerminal CreateTerminal(TerminalService service, TerminalPlacement placement, bool useBuilder)
        => useBuilder
            ? service.CreateTerminal("Shell", placement, Hex1bTerminal.CreateBuilder().WithPtyProcess("bash"))
            : service.CreateTerminal(new TerminalLaunchOptions
            {
                Title = "Shell",
                Command = new TerminalCommand("bash"),
                Placement = placement
            });

    private static IAspireTerminal CreateInteractionTerminal(TerminalService service, string title)
        => service.CreateTerminal(new TerminalLaunchOptions
        {
            Title = title,
            Command = new TerminalCommand("bash"),
            Placement = TerminalPlacement.Dialog
        });

    private static Hex1bAspireTerminal CreateDockTerminal(TerminalService service, string title)
        => Assert.IsType<Hex1bAspireTerminal>(service.CreateTerminal(new TerminalLaunchOptions
        {
            Title = title,
            Command = new TerminalCommand("bash"),
            Placement = TerminalPlacement.Dock
        }));

    /// <summary>
    /// Reads the private channel set the dock fan-out writes to.
    /// </summary>
    /// <remarks>
    /// Registration is deliberately invisible from the public surface: a leaked channel is silent, and the only
    /// observable symptom is retained buffers as abandoned subscriptions accumulate. Asserting on the set directly is
    /// what makes the leak regression detectable at all -- a test that only checks a later subscription still
    /// receives changes passes whether or not the abandoned channel was released.
    /// </remarks>
    private static ImmutableHashSet<Channel<TerminalUpdate>> GetOutgoingChannels(TerminalService service)
    {
        var field = typeof(TerminalService).GetField("_outgoingChannels", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);

        return (ImmutableHashSet<Channel<TerminalUpdate>>)field.GetValue(service)!;
    }
}
