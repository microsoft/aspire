// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Model;
using Bunit;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Controls;

public class AspireMenuButtonTests : DashboardTestContext
{
    [Fact]
    public void ToggleMenu_UpdatesAriaExpandedState()
    {
        FluentUISetupHelpers.SetupFluentUIComponents(this);
        FluentUISetupHelpers.SetupFluentAnchoredRegion(this);
        FluentUISetupHelpers.SetupFluentButton(this);
        FluentUISetupHelpers.SetupFluentMenu(this);

        var cut = Render(builder =>
        {
            builder.OpenComponent<Microsoft.FluentUI.AspNetCore.Components.FluentMenuProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<AspireMenuButton>(1);
            builder.AddAttribute(2, nameof(AspireMenuButton.MenuButtonId), "view-options-button");
            builder.AddAttribute(3, nameof(AspireMenuButton.Text), "View options");
            builder.AddAttribute(4, nameof(AspireMenuButton.Items), new List<MenuButtonItem>
            {
                new MenuButtonItem
                {
                    Text = "Show hidden resources"
                }
            });
            builder.CloseComponent();
        });

        var button = cut.Find("#view-options-button");
        Assert.Equal("false", button.GetAttribute("aria-expanded"));

        button.Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Equal("true", cut.Find("#view-options-button").GetAttribute("aria-expanded"));
        });

        cut.FindComponent<Microsoft.FluentUI.AspNetCore.Components.FluentMenuItem>()
            .Find("fluent-menu-item")
            .Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Equal("false", cut.Find("#view-options-button").GetAttribute("aria-expanded"));
        });
    }
}
