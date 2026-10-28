// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;
using Aspire.Hosting.Ats;
using Aspire.Hosting.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;

#pragma warning disable ASPIRETERMINAL001 // Tests for the experimental terminal adapters.

namespace Aspire.Hosting.Tests;

[Trait("Partition", "5")]
public class AtsTerminalExportsTests
{
    [Theory]
    [InlineData(TerminalPlacement.Dock)]
    [InlineData(TerminalPlacement.Dialog)]
    [InlineData(TerminalPlacement.None)]
    public async Task CreateAndLookup_PreserveHandleIdentityAndMetadata(TerminalPlacement placement)
    {
        await using var service = TestTerminalService.Create();
        await using var services = new ServiceCollection().AddSingleton(service).BuildServiceProvider();
        Assert.Same(service, TerminalExports.GetTerminalService(services));

        var terminal = TerminalExports.CreateTerminal(service, new CreateTerminalOptions
        {
            Title = "Guest terminal",
            Executable = "must-not-be-started",
            Placement = placement
        });

        Assert.Same(terminal, TerminalExports.GetTerminal(service, terminal.Id));
        Assert.Equal("Guest terminal", terminal.Title);
        Assert.Equal(TerminalOwner.AppHost, terminal.Owner);
        Assert.Equal(placement, terminal.Placement);
        Assert.Null(TerminalExports.GetTerminal(service, "missing"));

        await TerminalExports.Dispose(terminal).DefaultTimeout();
        Assert.Null(TerminalExports.GetTerminal(service, terminal.Id));
        await TerminalExports.Dispose(terminal).DefaultTimeout();
    }

    [Fact]
    public void LaunchOptions_PreserveNativeDefaults()
    {
        var options = new CreateTerminalOptions { Title = "Terminal", Executable = "node" }.ToOptions();

        Assert.Equal("Terminal", options.Title);
        Assert.Equal("node", options.Executable);
        Assert.Equal(TerminalPlacement.Dock, options.Placement);
        Assert.Equal(80, options.Columns);
        Assert.Equal(24, options.Rows);
        Assert.Null(options.WorkingDirectory);
        Assert.Empty(options.Arguments);
        Assert.Empty(options.EnvironmentVariables);
    }

    [Fact]
    public void LaunchOptions_MapAllSettings()
    {
        var options = new CreateTerminalOptions
        {
            Title = "Setup",
            Executable = "node",
            Arguments = ["setup.mjs", "--interactive"],
            WorkingDirectory = "work",
            EnvironmentVariables = new() { ["TERMINAL_TEST"] = "value" },
            Columns = 120,
            Rows = 32,
            Placement = TerminalPlacement.None
        }.ToOptions();

        Assert.Equal("Setup", options.Title);
        Assert.Equal("node", options.Executable);
        Assert.Equal(["setup.mjs", "--interactive"], options.Arguments);
        Assert.Equal("work", options.WorkingDirectory);
        Assert.Equal(new KeyValuePair<string, string>("TERMINAL_TEST", "value"), Assert.Single(options.EnvironmentVariables));
        Assert.Equal(120, options.Columns);
        Assert.Equal(32, options.Rows);
        Assert.Equal(TerminalPlacement.None, options.Placement);
    }

    [Theory]
    [InlineData(0, 24)]
    [InlineData(80, 0)]
    [InlineData(-1, 24)]
    public void LaunchOptions_RejectInvalidDimensions(int columns, int rows)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CreateTerminalOptions
        {
            Title = "Terminal",
            Executable = "node",
            Columns = columns,
            Rows = rows
        }.ToOptions());
    }

    [Fact]
    public async Task CreateTerminal_RejectsResourcePlacement()
    {
        await using var service = TestTerminalService.Create();

        Assert.Throws<ArgumentOutOfRangeException>(() => TerminalExports.CreateTerminal(service, new CreateTerminalOptions
        {
            Title = "Terminal",
            Executable = "node",
            Placement = TerminalPlacement.ResourceView
        }));
        Assert.Empty(service.ListAll());
    }

    [Fact]
    public async Task Automation_ForwardsArgumentsAndAwaitsDisposal()
    {
        using var cts = new CancellationTokenSource();
        var calls = new List<string>();
        var disposeCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new TestTerminalBackend("terminal")
        {
            OnStart = () => calls.Add("start"),
            OnShow = () => calls.Add("show"),
            OnSendText = (text, token) =>
            {
                Assert.Equal(cts.Token, token);
                calls.Add(text);
                return Task.CompletedTask;
            },
            OnWaitForText = (text, timeout, token) =>
            {
                Assert.Equal(cts.Token, token);
                Assert.Equal(TimeSpan.FromSeconds(2), timeout);
                calls.Add(text);
                return Task.CompletedTask;
            },
            OnGetScreenText = () => "Screen\ntext",
            OnDispose = () => new ValueTask(disposeCompleted.Task)
        };
        var terminal = new AspireTerminal(backend);

        TerminalExports.Start(terminal);
        TerminalExports.Show(terminal);
        await TerminalExports.SendText(terminal, "input", cts.Token);
        await TerminalExports.WaitForText(terminal, "ready", TimeSpan.FromSeconds(2), cts.Token);
        Assert.Equal("Screen\ntext", TerminalExports.GetScreenText(terminal));
        Assert.Equal(["start", "show", "input", "ready"], calls);

        var dispose = TerminalExports.Dispose(terminal);
        Assert.False(dispose.IsCompleted);
        disposeCompleted.SetResult();
        await dispose.DefaultTimeout();
        Assert.True(backend.IsDisposed);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public async Task SendKey_MapsNamesAndModifiers(bool ctrl, bool alt, bool shift)
    {
        using var cts = new CancellationTokenSource();
        var expected = AspireTerminalKey.C;
        if (ctrl)
        {
            expected = AspireTerminalKey.Ctrl(expected);
        }
        if (alt)
        {
            expected = AspireTerminalKey.Alt(expected);
        }
        if (shift)
        {
            expected = AspireTerminalKey.Shift(expected);
        }
        var called = false;
        var terminal = new AspireTerminal(new TestTerminalBackend("terminal")
        {
            OnSendKey = (key, token) =>
            {
                Assert.Equal(expected, key);
                Assert.Equal(cts.Token, token);
                called = true;
                return Task.CompletedTask;
            }
        });

        await TerminalExports.SendKey(terminal, TerminalKeyName.C, new TerminalKeyModifiers { Ctrl = ctrl, Alt = alt, Shift = shift }, cts.Token);

        Assert.True(called);
    }

    [Fact]
    public void KeyNames_CoverNativeKeyCatalog()
    {
        var nativeKeys = typeof(AspireTerminalKey).GetProperties(BindingFlags.Public | BindingFlags.Static)
            .Where(property => property.PropertyType == typeof(AspireTerminalKey))
            .OrderBy(property => property.Name, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(nativeKeys.Select(property => property.Name), Enum.GetNames<TerminalKeyName>().Order(StringComparer.Ordinal));
        foreach (var name in Enum.GetValues<TerminalKeyName>())
        {
            var native = Assert.Single(nativeKeys, property => property.Name == name.ToString());
            Assert.Equal((AspireTerminalKey)native.GetValue(null)!, TerminalExports.ToKey(name));
        }
    }

    [Fact]
    public async Task SendKey_RejectsUnknownKeyBeforeBackendCall()
    {
        var terminal = new AspireTerminal(new TestTerminalBackend("terminal"));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>("key", () => TerminalExports.SendKey(terminal, (TerminalKeyName)(-1)));
    }

    [Fact]
    public async Task Automation_PropagatesCancellationAndTimeout()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var failure = new TimeoutException("Text was not found.");
        var terminal = new AspireTerminal(new TestTerminalBackend("terminal")
        {
            OnSendText = (_, token) => Task.FromCanceled(token),
            OnWaitForText = (_, timeout, _) =>
            {
                Assert.Null(timeout);
                return Task.FromException(failure);
            }
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => TerminalExports.SendText(terminal, "text", cts.Token));
        Assert.Same(failure, await Assert.ThrowsAsync<TimeoutException>(() => TerminalExports.WaitForText(terminal, "missing")));
    }
}
