// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Resize;
using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Model;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aspire.Dashboard.Components.Tests;

public class GridColumnManagerTests : DashboardTestContext
{
    [Fact]
    public void Returns_Correct_TemplateColumn_String()
    {
        var dimensionManager = new DimensionManager();
        dimensionManager.InvokeOnViewportInformationChanged(new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false));
        Services.AddSingleton<DimensionManager>(dimensionManager);

        IList<GridColumn> gridColumns = [
            new GridColumn("NoMobile", "1fr", null),
            new GridColumn("Both1", "1fr", "1fr"),
            new GridColumn("Both2", "3fr", "0.5fr"),
            new GridColumn("NoDesktop", null, "2fr"),
            new GridColumn("NoDesktopWithIsVisibleFalse", null, "2fr", IsVisible: () => false),
            new GridColumn("NoDesktopWithIsVisibleTrue", null, "4fr", IsVisible: () => true)
        ];

        var cut = RenderComponent<GridColumnManager>(builder =>
        {
            builder.Add(c => c.Columns, gridColumns);
        });
        var manager = cut.Instance;

        Assert.Equal("1fr 1fr 3fr", manager.GetGridTemplateColumns());

        dimensionManager.InvokeOnViewportInformationChanged(new ViewportInformation(IsDesktop: false, IsUltraLowHeight: true, IsUltraLowWidth: false));
        Assert.Equal("1fr 0.5fr 2fr 4fr", manager.GetGridTemplateColumns());
    }

    [Fact]
    public void Returns_Right_Columns_IsVisible()
    {
        var dimensionManager = new DimensionManager();
        dimensionManager.InvokeOnViewportInformationChanged(new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false));
        Services.AddSingleton<DimensionManager>(dimensionManager);

        IList<GridColumn> gridColumns = [
            new GridColumn("NoMobile", "1fr", null),
            new GridColumn("Both1", "1fr", "1fr"),
            new GridColumn("Both2", "3fr", "0.5fr"),
            new GridColumn("NoDesktop", null, "2fr"),
            new GridColumn("NoDesktopWithIsVisibleFalse", null, "2fr", IsVisible: () => false),
            new GridColumn("NoDesktopWithIsVisibleTrue", null, "4fr", IsVisible: () => true)
        ];

        var cut = RenderComponent<GridColumnManager>(builder =>
        {
            builder.Add(c => c.Columns, gridColumns);
        });
        var manager = cut.Instance;

        Assert.True(manager.IsColumnVisible("NoMobile"));
        Assert.True(manager.IsColumnVisible("Both1"));
        Assert.True(manager.IsColumnVisible("Both2"));
        Assert.False(manager.IsColumnVisible("NoDesktop"));

        dimensionManager.InvokeOnViewportInformationChanged(new ViewportInformation(IsDesktop: false, IsUltraLowHeight: true, IsUltraLowWidth: false));
        Assert.False(manager.IsColumnVisible("NoMobile"));
        Assert.True(manager.IsColumnVisible("Both1"));
        Assert.True(manager.IsColumnVisible("Both2"));
        Assert.True(manager.IsColumnVisible("NoDesktop"));
        Assert.False(manager.IsColumnVisible("NoDesktopWithIsVisibleFalse"));
        Assert.True(manager.IsColumnVisible("NoDesktopWithIsVisibleTrue"));
    }

    [Fact]
    public void WidthFraction_MobileViewOnResize()
    {
        var dimensionManager = new DimensionManager();
        dimensionManager.InvokeOnViewportSizeChanged(new ViewportSize(ViewportInformation.MobileCutoffPixelWidth + 1, 1000));
        dimensionManager.InvokeOnViewportInformationChanged(new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false));
        Services.AddSingleton<DimensionManager>(dimensionManager);

        IList<GridColumn> gridColumns = [
            new GridColumn("NoMobile", "1fr", null),
            new GridColumn("Both1", "1fr", "1fr"),
            new GridColumn("Both2", "3fr", "0.5fr"),
            new GridColumn("NoDesktop", null, "2fr"),
            new GridColumn("NoDesktopWithIsVisibleFalse", null, "2fr", IsVisible: () => false),
            new GridColumn("NoDesktopWithIsVisibleTrue", null, "4fr", IsVisible: () => true)
        ];

        var cut = RenderComponent<GridColumnManager>(builder =>
        {
            builder.Add(c => c.Columns, gridColumns);
        });
        var manager = cut.Instance;

        Assert.Equal("1fr 1fr 3fr", manager.GetGridTemplateColumns());

        // Fraction reduces grid view port to mobile size.
        manager.SetWidthFraction(0.5f);
        Assert.Equal("1fr 0.5fr 2fr 4fr", manager.GetGridTemplateColumns());

        // Increase browser size so grid view port is desktop size.
        dimensionManager.InvokeOnViewportSizeChanged(new ViewportSize((ViewportInformation.MobileCutoffPixelWidth + 1) * 2, 1000));
        Assert.Equal("1fr 1fr 3fr", manager.GetGridTemplateColumns());
    }
}
