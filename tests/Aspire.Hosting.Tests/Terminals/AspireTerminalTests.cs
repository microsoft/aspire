// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;
using Aspire.Hosting.Terminals;
using Aspire.Hosting.Utils;

#pragma warning disable ASPIRETERMINAL002 // Test consumer of the experimental AppHost terminal API.

namespace Aspire.Hosting.Tests.Terminals;

[Trait("Partition", "2")]
public class AspireTerminalTests
{
    [Fact]
    public void PublicHandleIsSealedWithOnlyAnInternalConstructor()
    {
        Assert.True(typeof(AspireTerminal).IsPublic);
        Assert.True(typeof(AspireTerminal).IsSealed);
        Assert.Empty(typeof(AspireTerminal).GetConstructors());
        var constructor = Assert.Single(typeof(AspireTerminal).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic));
        Assert.True(constructor.IsAssembly);
        Assert.False(typeof(ITerminalBackend).IsVisible);
        Assert.Equal([typeof(IAsyncDisposable)], typeof(AspireTerminal).GetInterfaces());
    }

    [Fact]
    public async Task HandlePreservesIdentityAndLiveMetadata()
    {
        await using var service = TestTerminalService.Create();
        await using var terminal = service.CreateTerminal(new TerminalLaunchOptions
        {
            Title = "Before",
            Placement = TerminalPlacement.Dialog,
            Command = new TerminalCommand("bash")
        });
        var backend = Assert.IsType<Hex1bAspireTerminal>(terminal.Backend);
        backend.Retitle("After");

        Assert.Same(terminal, backend.Handle);
        Assert.True(service.TryGetTerminal(terminal.Id, out var found));
        Assert.Same(terminal, found);
        Assert.Equal("After", terminal.Title);
        Assert.Equal(TerminalOwner.AppHost, terminal.Owner);
        Assert.Equal(TerminalPlacement.Dialog, terminal.Placement);

        await terminal.DisposeAsync();
        Assert.False(service.TryGetTerminal(terminal.Id, out _));
    }
}
