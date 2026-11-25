// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Layout;
using Aspire.Dashboard.Model;
using Aspire.Tests.Shared.DashboardModel;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Layout;

public class ResourcesLayoutStateClassTests
{
    [Theory]
    [InlineData("error", "state-error")]
    [InlineData("warning", "state-warning")]
    [InlineData("success", "state-success")]
    [InlineData("info", "state-neutral")]
    [InlineData(null, "state-neutral")]
    public void CustomStatesUseTheirPublishedStyle(string? stateStyle, string expected)
    {
        // For example, a watched service whose process crashed publishes the custom state "Crashed" with style "error".
        var resource = ModelTestHelpers.CreateResource(customState: "Crashed", stateStyle: stateStyle);

        Assert.Null(resource.KnownState);
        Assert.Equal(expected, ResourcesLayout.GetStateClass(resource));
    }

    [Fact]
    public void KnownStatesIgnoreTheStyle()
    {
        var resource = ModelTestHelpers.CreateResource(state: KnownResourceState.Running, stateStyle: "error");

        Assert.Equal("state-success", ResourcesLayout.GetStateClass(resource));
    }
}
