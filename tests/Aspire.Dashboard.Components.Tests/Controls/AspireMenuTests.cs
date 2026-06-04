// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Model;
using Bunit;
using Microsoft.FluentUI.AspNetCore.Components;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Controls;

public class AspireMenuTests : DashboardTestContext
{
    [Fact]
    public void ClickItem_RestoreFocusOnItemClickTrue_FocusesAnchor()
    {
        FluentUISetupHelpers.SetupFluentUIComponents(this);
        FluentUISetupHelpers.SetupFluentMenu(this);
        FluentUISetupHelpers.SetupFluentAnchoredRegion(this);
        FluentUISetupHelpers.SetupFluentButton(this);

        var anchor = "view-options-button";
        var itemClicked = false;
        var focusElementInvocationHandler = JSInterop.SetupVoid("focusElement", anchor);
        var focusElementInvocationsDuringOnClick = -1;
        var items = new List<MenuButtonItem>
        {
            new()
            {
                Text = "Show hidden resources",
                OnClick = () =>
                {
                    focusElementInvocationsDuringOnClick = focusElementInvocationHandler.Invocations.Count;
                    Assert.True(
                        focusElementInvocationsDuringOnClick == 0,
                        $"Focus should not be restored until item OnClick completes. Actual focusElement invocations during OnClick: {focusElementInvocationsDuringOnClick}.");
                    itemClicked = true;

                    return Task.CompletedTask;
                }
            }
        };

        var cut = Render(builder =>
        {
            builder.OpenComponent<FluentMenuProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<AspireMenuButton>(1);
            builder.AddAttribute(2, nameof(AspireMenuButton.MenuButtonId), anchor);
            builder.AddAttribute(3, nameof(AspireMenuButton.Title), "View options");
            builder.AddAttribute(4, nameof(AspireMenuButton.Items), items);
            builder.AddAttribute(5, nameof(AspireMenuButton.RestoreFocusOnItemClick), true);
            builder.CloseComponent();
        });

        cut.Find($"#{anchor}").Click();
        cut.WaitForElement("fluent-menu-item").Click();

        Assert.True(itemClicked);
        Assert.True(
            focusElementInvocationsDuringOnClick == 0,
            $"Expected zero focusElement invocations during item OnClick, but captured {focusElementInvocationsDuringOnClick}.");
        var invocation = Assert.Single(focusElementInvocationHandler.Invocations);
        Assert.Collection(invocation.Arguments,
            argument => Assert.Equal(anchor, Assert.IsType<string>(argument)));
    }

    [Fact]
    public void ClickItem_RestoreFocusOnItemClickFalse_DoesNotFocusAnchor()
    {
        FluentUISetupHelpers.SetupFluentUIComponents(this);
        FluentUISetupHelpers.SetupFluentMenu(this);
        FluentUISetupHelpers.SetupFluentAnchoredRegion(this);
        FluentUISetupHelpers.SetupFluentButton(this);

        var anchor = "view-options-button";
        var itemClicked = false;
        var items = new List<MenuButtonItem>
        {
            new()
            {
                Text = "Show hidden resources",
                OnClick = () =>
                {
                    itemClicked = true;
                    return Task.CompletedTask;
                }
            }
        };

        var cut = Render(builder =>
        {
            builder.OpenComponent<FluentMenuProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<AspireMenuButton>(1);
            builder.AddAttribute(2, nameof(AspireMenuButton.MenuButtonId), anchor);
            builder.AddAttribute(3, nameof(AspireMenuButton.Title), "View options");
            builder.AddAttribute(4, nameof(AspireMenuButton.Items), items);
            builder.CloseComponent();
        });

        cut.Find($"#{anchor}").Click();
        cut.WaitForElement("fluent-menu-item").Click();

        Assert.True(itemClicked);
        var focusElementInvocations = JSInterop.Invocations
            .Where(invocation => invocation.Identifier == "focusElement")
            .ToArray();
        Assert.Empty(focusElementInvocations);
    }

    [Fact]
    public void OpenMenu_InitializesKeyboardNavigation()
    {
        FluentUISetupHelpers.AddCommonDashboardServices(this);
        FluentUISetupHelpers.SetupFluentUIComponents(this);
        FluentUISetupHelpers.SetupFluentMenu(this);
        FluentUISetupHelpers.SetupFluentAnchoredRegion(this);
        FluentUISetupHelpers.SetupFluentButton(this);

        var anchor = "view-options-button";
        var items = new List<MenuButtonItem>
        {
            new()
            {
                Text = "Show hidden resources",
                OnClick = () => Task.CompletedTask
            }
        };

        var cut = Render(builder =>
        {
            builder.OpenComponent<FluentMenuProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<AspireMenuButton>(1);
            builder.AddAttribute(2, nameof(AspireMenuButton.MenuButtonId), anchor);
            builder.AddAttribute(3, nameof(AspireMenuButton.Title), "View options");
            builder.AddAttribute(4, nameof(AspireMenuButton.Items), items);
            builder.CloseComponent();
        });

        cut.Find($"#{anchor}").Click();

        var invocation = JSInterop.Invocations.Last(invocation => invocation.Identifier == "initializeAspirePopupKeyboardNavigation");
        Assert.Collection(invocation.Arguments,
            argument => Assert.Equal(anchor, Assert.IsType<string>(argument)),
            argument => Assert.False(string.IsNullOrEmpty(Assert.IsType<string>(argument))),
            AssertNotNull,
            argument =>
            {
                var options = Assert.IsAssignableFrom<object>(argument);
                Assert.True(GetTabExitsAlways(options));
            });
    }

    [Fact]
    public async Task OpenContextMenu_DoesNotInitializeKeyboardNavigation()
    {
        FluentUISetupHelpers.AddCommonDashboardServices(this);
        FluentUISetupHelpers.SetupFluentUIComponents(this);
        FluentUISetupHelpers.SetupFluentMenu(this);
        FluentUISetupHelpers.SetupFluentAnchoredRegion(this);

        var items = new List<MenuButtonItem>
        {
            new()
            {
                Text = "Show hidden resources",
                OnClick = () => Task.CompletedTask
            }
        };

        RenderComponent<AspireMenu>(builder =>
        {
            builder.Add(p => p.Anchor, "resources-summary-layout-id");
            builder.Add(p => p.Anchored, false);
            builder.Add(p => p.Open, true);
            builder.Add(p => p.Items, items);
        });
        await Task.Yield();

        Assert.DoesNotContain(JSInterop.Invocations, invocation => invocation.Identifier == "initializeAspirePopupKeyboardNavigation");
    }

    [Fact]
    public async Task OpenMenu_DisposesKeyboardNavigationWithRegisteredAnchorWhenAnchorChanges()
    {
        FluentUISetupHelpers.AddCommonDashboardServices(this);
        FluentUISetupHelpers.SetupFluentUIComponents(this);
        FluentUISetupHelpers.SetupFluentMenu(this);
        FluentUISetupHelpers.SetupFluentAnchoredRegion(this);
        JSInterop.SetupVoid("initializeAspirePopupKeyboardNavigation", _ => true);
        JSInterop.SetupVoid("disposeAspirePopupKeyboardNavigation", _ => true);

        var items = new List<MenuButtonItem>
        {
            new()
            {
                Text = "Show hidden resources",
                OnClick = () => Task.CompletedTask
            }
        };

        var cut = RenderComponent<AspireMenu>(builder =>
        {
            builder.Add(p => p.Anchor, "view-options-button");
            builder.Add(p => p.Open, true);
            builder.Add(p => p.Items, items);
        });
        await Task.Yield();

        var menuId = Assert.IsType<string>(JSInterop.Invocations.Single(i => i.Identifier == "initializeAspirePopupKeyboardNavigation").Arguments[1]);

        cut.SetParametersAndRender(builder =>
        {
            builder.Add(p => p.Anchor, "resource-filter-button");
            builder.Add(p => p.Open, true);
            builder.Add(p => p.Items, items);
        });
        await Task.Yield();

        Assert.Collection(JSInterop.Invocations.Where(i => i.Identifier == "disposeAspirePopupKeyboardNavigation"),
            invocation =>
            {
                Assert.Collection(invocation.Arguments,
                    argument => Assert.Equal("view-options-button", Assert.IsType<string>(argument)),
                    argument => Assert.Equal(menuId, Assert.IsType<string>(argument)));
            });
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
