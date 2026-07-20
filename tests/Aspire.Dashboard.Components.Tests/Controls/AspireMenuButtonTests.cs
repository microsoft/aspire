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
    public void Disabled_DisablesButton()
    {
        FluentUISetupHelpers.SetupFluentUIComponents(this);
        FluentUISetupHelpers.SetupFluentAnchoredRegion(this);
        FluentUISetupHelpers.SetupFluentButton(this);
        FluentUISetupHelpers.SetupFluentMenu(this);

        var cut = RenderComponent<AspireMenuButton>(builder => builder
            .Add(component => component.MenuButtonId, "disabled-menu-button")
            .Add(component => component.Items, [new MenuButtonItem { Text = "Item" }])
            .Add(component => component.Disabled, true));

        Assert.True(cut.FindComponent<Microsoft.FluentUI.AspNetCore.Components.FluentButton>().Instance.Disabled);
    }

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

        button.Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Equal("false", cut.Find("#view-options-button").GetAttribute("aria-expanded"));
        });
    }

    [Fact]
    public void OnOpening_LoadsItemsBeforeMenuIsDisplayed()
    {
        FluentUISetupHelpers.SetupFluentUIComponents(this);
        FluentUISetupHelpers.SetupFluentAnchoredRegion(this);
        FluentUISetupHelpers.SetupFluentButton(this);
        FluentUISetupHelpers.SetupFluentMenu(this);

        var items = new List<MenuButtonItem>();
        var cut = Render(builder =>
        {
            builder.OpenComponent<Microsoft.FluentUI.AspNetCore.Components.FluentMenuProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<AspireMenuButton>(1);
            builder.AddAttribute(2, nameof(AspireMenuButton.MenuButtonId), "lazy-menu-button");
            builder.AddAttribute(3, nameof(AspireMenuButton.Items), items);
            builder.AddAttribute(4, nameof(AspireMenuButton.OnOpening),
                Microsoft.AspNetCore.Components.EventCallback.Factory.Create(this, () => items.Add(new MenuButtonItem { Text = "Loaded item" })));
            builder.CloseComponent();
        });

        var button = cut.Find("#lazy-menu-button");
        Assert.DoesNotContain("disabled", button.Attributes.Select(attribute => attribute.Name));

        button.Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Equal("true", cut.Find("#lazy-menu-button").GetAttribute("aria-expanded"));
            Assert.Equal("Loaded item", cut.Find("fluent-menu-item").TextContent.Trim());
        });
    }
}
