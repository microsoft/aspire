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
        FluentUISetupHelpers.SetupFluentMenu(this);

        var cut = Render(builder =>
        {
            builder.OpenComponent<AspireMenuButton>(0);
            builder.AddAttribute(1, nameof(AspireMenuButton.MenuButtonId), "view-options-button");
            builder.AddAttribute(2, nameof(AspireMenuButton.Text), "View options");
            builder.AddAttribute(3, nameof(AspireMenuButton.Items), new List<MenuButtonItem>
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

        button.Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Equal("false", cut.Find("#view-options-button").GetAttribute("aria-expanded"));
        });
    }
}
