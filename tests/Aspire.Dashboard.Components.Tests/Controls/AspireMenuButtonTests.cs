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
        AssertAccessibilityInvocation(expanded: false);

        button.Click();

        cut.WaitForAssertion(() =>
        {
            AssertAccessibilityInvocation(expanded: true);
        });

        button.Click();

        cut.WaitForAssertion(() =>
        {
            AssertAccessibilityInvocation(expanded: false, expectedInvocationCount: 2);
        });

        void AssertAccessibilityInvocation(bool expanded, int expectedInvocationCount = 1)
        {
            var invocations = JSInterop.Invocations
                .Where(i => i.Identifier == "setMenuButtonAccessibility" &&
                    string.Equals(i.Arguments[0]?.ToString(), "view-options-button", StringComparison.Ordinal) &&
                    Equals(i.Arguments[1], expanded))
                .ToList();
            Assert.Equal(expectedInvocationCount, invocations.Count);
        }
    }
}
