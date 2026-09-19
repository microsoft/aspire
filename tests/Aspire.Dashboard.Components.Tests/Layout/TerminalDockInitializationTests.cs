// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Threading.Channels;
using Aspire.Dashboard.Components.Controls;
using Aspire.Dashboard.Components.Layout;
using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Tests.Shared;
using Aspire.DashboardService.Proto.V1;
using Bunit;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.JSInterop;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Layout;

public partial class TerminalDockTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FirstOpening_DefersResourcesAndRenderingButPreservesRemoteActivation(bool remoteActivation)
    {
        var updates = Channel.CreateUnbounded<WatchTerminalsUpdate>();
        var processed = Channel.CreateUnbounded<WatchTerminalsUpdate>();
        var resources = Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>();
        List<ResourceViewModel> initialResources = [TerminalSetupHelpers.CreateTerminalResource("first-resource")];
        var client = new TestDashboardClient(
            isEnabled: true,
            terminalChannelProvider: () => updates,
            resourceChannelProvider: () => resources,
            initialResources: initialResources)
        {
            OnTerminalUpdateProcessed = update => processed.Writer.TryWrite(update)
        };
        TerminalSetupHelpers.SetupTerminalComponents(this, client);
        var cut = RenderComponent<TerminalDock>();
        var renderCount = cut.RenderCount;
        WatchTerminalsUpdate[] metadata =
        [
            TerminalSetupHelpers.Snapshot("first", "removed"),
            TerminalSetupHelpers.Change(TerminalChangeType.Retitled, "first", "Renamed"),
            TerminalSetupHelpers.Change(TerminalChangeType.Removed, "removed"),
            TerminalSetupHelpers.Change(TerminalChangeType.Added, "second")
        ];

        foreach (var update in metadata)
        {
            await updates.Writer.WriteAsync(update);
            Assert.Same(update, await processed.Reader.ReadAsync().AsTask().DefaultTimeout());
            Assert.Equal(renderCount, cut.RenderCount);
        }
        Assert.Equal(1, client.TerminalSubscriptionCount);
        Assert.Equal(0, client.ResourceSubscriptionCount);
        Assert.Empty(cut.FindAll(".terminal-dock"));
        Assert.Empty(cut.FindComponents<TerminalView>());
        Assert.Equal([], JSInterop.Invocations.Where(IsTerminalModuleImport));

        initialResources.Add(TerminalSetupHelpers.CreateTerminalResource("second-resource"));
        if (remoteActivation)
        {
            await updates.Writer.WriteAsync(TerminalSetupHelpers.Change(TerminalChangeType.Activated, "first", "Renamed"));
            await processed.Reader.ReadAsync().AsTask().DefaultTimeout();
        }
        else
        {
            await cut.InvokeAsync(cut.Instance.ToggleAsync);
        }
        cut.WaitForAssertion(() =>
        {
            Assert.Single(cut.FindAll(".terminal-dock.visible"));
            Assert.Equal(["Renamed", "second"], cut.FindAll("[role=tab]").Select(tab => tab.TextContent.Trim()));
            Assert.Equal(2, cut.FindComponents<TerminalView>().Count);
        });

        await updates.Writer.WriteAsync(TerminalSetupHelpers.Snapshot());
        await processed.Reader.ReadAsync().AsTask().DefaultTimeout();
        cut.WaitForAssertion(() => Assert.Equal(["first-resource", "second-resource"],
            cut.FindAll(".terminal-dock-resource-links a").Select(link => link.TextContent)));
        Assert.Equal(1, client.ResourceSubscriptionCount);

        await cut.InvokeAsync(cut.Instance.ToggleAsync);
        await cut.InvokeAsync(cut.Instance.ToggleAsync);
        Assert.Equal(1, client.TerminalSubscriptionCount);
        Assert.Equal(1, client.ResourceSubscriptionCount);
        await cut.InvokeAsync(() => cut.Instance.DisposeAsync().AsTask()).DefaultTimeout();
        Assert.Equal(0, client.ActiveTerminalSubscriptionCount);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task DisabledOrReadOnly_DoesNotWatchOrInitialize(bool enabled, bool readOnly)
    {
        var client = new TestDashboardClient(isEnabled: enabled, isReadOnly: readOnly);
        TerminalSetupHelpers.SetupTerminalComponents(this, client);
        var cut = RenderComponent<TerminalDock>();

        await cut.InvokeAsync(cut.Instance.ToggleAsync);
        await cut.InvokeAsync(() => cut.Instance.DisposeAsync().AsTask()).DefaultTimeout();

        Assert.Equal(0, client.TerminalSubscriptionCount);
        Assert.Equal(0, client.ResourceSubscriptionCount);
        Assert.Empty(cut.FindAll(".terminal-dock"));
        Assert.Equal([], JSInterop.Invocations.Where(IsTerminalModuleImport));
    }

    [Theory]
    [InlineData("import")]
    [InlineData("registerResizeHandle")]
    [InlineData("registerTabNavigation")]
    public async Task Initialization_OverlappingRendersRegisterListenersOnce(string delayedStage)
    {
        TerminalSetupHelpers.SetupTerminalComponents(this, TerminalSetupHelpers.CreateTerminalDashboardClient());
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var module = new TestJSObjectReference
        {
            BeforeInvokeAsync = identifier =>
            {
                if (identifier == delayedStage)
                {
                    started.TrySetResult();
                    return release.Task;
                }
                return Task.CompletedTask;
            }
        };
        var import = TestJSObjectReference.SetupImport(this, "./Components/Layout/TerminalDock.razor.js");
        if (delayedStage != "import")
        {
            import.SetResult(module);
        }
        var cut = RenderComponent<TerminalDock>();
        try
        {
            await cut.InvokeAsync(cut.Instance.ToggleAsync);
            if (delayedStage != "import")
            {
                await started.Task.DefaultTimeout();
            }
            cut.Render();
            await cut.InvokeAsync(() => cut.Instance.SetHeightAsync(450, 800));
            Assert.Single(import.Invocations);
            Assert.Equal(delayedStage switch
            {
                "import" => [],
                "registerResizeHandle" => new[] { "registerResizeHandle" },
                _ => new[] { "registerResizeHandle", "registerTabNavigation" }
            }, module.Invocations.Select(invocation => invocation.Identifier));
        }
        finally
        {
            if (delayedStage == "import")
            {
                import.SetResult(module);
            }
            release.TrySetResult();
        }

        await AsyncTestHelpers.AssertIsTrueRetryAsync(
            () => Task.FromResult(module.Invocations.Any(invocation => invocation.Identifier == "registerTabNavigation")),
            "Dock initialization did not complete.");
        var reference = Assert.IsType<DotNetObjectReference<TerminalDock>>(module.Invocations.First().Arguments[1]);
        await cut.InvokeAsync(() => cut.Instance.DisposeAsync().AsTask()).DefaultTimeout();
        await cut.InvokeAsync(() => cut.Instance.DisposeAsync().AsTask()).DefaultTimeout();

        Assert.Equal(["registerResizeHandle", "registerTabNavigation", "unregisterResizeHandle", "unregisterTabNavigation"],
            module.Invocations.Select(invocation => invocation.Identifier));
        Assert.Equal(1, module.DisposeCount);
        Assert.Throws<ObjectDisposedException>(() => reference.Value);
    }

    [Theory]
    [InlineData("import")]
    [InlineData("registerResizeHandle")]
    [InlineData("registerTabNavigation")]
    public async Task Disposal_DuringInitialization_JoinsAndReleasesPartialRegistration(string delayedStage)
    {
        TerminalSetupHelpers.SetupTerminalComponents(this, TerminalSetupHelpers.CreateTerminalDashboardClient());
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var module = new TestJSObjectReference
        {
            BeforeInvokeAsync = identifier =>
            {
                if (identifier == delayedStage)
                {
                    started.TrySetResult();
                    return release.Task;
                }
                return Task.CompletedTask;
            }
        };
        var import = TestJSObjectReference.SetupImport(this, "./Components/Layout/TerminalDock.razor.js");
        if (delayedStage != "import")
        {
            import.SetResult(module);
        }
        var cut = RenderComponent<TerminalDock>();
        Task disposing = Task.CompletedTask;
        try
        {
            await cut.InvokeAsync(cut.Instance.ToggleAsync);
            if (delayedStage != "import")
            {
                await started.Task.DefaultTimeout();
            }
            await cut.InvokeAsync(() => { disposing = cut.Instance.DisposeAsync().AsTask(); });
            Assert.False(disposing.IsCompleted);
            await cut.InvokeAsync(() => Assert.Same(disposing, cut.Instance.DisposeAsync().AsTask()));
            Assert.Equal(0, module.DisposeCount);
        }
        finally
        {
            if (delayedStage == "import")
            {
                import.SetResult(module);
            }
            release.TrySetResult();
            await disposing.DefaultTimeout();
        }

        Assert.Equal(delayedStage switch
        {
            "import" => [],
            "registerResizeHandle" => new[] { "registerResizeHandle", "unregisterResizeHandle" },
            _ => new[] { "registerResizeHandle", "registerTabNavigation", "unregisterResizeHandle", "unregisterTabNavigation" }
        }, module.Invocations.Select(invocation => invocation.Identifier));
        Assert.Equal(1, module.DisposeCount);
        if (delayedStage != "import")
        {
            var reference = Assert.IsType<DotNetObjectReference<TerminalDock>>(module.Invocations.First().Arguments[1]);
            Assert.Throws<ObjectDisposedException>(() => reference.Value);
        }
    }

    [Theory]
    [InlineData("import")]
    [InlineData("registerResizeHandle")]
    [InlineData("registerTabNavigation")]
    public async Task InitializationFailure_PropagatesAndDisposalReleasesPartialState(string failedStage)
    {
        TerminalSetupHelpers.SetupTerminalComponents(this, TerminalSetupHelpers.CreateTerminalDashboardClient());
        var failure = new JSException("Dock initialization failed.");
        var module = new TestJSObjectReference
        {
            BeforeInvokeAsync = identifier => identifier == failedStage ? Task.FromException(failure) : Task.CompletedTask
        };
        var import = TestJSObjectReference.SetupImport(this, "./Components/Layout/TerminalDock.razor.js");
        var cut = RenderComponent<TerminalDock>();
        var component = cut.Instance;
        await cut.InvokeAsync(component.ToggleAsync);
        if (failedStage == "import")
        {
            import.SetException(failure);
        }
        else
        {
            import.SetResult(module);
        }

        Assert.Same(failure, await Renderer.UnhandledException.DefaultTimeout());
        await Renderer.Dispatcher.InvokeAsync(() => component.DisposeAsync().AsTask()).DefaultTimeout();

        Assert.Equal(failedStage switch
        {
            "import" => [],
            "registerResizeHandle" => new[] { "registerResizeHandle", "unregisterResizeHandle" },
            _ => new[] { "registerResizeHandle", "registerTabNavigation", "unregisterResizeHandle", "unregisterTabNavigation" }
        }, module.Invocations.Select(invocation => invocation.Identifier));
        Assert.Equal(failedStage == "import" ? 0 : 1, module.DisposeCount);
        if (failedStage != "import")
        {
            var reference = Assert.IsType<DotNetObjectReference<TerminalDock>>(module.Invocations.First().Arguments[1]);
            Assert.Throws<ObjectDisposedException>(() => reference.Value);
        }
    }

    private static bool IsTerminalModuleImport(JSRuntimeInvocation invocation)
        => invocation.Identifier == "import" && invocation.Arguments[0] is string path &&
            (path.EndsWith("/TerminalDock.razor.js", StringComparison.Ordinal) ||
             path.EndsWith("/TerminalView.razor.js", StringComparison.Ordinal) ||
             path.EndsWith("/app-terminalwindow.js", StringComparison.Ordinal));
}
