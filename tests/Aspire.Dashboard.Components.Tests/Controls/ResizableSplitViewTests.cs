// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Controls;
using Aspire.Dashboard.Components.Tests.Shared;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Controls;

public sealed class ResizableSplitViewTests : DashboardTestContext
{
    [Fact]
    public async Task Render_ResizeAndCollapse()
    {
        Services.AddLocalization();
        var module = JSInterop.SetupModule("./Components/Controls/ResizableSplitView.razor.js");
        module.SetupVoid("initializeSplitView", _ => true);
        module.SetupVoid("disposeSplitView", _ => true);
        SplitResizedEventArgs? resized = null;

        var cut = RenderComponent<ResizableSplitView>(builder =>
        {
            builder.Add(p => p.Orientation, SplitOrientation.Vertical);
            builder.Add(p => p.Panel1Percent, 40);
            builder.Add(p => p.Panel1, content => content.AddContent(0, "Summary"));
            builder.Add(p => p.Panel2, content => content.AddContent(0, "Details"));
            builder.Add(p => p.OnResized, args => resized = args);
        });

        Assert.Equal("vertical", cut.Find(".split-view").GetAttribute("data-orientation"));
        Assert.Equal("horizontal", cut.Find(".split-view-bar").GetAttribute("aria-orientation"));
        Assert.Equal("40", cut.Find(".split-view-bar").GetAttribute("aria-valuenow"));

        await cut.InvokeAsync(() => cut.Instance.HandleResizeAsync(200, 300));
        Assert.Equal(new SplitResizedEventArgs(200, 300), resized);

        cut.SetParametersAndRender(builder =>
        {
            builder.Add(p => p.Collapsed, true);
            builder.Add(p => p.Orientation, SplitOrientation.Vertical);
            builder.Add(p => p.Panel1, content => content.AddContent(0, "Summary"));
            builder.Add(p => p.Panel2, content => content.AddContent(0, "Details"));
        });

        Assert.True(cut.Find(".split-view").ClassList.Contains("split-view--collapsed"));
        Assert.NotNull(cut.Find(".split-view-bar").GetAttribute("hidden"));
    }
}
