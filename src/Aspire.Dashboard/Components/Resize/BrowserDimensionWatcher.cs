// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Aspire.Dashboard.Components.Resize;

public class BrowserDimensionWatcher : ComponentBase, IDisposable
{
    [Parameter]
    public ViewportInformation? ViewportInformation { get; set; }

    [Parameter]
    public EventCallback<ViewportInformation?> ViewportInformationChanged { get; set; }

    [Inject]
    public required IJSRuntime JS { get; init; }

    [Inject]
    public required DimensionManager DimensionManager { get; init; }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            try
            {
                var viewportSize = await JS.InvokeAsync<ViewportSize>("window.getWindowDimensions");
                DimensionManager.InvokeOnViewportSizeChanged(viewportSize);
                ViewportInformation = ViewportInformation.GetViewportInformation(viewportSize);
                DimensionManager.InvokeOnViewportInformationChanged(ViewportInformation);

                await ViewportInformationChanged.InvokeAsync(ViewportInformation);

                await JS.InvokeVoidAsync("window.listenToWindowResize", DotNetObjectReference.Create(this));
            }
            catch (JSDisconnectedException)
            {
                // Per https://learn.microsoft.com/aspnet/core/blazor/javascript-interoperability/?view=aspnetcore-7.0#javascript-interop-calls-without-a-circuit
                // this is one of the calls that will fail if the circuit is disconnected, and we just need to catch the exception so it doesn't pollute the logs
            }
        }

        await base.OnAfterRenderAsync(firstRender);
    }

    [JSInvokable]
    public async Task OnResizeAsync(ViewportSize viewportSize)
    {
        DimensionManager.InvokeOnViewportSizeChanged(viewportSize);

        var newViewportInformation = ViewportInformation.GetViewportInformation(viewportSize);

        if (newViewportInformation.IsDesktop != ViewportInformation!.IsDesktop
            || newViewportInformation.IsUltraLowHeight != ViewportInformation.IsUltraLowHeight
            || newViewportInformation.IsUltraLowWidth != ViewportInformation.IsUltraLowWidth)
        {
            ViewportInformation = newViewportInformation;
            // A re-render happens on components after ViewportInformationChanged is invoked
            // we should invoke InvokeOnViewportInformationChanged first so that listeners of it
            // that are outside of the UI tree have the current viewport kind internally when components
            // call them
            DimensionManager.InvokeOnViewportInformationChanged(newViewportInformation);
            await ViewportInformationChanged.InvokeAsync(newViewportInformation);
        }
    }

    public void Dispose()
    {
    }
}

public record ViewportSize(int Width, int Height);
