// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Resize;
using Aspire.Dashboard.Components.Tests.Shared;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Resize;

public class BrowserDimensionWatcherTests : DashboardTestContext
{
    [Fact]
    public void OnAfterRender_FirstRender_InitializesDimensionsAndResizeListenerInSingleJsCall()
    {
        var dimensionManager = new DimensionManager();
        Services.AddSingleton(dimensionManager);
        JSInterop.Setup<ViewportSize>("window.initializeWindowDimensions", _ => true)
            .SetResult(new ViewportSize(1440, 1000));

        ViewportInformation? reportedViewport = null;
        var cut = Render<BrowserDimensionWatcher>(builder => builder
            .Add(p => p.ViewportInformationChanged, viewport => reportedViewport = viewport));

        var invocation = Assert.Single(JSInterop.Invocations["window.initializeWindowDimensions"]);
        Assert.IsType<DotNetObjectReference<BrowserDimensionWatcher>>(Assert.Single(invocation.Arguments));
        Assert.Empty(JSInterop.Invocations["window.getWindowDimensions"]);
        Assert.Empty(JSInterop.Invocations["window.listenToWindowResize"]);

        Assert.NotNull(reportedViewport);
        Assert.True(reportedViewport.IsDesktop);
        Assert.Equal(reportedViewport, dimensionManager.ViewportInformation);
        Assert.Equal(new ViewportSize(1440, 1000), dimensionManager.ViewportSize);
    }

    [Fact]
    public async Task OnResizeAsync_UpdatesViewportInformationAndHandlesInitialNullViewport()
    {
        var dimensionManager = new DimensionManager();
        Services.AddSingleton(dimensionManager);
        var initializeHandler = JSInterop.Setup<ViewportSize>("window.initializeWindowDimensions", _ => true);

        var reportedViewports = new List<ViewportInformation?>();
        var secondViewportTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cut = Render<BrowserDimensionWatcher>(builder => builder
            .Add(p => p.ViewportInformationChanged, viewport =>
            {
                reportedViewports.Add(viewport);
                if (reportedViewports.Count == 2)
                {
                    secondViewportTcs.TrySetResult();
                }
            }));

        // Resize arriving before initializeWindowDimensions completes must not throw on null ViewportInformation.
        await cut.InvokeAsync(() => cut.Instance.OnResizeAsync(new ViewportSize(600, 800)));

        Assert.Single(reportedViewports);
        Assert.NotNull(reportedViewports[0]);
        Assert.False(reportedViewports[0]!.IsDesktop);
        Assert.Equal(new ViewportSize(600, 800), dimensionManager.ViewportSize);

        await cut.InvokeAsync(() => initializeHandler.SetResult(new ViewportSize(1440, 1000)));
        await secondViewportTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, reportedViewports.Count);
        Assert.NotNull(reportedViewports[1]);
        Assert.True(reportedViewports[1]!.IsDesktop);
        Assert.Equal(new ViewportSize(1440, 1000), dimensionManager.ViewportSize);
    }

    [Fact]
    public void OnAfterRender_JSDisconnected_DoesNotThrow()
    {
        Services.AddSingleton(new DimensionManager());
        JSInterop.Setup<ViewportSize>("window.initializeWindowDimensions", _ => true)
            .SetException(new JSDisconnectedException("The circuit disconnected."));

        var cut = Render<BrowserDimensionWatcher>();

        Assert.Single(JSInterop.Invocations["window.initializeWindowDimensions"]);
        Assert.False(Renderer.UnhandledException.IsCompleted);
    }
}
