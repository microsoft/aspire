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
        var cut = RenderCombobox(out _);

        var input = cut.Find("input.deck-combobox__input");
        input.Focus();
        input.KeyDown(new KeyboardEventArgs { Key = "ArrowDown" });

        Assert.Equal("true", cut.Find("input.deck-combobox__input").GetAttribute("data-active-option"));
    }

    [Fact]
    public void ArrowUp_OnClosedPopup_OpensAndActivatesLastOption()
    {
        var cut = RenderCombobox(out var valueChanges);

        var input = cut.Find("input.deck-combobox__input");
        input.KeyDown(new KeyboardEventArgs { Key = "ArrowUp" });
        Assert.Equal("true", cut.Find("input.deck-combobox__input").GetAttribute("data-active-option"));

        cut.Find("input.deck-combobox__input").KeyDown(new KeyboardEventArgs { Key = "Enter" });
        Assert.Equal("gamma", Assert.Single(valueChanges));
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
    public void Refocus_AfterPopupClosed_DoesNotRestorePreviousActiveOption()
    {
        var cut = RenderCombobox(out var valueChanges);

        var input = cut.Find("input.deck-combobox__input");
        input.Focus();
        input.KeyDown(new KeyboardEventArgs { Key = "ArrowDown" });
        input.Blur();
        input.Focus();

        Assert.Equal("false", cut.Find("input.deck-combobox__input").GetAttribute("data-active-option"));
        cut.Find("input.deck-combobox__input").KeyDown(new KeyboardEventArgs { Key = "Enter" });
        Assert.Empty(valueChanges);
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
        // The Blazor side must not select or emit a value when no option is active.
        var cut = RenderCombobox(out var valueChanges);

        var input = cut.Find("input.deck-combobox__input");
        input.Focus();
        Assert.Equal("false", cut.Find("input.deck-combobox__input").GetAttribute("data-active-option"));

        cut.Find("input.deck-combobox__input").KeyDown(new KeyboardEventArgs { Key = "Enter" });

        Assert.Empty(valueChanges);
    }
}
