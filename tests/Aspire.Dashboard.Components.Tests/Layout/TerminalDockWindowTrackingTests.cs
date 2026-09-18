// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Threading.Channels;
using Aspire.Dashboard.Components.Controls;
using Aspire.Dashboard.Components.Layout;
using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Model;
using Aspire.DashboardService.Proto.V1;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Layout;

public partial class TerminalDockTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReloadRecovery_KeepsPlaceholderUntilExplicitReturnOrConfirmedClosure(bool storageFailure)
    {
        var updates = Channel.CreateUnbounded<WatchTerminalsUpdate>();
        var client = TerminalSetupHelpers.CreateTerminalDashboardClient(terminalChannelProvider: () => updates);
        TerminalSetupHelpers.SetupTerminalComponents(this, client);
        var module = TerminalSetupHelpers.SetupTerminalWindows(this);
        var adoption = module.SetupVoid("adoptTerminalWindows", _ => true);
        var cut = RenderComponent<TerminalDock>();
        await cut.InvokeAsync(cut.Instance.ToggleAsync);
        await updates.Writer.WriteAsync(TerminalSetupHelpers.Snapshot("terminal"));
        var launcher = TerminalSetupHelpers.GetWindowLauncher(this, cut);
        if (storageFailure)
        {
            adoption.SetException(new JSException("Storage denied"));
        }
        else
        {
            await cut.InvokeAsync(() => launcher.OnTerminalWindowOpenedAsync("terminal", "recovering"));
            adoption.SetVoidResult();
        }
        cut.WaitForAssertion(() =>
        {
            Assert.Single(cut.FindAll(".terminal-dock-detached"));
            Assert.Equal(Resources.TerminalStrings.TerminalDockRecoveringWindow,
                cut.Find(".terminal-dock-detached > span").TextContent);
            Assert.Empty(cut.FindComponents<TerminalView>());
        });
        cut.Render();
        await cut.InvokeAsync(cut.Instance.ToggleAsync);
        await cut.InvokeAsync(cut.Instance.ToggleAsync);
        Assert.Equal([], JSInterop.Invocations.Where(i => i.Identifier == "initTerminal"));
        var focus = cut.Find("[data-terminal-window-focus-key]");
        Assert.Equal("terminal", focus.GetAttribute("data-terminal-window-focus-key"));
        Assert.Equal(cut.Find(".terminal-dock-detach").GetAttribute("data-terminal-window-focus-group"),
            focus.GetAttribute("data-terminal-window-focus-group"));

        await cut.InvokeAsync(() => cut.FindAll(".terminal-dock-detached-actions .aspire-button")[1].ClickAsync(new()));
        cut.WaitForAssertion(() => Assert.Single(cut.FindComponents<TerminalView>()));
        Assert.Empty(client.ClosedTerminals);
    }

    [Theory]
    [InlineData("")]
    [InlineData("/aspire/nested")]
    public async Task ReplacementDock_AdoptsWindowsBeforeMountingAnyViewers(string pathBase)
    {
        var updates = Channel.CreateUnbounded<WatchTerminalsUpdate>();
        var client = TerminalSetupHelpers.CreateTerminalDashboardClient(terminalChannelProvider: () => updates);
        Services.AddSingleton<NavigationManager>(new TestNavigationManager($"https://dashboard.example{pathBase}/"));
        TerminalSetupHelpers.SetupTerminalComponents(this, client, pathBase);
        var old = RenderComponent<TerminalDock>();
        await old.InvokeAsync(old.Instance.ToggleAsync);
        await updates.Writer.WriteAsync(TerminalSetupHelpers.Snapshot("active", "inactive"));
        old.WaitForAssertion(() =>
        {
            Assert.Equal(2, old.FindComponents<TerminalView>().Count);
            Assert.Equal(2, JSInterop.Invocations.Count(i => i.Identifier == "initTerminal"));
        });
        var oldLauncher = TerminalSetupHelpers.GetWindowLauncher(this, old);
        await old.InvokeAsync(() => oldLauncher.OnTerminalWindowOpenedAsync("active", "opened"));
        await old.InvokeAsync(() => oldLauncher.OnTerminalWindowOpenedAsync("inactive", "opened"));
        await old.InvokeAsync(() => old.Instance.DisposeAsync().AsTask()).DefaultTimeout();
        Assert.Empty(client.ClosedTerminals);
        Assert.Equal([], JSInterop.Invocations.Where(i => i.Identifier == "closeTerminalWindow"));

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var module = new TestJSObjectReference
        {
            BeforeInvokeAsync = identifier =>
            {
                if (identifier == "adoptTerminalWindows")
                {
                    started.TrySetResult();
                    return release.Task;
                }
                return Task.CompletedTask;
            }
        };
        TestJSObjectReference.SetupImport(this, $"{pathBase}/js/app-terminalwindow.js").SetResult(module);
        var cut = RenderComponent<TerminalDock>();
        TerminalWindowLauncher launcher;
        try
        {
            await cut.InvokeAsync(cut.Instance.ToggleAsync);
            await updates.Writer.WriteAsync(TerminalSetupHelpers.Snapshot("active", "inactive"));
            await started.Task.DefaultTimeout();
            Assert.Equal(2, cut.FindAll("[role=tab]").Count);
            Assert.Empty(cut.FindComponents<TerminalView>());
            Assert.Equal(2, JSInterop.Invocations.Count(i => i.Identifier == "initTerminal"));
            var registration = Assert.Single(module.Invocations, i => i.Identifier == "registerTerminalWindowButton");
            launcher = Assert.IsType<DotNetObjectReference<TerminalWindowLauncher>>(registration.Arguments[2]).Value;
            var adoption = Assert.Single(module.Invocations, i => i.Identifier == "adoptTerminalWindows");
            Assert.Equal(registration.Arguments[1], adoption.Arguments[0]);
            Assert.Equal(["active", "inactive"], Assert.IsType<string[]>(adoption.Arguments[1]));

            // bUnit drives the JS acknowledgements, not real popup handles. The Node suite verifies retention.
            await cut.InvokeAsync(() => launcher.OnTerminalWindowOpenedAsync("active", "adopted"));
            await cut.InvokeAsync(() => launcher.OnTerminalWindowOpenedAsync("inactive", "adopted"));
            Assert.Equal(2, cut.FindAll(".terminal-dock-detached").Count);
            Assert.Empty(cut.FindComponents<TerminalView>());
        }
        finally
        {
            release.TrySetResult();
        }

        await cut.InvokeAsync(() => launcher.OnTerminalWindowClosedAsync("active"));
        cut.WaitForAssertion(() =>
        {
            var view = Assert.Single(cut.FindComponents<TerminalView>()).Instance;
            Assert.Equal("dock:active", view.SizeMemoryKey);
            Assert.True(view.AutoFit);
            Assert.Single(cut.FindAll(".terminal-dock-detached"));
        });
        await cut.FindAll(".terminal-dock-detached-actions .aspire-button")[1].ClickAsync(new());
        cut.WaitForAssertion(() =>
        {
            Assert.Empty(cut.FindAll(".terminal-dock-detached"));
            Assert.Equal([true, false], cut.FindComponents<TerminalView>().Select(c => c.Instance.AutoFit));
        });
        var close = Assert.Single(module.Invocations, i => i.Identifier == "closeTerminalWindow");
        Assert.Equal("inactive", close.Arguments[0]);
        Assert.Empty(client.ClosedTerminals);
        await cut.InvokeAsync(() => cut.Instance.DisposeAsync().AsTask()).DefaultTimeout();
        Assert.Single(module.Invocations, i => i.Identifier == "unregisterTerminalWindowButton");
        Assert.Equal(1, module.DisposeCount);
    }

    [Fact]
    public async Task Adoption_ChangedSnapshotChecksNewIdentitiesAndDoesNotMountRemovedTerminal()
    {
        var updates = Channel.CreateUnbounded<WatchTerminalsUpdate>();
        var client = TerminalSetupHelpers.CreateTerminalDashboardClient(terminalChannelProvider: () => updates);
        TerminalSetupHelpers.SetupTerminalComponents(this, client);
        var started = Channel.CreateUnbounded<bool>();
        var firstRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var adoptionCount = 0;
        var module = new TestJSObjectReference
        {
            BeforeInvokeAsync = identifier =>
            {
                if (identifier == "adoptTerminalWindows")
                {
                    started.Writer.TryWrite(true);
                    return ++adoptionCount == 1 ? firstRelease.Task : secondRelease.Task;
                }
                return Task.CompletedTask;
            }
        };
        TestJSObjectReference.SetupImport(this, "/js/app-terminalwindow.js").SetResult(module);
        var cut = RenderComponent<TerminalDock>();
        try
        {
            await cut.InvokeAsync(cut.Instance.ToggleAsync);
            await updates.Writer.WriteAsync(TerminalSetupHelpers.Snapshot("old-identity"));
            await started.Reader.ReadAsync().AsTask().DefaultTimeout();
            var registration = Assert.Single(module.Invocations, i => i.Identifier == "registerTerminalWindowButton");
            var launcher = Assert.IsType<DotNetObjectReference<TerminalWindowLauncher>>(registration.Arguments[2]).Value;
            await updates.Writer.WriteAsync(TerminalSetupHelpers.Snapshot("new-identity"));
            cut.WaitForAssertion(() => Assert.Equal("new-identity", cut.Find("[role=tab]").TextContent.Trim()));
            await cut.InvokeAsync(() => launcher.OnTerminalWindowOpenedAsync("old-identity", "adopted"));
            var close = Assert.Single(module.Invocations, i => i.Identifier == "closeTerminalWindow");
            Assert.Equal("old-identity", close.Arguments[0]);
            Assert.Empty(cut.FindComponents<TerminalView>());
            Assert.Empty(cut.FindAll(".terminal-dock-detached"));

            firstRelease.SetResult();
            await started.Reader.ReadAsync().AsTask().DefaultTimeout();
            var adoptions = module.Invocations.Where(i => i.Identifier == "adoptTerminalWindows").ToArray();
            Assert.Equal(2, adoptions.Length);
            Assert.Equal(["new-identity"], Assert.IsType<string[]>(adoptions[1].Arguments[1]));
            Assert.Empty(cut.FindComponents<TerminalView>());
            await cut.InvokeAsync(() => launcher.OnTerminalWindowOpenedAsync("new-identity", "adopted"));
        }
        finally
        {
            firstRelease.TrySetResult();
            secondRelease.TrySetResult();
        }

        cut.WaitForAssertion(() =>
        {
            Assert.Empty(cut.FindComponents<TerminalView>());
            Assert.Single(cut.FindAll(".terminal-dock-detached"));
        });
        Assert.Equal([], JSInterop.Invocations.Where(i => i.Identifier == "initTerminal"));
        Assert.Empty(client.ClosedTerminals);
        await cut.InvokeAsync(() => cut.Instance.DisposeAsync().AsTask()).DefaultTimeout();
    }
}
