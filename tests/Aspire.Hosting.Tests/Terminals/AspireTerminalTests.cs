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
    [Theory]
    [InlineData(-1, false)]
    [InlineData(-1, true)]
    [InlineData(int.MaxValue, false)]
    [InlineData(int.MaxValue, true)]
    public async Task SendKeyAsync_InvalidKey_DoesNotInvokeBackend(int value, bool canceled)
    {
        var invoked = false;
        var backend = new TestTerminalBackend("invalid-key")
        {
            OnSendKey = (_, _) =>
            {
                invoked = true;
                return Task.CompletedTask;
            }
        };
        await using var terminal = new AspireTerminal(backend);
        using var cts = new CancellationTokenSource();
        if (canceled)
        {
            cts.Cancel();
        }

        var key = (AspireTerminalKey)value;
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            _ = terminal.SendKeyAsync(key, cts.Token);
        });

        Assert.Equal("key", exception.ParamName);
        Assert.Equal(key, exception.ActualValue);
        Assert.False(invoked);
    }

    [Fact]
    public async Task SendKeyAsync_DeclaredKeys_ForwardKeyAndCancellationToken()
    {
        List<(AspireTerminalKey Key, CancellationToken Token)> calls = [];
        var backend = new TestTerminalBackend("valid-key")
        {
            OnSendKey = (key, token) =>
            {
                calls.Add((key, token));
                return Task.CompletedTask;
            }
        };
        await using var terminal = new AspireTerminal(backend);
        using var cts = new CancellationTokenSource();
        var keys = Enum.GetValues<AspireTerminalKey>();

        foreach (var key in keys)
        {
            await terminal.SendKeyAsync(key, cts.Token);
        }

        Assert.Equal(keys.Select(key => (key, cts.Token)), calls);
    }

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
