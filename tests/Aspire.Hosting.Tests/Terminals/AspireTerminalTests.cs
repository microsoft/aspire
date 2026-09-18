// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;
using Aspire.Hosting.Terminals;
using Aspire.Hosting.Utils;

#pragma warning disable ASPIRETERMINAL001 // Test consumer of the experimental AppHost terminal API.

namespace Aspire.Hosting.Tests.Terminals;

[Trait("Partition", "2")]
public class AspireTerminalTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SendKeyAsync_UninitializedKey_DoesNotInvokeBackend(bool canceled)
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

        var exception = Assert.Throws<ArgumentException>(() =>
        {
            _ = terminal.SendKeyAsync(default, cts.Token);
        });

        Assert.Equal("key", exception.ParamName);
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
        var keys = typeof(AspireTerminalKey).GetProperties(BindingFlags.Public | BindingFlags.Static)
            .Where(property => property.PropertyType == typeof(AspireTerminalKey))
            .Select(property => Assert.IsType<AspireTerminalKey>(property.GetValue(null)))
            .SelectMany(key => new[]
            {
                key,
                AspireTerminalKey.Ctrl(key),
                AspireTerminalKey.Shift(key),
                AspireTerminalKey.Alt(key),
                AspireTerminalKey.Ctrl(AspireTerminalKey.Shift(AspireTerminalKey.Alt(key)))
            })
            .ToArray();

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
            Executable = "bash"
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
