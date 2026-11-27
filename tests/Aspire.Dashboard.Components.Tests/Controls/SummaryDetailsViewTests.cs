// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Controls;
using Aspire.Dashboard.Components.Resize;
using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Utils;
using Bunit;
using Microsoft.FluentUI.AspNetCore.Components;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Controls;

public class SummaryDetailsViewTests : DashboardTestContext
{
    [Theory]
    [InlineData(Orientation.Horizontal, Orientation.Vertical, 0.75f)]
    [InlineData(Orientation.Vertical, Orientation.Horizontal, 0.25f)]
    public async Task OpenDetails_RestoresSizeForSavedOrientation(Orientation initialOrientation, Orientation savedOrientation, float expectedFraction)
    {
        const string viewKey = "test-view";
        var storage = new TestLocalStorage();
        await storage.SetUnprotectedAsync(BrowserStorageKeys.SplitterOrientationKey(viewKey), savedOrientation);
        await storage.SetUnprotectedAsync(BrowserStorageKeys.SplitterSizeKey(viewKey, Orientation.Horizontal), 0.25f);
        await storage.SetUnprotectedAsync(BrowserStorageKeys.SplitterSizeKey(viewKey, Orientation.Vertical), 0.75f);
        FluentUISetupHelpers.AddCommonDashboardServices(this, localStorage: storage);
        FluentUISetupHelpers.SetupFluentButton(this);
        JSInterop.SetupModule(invocation => invocation.Arguments[0] is string path && path.Contains(nameof(FluentMultiSplitter), StringComparison.Ordinal));

        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        SummaryDetailsSize? restoredSize = null;
        var cut = Render<SummaryDetailsView<string>>(p => p
            .AddCascadingValue(viewport)
            .Add(c => c.ViewKey, viewKey)
            .Add(c => c.Orientation, initialOrientation)
            .Add(c => c.SelectedValue, "details")
            .Add(c => c.ShowDetails, true)
            .Add(c => c.OnResize, size =>
            {
                restoredSize = size;
            }));

        cut.WaitForAssertion(() => Assert.Equal(new SummaryDetailsSize(savedOrientation, expectedFraction), restoredSize));
    }
}
