// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Pages;
using Aspire.Dashboard.Components.Resize;
using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Model;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Pages;

[UseCulture("en-US")]
public class TracesTests : DashboardTestContext
{
    [Fact]
    public void Render_ClearMenuDownloadItemNotDisplayed()
    {
        SetupTracesServices();

        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);

        var dimensionManager = Services.GetRequiredService<DimensionManager>();
        dimensionManager.InvokeOnViewportInformationChanged(viewport);

        var cut = RenderComponent<Traces>(builder =>
        {
            builder.Add(p => p.ViewportInformation, viewport);
        });

        cut.Find(".clear-button").Click();
        cut.WaitForElement("#clear-menu-all");

        Assert.Empty(cut.FindAll("#clear-menu-download"));
    }

    private void SetupTracesServices()
    {
        FluentUISetupHelpers.SetupFluentDivider(this);
        FluentUISetupHelpers.SetupFluentInputLabel(this);
        FluentUISetupHelpers.SetupFluentDataGrid(this);
        FluentUISetupHelpers.SetupFluentList(this);
        FluentUISetupHelpers.SetupFluentSearch(this);
        FluentUISetupHelpers.SetupFluentKeyCode(this);
        FluentUISetupHelpers.SetupFluentMenu(this);
        FluentUISetupHelpers.SetupFluentToolbar(this);
        FluentUISetupHelpers.SetupFluentAnchoredRegion(this);

        JSInterop.SetupVoid("initializeContinuousScroll");
        JSInterop.SetupVoid("resetContinuousScrollPosition");

        FluentUISetupHelpers.AddCommonDashboardServices(this);
        Services.AddSingleton<ILogger<Traces>>(NullLogger<Traces>.Instance);
        Services.AddSingleton<TracesViewModel>();
    }
}
