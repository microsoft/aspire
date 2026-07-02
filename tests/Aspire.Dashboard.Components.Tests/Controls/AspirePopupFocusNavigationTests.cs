// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Tests.Shared;
using Bunit;
using Microsoft.JSInterop;
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

    [Fact]
    public async Task DisposeAsync_ReleasesDotNetReferenceWhenJSDisposeThrows()
    {
        FluentUISetupHelpers.AddCommonDashboardServices(this);
        JSInterop.SetupVoid("initializeAspirePopupKeyboardNavigation", _ => true);

        // Simulate the browser-side dispose failing with something other than JSDisconnectedException
        // (for example, a transient JS error during teardown). DisposeAsync must still release the
        // DotNetObjectReference, otherwise the GC root keeps the component alive after disposal.
        JSInterop.SetupVoid("disposeAspirePopupKeyboardNavigation", _ => true).SetException(new JSException("dispose failed"));

        var cut = RenderComponent<AspirePopupFocusNavigation>(builder =>
        {
            builder.Add(component => component.AnchorId, "resourceFilterButton");
            builder.Add(component => component.Open, true);
            builder.AddChildContent("<button>First filter</button>");
        });
        await Task.Yield();

        var initInvocation = JSInterop.Invocations.Single(i => i.Identifier == "initializeAspirePopupKeyboardNavigation");
        var dotNetRef = Assert.IsType<DotNetObjectReference<AspirePopupFocusNavigation>>(initInvocation.Arguments[2]);

        await Assert.ThrowsAsync<JSException>(async () => await cut.Instance.DisposeAsync());

        Assert.Throws<ObjectDisposedException>(() => dotNetRef.Value);
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
