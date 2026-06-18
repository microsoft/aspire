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
    public async Task ClosePopup_DisposesKeyboardNavigation()
    {
        FluentUISetupHelpers.AddCommonDashboardServices(this);

        var cut = RenderComponent<AspirePopupFocusNavigation>(builder =>
        {
            builder.Add(component => component.AnchorId, "resourceFilterButton");
            builder.Add(component => component.Open, true);
            builder.AddChildContent("<button>First filter</button>");
        });
        await Task.Yield();
        var initializeInvocation = JSInterop.Invocations.Last(invocation => invocation.Identifier == "initializeAspirePopupKeyboardNavigation");
        var popupId = Assert.IsType<string>(initializeInvocation.Arguments[1]);

        cut.SetParametersAndRender(builder =>
        {
            builder.Add(component => component.AnchorId, "resourceFilterButton");
            builder.Add(component => component.Open, false);
            builder.AddChildContent("<button>First filter</button>");
        });
        await Task.Yield();

        var disposeInvocation = Assert.Single(JSInterop.Invocations, invocation => invocation.Identifier == "disposeAspirePopupKeyboardNavigation");
        Assert.Collection(disposeInvocation.Arguments,
            argument => Assert.Equal("resourceFilterButton", Assert.IsType<string>(argument)),
            argument => Assert.Equal(popupId, Assert.IsType<string>(argument)));
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
