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
}
