// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using System.Reflection;
using System.Threading.Channels;
using Aspire.Hosting.Terminals;
using Aspire.Hosting.Utils;
using Microsoft.AspNetCore.InternalTesting;

#pragma warning disable ASPIRETERMINAL002 // Test consumer of the experimental AppHost terminal API.

namespace Aspire.Hosting.Tests.Terminals;

/// <summary>
/// Guards <see cref="TerminalService"/>'s registry and dock change fan-out. No test here starts a workload:
/// terminals are lazy, so creation, lookup, removal, and the dock subscription can all be exercised without a
/// PTY, which is what keeps these tests fast and platform-independent.
/// </summary>
[Trait("Partition", "2")]
public class TerminalServiceTests
{
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

    [Fact]
    public void CreateTerminal_RegistersTerminalUnderANonGuessableId()
    {
        var service = TestTerminalService.Create();

        var terminal = CreateInteractionTerminal(service, "Shell");

        Assert.Equal("Shell", terminal.Title);
        Assert.Equal(TerminalSurface.Interaction, terminal.Surface);

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
            () => service.AttachAsync("does-not-exist", stream, CancellationToken.None)).DefaultTimeout();
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

        Assert.Equal(TerminalChangeType.Added, changes.Current.ChangeType);
        Assert.Equal(dock.Id, changes.Current.Terminal.Id);
    }

    [Fact]
    public async Task SubscribeDockTerminals_DoesNotPublishInteractionTerminal()
    {
        var service = TestTerminalService.Create();
        using var subscription = service.SubscribeDockTerminals();

        CreateInteractionTerminal(service, "Dialog");
        var dock = CreateDockTerminal(service, "Dock");

        // The interaction terminal was created first, so if it were published at all it would arrive first.
        await using var changes = subscription.Subscription.GetAsyncEnumerator(CancellationToken.None);
        Assert.True(await changes.MoveNextAsync().AsTask().DefaultTimeout());

        Assert.Equal(dock.Id, changes.Current.Terminal.Id);
    }

    [Fact]
    public void SubscribeDockTerminals_DisposedWithoutEnumerating_ReleasesItsChannelRegistration()
    {
        var service = TestTerminalService.Create();

        // The subscription registers an unbounded channel eagerly, but StreamChanges is an async iterator whose
        // finally only runs once someone calls MoveNextAsync. A caller that faults before it starts enumerating --
        // a viewer that disconnects while the snapshot is being written, for example -- would otherwise leave a
        // channel registered that every subsequent change accumulates into for the lifetime of the AppHost.
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
        Assert.Equal(afterwards.Id, changes.Current.Terminal.Id);
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

    private static IAspireTerminal CreateInteractionTerminal(TerminalService service, string title)
        => service.CreateTerminal(new TerminalLaunchOptions
        {
            Title = title,
            Command = new TerminalCommand("bash"),
            Surface = TerminalSurface.Interaction
        });

    private static IAspireTerminal CreateDockTerminal(TerminalService service, string title)
        => service.CreateTerminal(new TerminalLaunchOptions
        {
            Title = title,
            Command = new TerminalCommand("bash"),
            Surface = TerminalSurface.Dock
        });

    /// <summary>
    /// Reads the private channel set the dock fan-out writes to.
    /// </summary>
    /// <remarks>
    /// Registration is deliberately invisible from the public surface: a leaked channel is silent, and the only
    /// observable symptom is unbounded memory growth over the AppHost's lifetime. Asserting on the set directly is
    /// what makes the leak regression detectable at all -- a test that only checks a later subscription still
    /// receives changes passes whether or not the abandoned channel was released.
    /// </remarks>
    private static ImmutableHashSet<Channel<TerminalChange>> GetOutgoingChannels(TerminalService service)
    {
        var field = typeof(TerminalService).GetField("_outgoingChannels", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);

        return (ImmutableHashSet<Channel<TerminalChange>>)field.GetValue(service)!;
    }
}
