// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Deck;
using Aspire.Dashboard.Components.Tests.Shared;
using Bunit;
using Microsoft.AspNetCore.Components.Web;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Controls;

public class ComboboxTests : DashboardTestContext
{
    private IRenderedComponent<Combobox<string>> RenderCombobox(out List<string?> valueChanges)
    {
        DashboardSetupHelpers.AddCommonDashboardServices(this);
        DashboardSetupHelpers.SetupCombobox(this);

        var changes = new List<string?>();
        valueChanges = changes;

        var items = new[] { "alpha", "beta", "gamma" };
        return RenderComponent<Combobox<string>>(builder =>
        {
            builder.Add(p => p.Items, items);
            builder.Add(p => p.Value, string.Empty);
            builder.Add(p => p.OptionValue, o => o);
            builder.Add(p => p.OptionText, o => o);
            builder.Add(p => p.ValueChanged, changes.Add);
        });
    }

    [Fact]
    public void DataActiveOption_IsFalse_WhenNoOptionActive()
    {
        var cut = RenderCombobox(out _);

        var input = cut.Find("input.deck-combobox__input");
        Assert.Equal("false", input.GetAttribute("data-active-option"));
    }

    [Fact]
    public void DataActiveOption_BecomesTrue_WhenAnOptionIsActivated()
    {
        // The colocated JS cancels the browser's implicit EditForm submit only while this attribute is
        // "true", so activating an option via ArrowDown must flip it on.
        var cut = RenderCombobox(out _);

        var input = cut.Find("input.deck-combobox__input");
        input.Focus();
        input.KeyDown(new KeyboardEventArgs { Key = "ArrowDown" });

        Assert.Equal("true", cut.Find("input.deck-combobox__input").GetAttribute("data-active-option"));
    }

    [Fact]
    public void DataActiveOption_ReturnsToFalse_WhenPopupClosed()
    {
        var cut = RenderCombobox(out _);

        var input = cut.Find("input.deck-combobox__input");
        input.Focus();
        input.KeyDown(new KeyboardEventArgs { Key = "ArrowDown" });
        Assert.Equal("true", cut.Find("input.deck-combobox__input").GetAttribute("data-active-option"));

        cut.Find("input.deck-combobox__input").KeyDown(new KeyboardEventArgs { Key = "Escape" });
        Assert.Equal("false", cut.Find("input.deck-combobox__input").GetAttribute("data-active-option"));
    }

    [Fact]
    public void Enter_WithActiveOption_SelectsThatOption()
    {
        var cut = RenderCombobox(out var valueChanges);

        var input = cut.Find("input.deck-combobox__input");
        input.Focus();
        input.KeyDown(new KeyboardEventArgs { Key = "ArrowDown" });
        cut.Find("input.deck-combobox__input").KeyDown(new KeyboardEventArgs { Key = "Enter" });

        Assert.Equal("alpha", Assert.Single(valueChanges));
    }

    [Fact]
    public void Enter_WithNoActiveOption_DoesNotSelect_SoTheFormCanSubmit()
    {
        // With nothing active, HasActiveOption is false so data-active-option stays "false"; the JS
        // leaves Enter alone and the enclosing EditForm submits normally. Here we assert the Blazor
        // side does not select/emit a value.
        var cut = RenderCombobox(out var valueChanges);

        var input = cut.Find("input.deck-combobox__input");
        input.Focus();
        Assert.Equal("false", cut.Find("input.deck-combobox__input").GetAttribute("data-active-option"));

        cut.Find("input.deck-combobox__input").KeyDown(new KeyboardEventArgs { Key = "Enter" });

        Assert.Empty(valueChanges);
    }
}
