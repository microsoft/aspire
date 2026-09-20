// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Tests.Shared;
using Bunit;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Controls;

public class TotalItemsFooterTests : DashboardTestContext
{
    [Fact]
    public async Task UpdateDisplayedCount_WithDisplayedItemCount_DisplaysPartialCount()
    {
        var cut = RenderComponent<TotalItemsFooter>(builder => builder
            .Add(p => p.TotalItemCount, 0)
            .Add(p => p.SingularText, "Showing <strong>{0} item</strong>")
            .Add(p => p.PluralText, "Showing <strong>{0} items</strong>")
            .Add(p => p.PartialText, "Showing <strong>{0} of {1} logs</strong>. Use filters to narrow the results"));

        await cut.InvokeAsync(() => cut.Instance.UpdateDisplayedCount(totalItemCount: 600_000, displayedItemCount: 200_000));

        Assert.Equal("Showing 200000 of 600000 logs. Use filters to narrow the results", cut.Find(".result-count").TextContent.Trim());
        Assert.Equal("200000 of 600000 logs", cut.Find(".result-count strong").TextContent);

        await cut.InvokeAsync(() => cut.Instance.UpdateDisplayedCount(totalItemCount: 100, displayedItemCount: null));

        Assert.Equal("Showing 100 items", cut.Find(".result-count").TextContent);

        await cut.InvokeAsync(() => cut.Instance.UpdateDisplayedCount(totalItemCount: 100, displayedItemCount: 100));

        Assert.Equal("Showing 100 items", cut.Find(".result-count").TextContent);
    }
}