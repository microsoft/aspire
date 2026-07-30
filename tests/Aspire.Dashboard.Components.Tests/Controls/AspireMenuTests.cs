// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Model;
using Bunit;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Controls;

public class AspireMenuTests : DashboardTestContext
{
    [Fact]
    public void Open_RendersMenuItems_AndClosingRemovesThem()
    {
        FluentUISetupHelpers.AddCommonDashboardServices(this);
        FluentUISetupHelpers.SetupFluentMenu(this);

        var items = new[]
        {
            new MenuButtonItem { Text = "First" },
            new MenuButtonItem { IsDivider = true },
            new MenuButtonItem { Text = "Second" }
        };

        var open = false;
        var cut = RenderComponent<AspireMenu>(builder =>
        {
            builder.Add(p => p.Anchor, "menu-anchor");
            builder.Add(p => p.Items, items);
            builder.Add(p => p.Open, open);
        });

        // Closed: nothing rendered.
        Assert.Empty(cut.FindAll(".deck-menu__item"));

        // Open: menu items (excluding the divider) render.
        cut.SetParametersAndRender(builder => builder.Add(p => p.Open, true));
        Assert.Equal(2, cut.FindAll(".deck-menu__item").Count);
        Assert.Single(cut.FindAll(".deck-menu__divider"));

        // Close again: items removed.
        cut.SetParametersAndRender(builder => builder.Add(p => p.Open, false));
        Assert.Empty(cut.FindAll(".deck-menu__item"));
    }

    [Fact]
    public void ClickItem_RestoreFocusOnItemClickTrue_FocusesAnchor()
    {
        FluentUISetupHelpers.AddCommonDashboardServices(this);
        FluentUISetupHelpers.SetupFluentMenu(this);

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
            builder.OpenComponent<AspireMenuButton>(0);
            builder.AddAttribute(1, nameof(AspireMenuButton.MenuButtonId), anchor);
            builder.AddAttribute(2, nameof(AspireMenuButton.Title), "View options");
            builder.AddAttribute(3, nameof(AspireMenuButton.Items), items);
            builder.AddAttribute(4, nameof(AspireMenuButton.RestoreFocusOnItemClick), true);
            builder.CloseComponent();
        });

        cut.Find($"#{anchor}").Click();
        cut.WaitForElement(".deck-menu__item").Click();

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
        FluentUISetupHelpers.AddCommonDashboardServices(this);
        FluentUISetupHelpers.SetupFluentMenu(this);

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
            builder.OpenComponent<AspireMenuButton>(0);
            builder.AddAttribute(1, nameof(AspireMenuButton.MenuButtonId), anchor);
            builder.AddAttribute(2, nameof(AspireMenuButton.Title), "View options");
            builder.AddAttribute(3, nameof(AspireMenuButton.Items), items);
            builder.CloseComponent();
        });

        cut.Find($"#{anchor}").Click();
        cut.WaitForElement(".deck-menu__item").Click();

        Assert.True(itemClicked);
        var focusElementInvocations = JSInterop.Invocations
            .Where(invocation => invocation.Identifier == "focusElement")
            .ToArray();
        Assert.Empty(focusElementInvocations);
    }
}
