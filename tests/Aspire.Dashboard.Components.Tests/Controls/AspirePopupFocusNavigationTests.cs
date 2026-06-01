// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Tests.Shared;
using Bunit;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Controls;

public class AspirePopupFocusNavigationTests : DashboardTestContext
{
    [Fact]
    public void OpenPopup_InitializesKeyboardNavigation()
    {
        FluentUISetupHelpers.AddCommonDashboardServices(this);
        JSInterop.SetupVoid("initializeAspirePopupKeyboardNavigation", _ => true);

        var anchor = "resourceFilterButton";

        RenderComponent<AspirePopupFocusNavigation>(builder =>
        {
            builder.Add(component => component.AnchorId, anchor);
            builder.Add(component => component.Open, true);
            builder.AddChildContent("<button>First filter</button>");
        });

        var invocation = JSInterop.Invocations.Last(invocation => invocation.Identifier == "initializeAspirePopupKeyboardNavigation");
        Assert.Collection(invocation.Arguments,
            argument => Assert.Equal(anchor, Assert.IsType<string>(argument)),
            argument => Assert.False(string.IsNullOrEmpty(Assert.IsType<string>(argument))),
            AssertNotNull,
            argument =>
            {
                var options = Assert.IsAssignableFrom<object>(argument);
                Assert.False(GetTabExitsAlways(options));
            });
    }

    [Fact]
    public async Task ClosePopup_RaisesOpenChanged()
    {
        FluentUISetupHelpers.AddCommonDashboardServices(this);

        var isOpen = true;
        var cut = RenderComponent<AspirePopupFocusNavigation>(builder =>
        {
            builder.Add(component => component.AnchorId, "resourceFilterButton");
            builder.Add(component => component.Open, false);
            builder.Add(component => component.OpenChanged, value => isOpen = value);
            builder.AddChildContent("<button>First filter</button>");
        });

        await cut.Instance.CloseAsync();

        Assert.False(isOpen);
    }

    private static bool GetTabExitsAlways(object options)
    {
        var property = options.GetType().GetProperty("tabExitsAlways");
        Assert.NotNull(property);

        return Assert.IsType<bool>(property.GetValue(options));
    }

    private static void AssertNotNull(object? argument)
    {
        Assert.NotNull(argument);
    }
}
