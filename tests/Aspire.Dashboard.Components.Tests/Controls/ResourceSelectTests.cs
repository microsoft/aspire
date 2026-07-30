// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Controls;
using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Model;
using Bunit;
using Microsoft.AspNetCore.Components.Web;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Controls;

public class ResourceSelectTests : DashboardTestContext
{
    private static List<SelectViewModel<ResourceTypeDetails>> CreateResources()
    {
        return new List<SelectViewModel<ResourceTypeDetails>>
        {
            new() { Name = "alpha", Id = ResourceTypeDetails.CreateSingleton("alpha-1", "alpha") },
            new() { Name = "beta", Id = ResourceTypeDetails.CreateSingleton("beta-1", "beta") },
        };
    }

    private IRenderedComponent<ResourceSelect> RenderSelect(
        List<SelectViewModel<ResourceTypeDetails>> resources,
        SelectViewModel<ResourceTypeDetails>? selected,
        Action<SelectViewModel<ResourceTypeDetails>> onChanged)
    {
        DashboardSetupHelpers.AddCommonDashboardServices(this);

        return RenderComponent<ResourceSelect>(builder =>
        {
            builder.Add(p => p.Resources, resources);
            builder.Add(p => p.SelectedResource, selected);
            builder.Add(p => p.AriaLabel, "Select resource");
            builder.Add(p => p.SelectedResourceChanged, onChanged);
        });
    }

    [Fact]
    public void EnterKeyDown_DoesNotToggle_SoNativeClickIsTheSingleActivation()
    {
        var resources = CreateResources();
        var cut = RenderSelect(resources, resources[0], _ => { });

        var trigger = cut.Find(".deck-resource-select__trigger");
        Assert.Equal("false", trigger.GetAttribute("aria-expanded"));

        // The trigger is a native <button>; the browser synthesizes a click for Enter/Space. The
        // keydown handler must NOT also toggle, otherwise Enter would open then immediately close.
        trigger.KeyDown(new KeyboardEventArgs { Key = "Enter" });
        Assert.Equal("false", cut.Find(".deck-resource-select__trigger").GetAttribute("aria-expanded"));

        // The native activation (click) is the single toggle that opens the listbox.
        cut.Find(".deck-resource-select__trigger").Click();
        Assert.Equal("true", cut.Find(".deck-resource-select__trigger").GetAttribute("aria-expanded"));
    }

    [Fact]
    public void SpaceKeyDown_DoesNotToggle()
    {
        var resources = CreateResources();
        var cut = RenderSelect(resources, resources[0], _ => { });

        var trigger = cut.Find(".deck-resource-select__trigger");
        trigger.KeyDown(new KeyboardEventArgs { Key = " " });

        Assert.Equal("false", cut.Find(".deck-resource-select__trigger").GetAttribute("aria-expanded"));
    }

    [Fact]
    public void EscapeKeyDown_ClosesOpenListbox()
    {
        var resources = CreateResources();
        var cut = RenderSelect(resources, resources[0], _ => { });

        cut.Find(".deck-resource-select__trigger").Click();
        Assert.Equal("true", cut.Find(".deck-resource-select__trigger").GetAttribute("aria-expanded"));

        cut.Find(".deck-resource-select__trigger").KeyDown(new KeyboardEventArgs { Key = "Escape" });
        Assert.Equal("false", cut.Find(".deck-resource-select__trigger").GetAttribute("aria-expanded"));
    }

    [Fact]
    public void ArrowDownKeyDown_MovesSelectionToNextOption()
    {
        var resources = CreateResources();
        SelectViewModel<ResourceTypeDetails>? changedTo = null;
        var cut = RenderSelect(resources, resources[0], vm => changedTo = vm);

        cut.Find(".deck-resource-select__trigger").KeyDown(new KeyboardEventArgs { Key = "ArrowDown" });

        Assert.Same(resources[1], changedTo);
    }

    [Fact]
    public void ArrowUpKeyDown_MovesSelectionToPreviousOption()
    {
        var resources = CreateResources();
        SelectViewModel<ResourceTypeDetails>? changedTo = null;
        var cut = RenderSelect(resources, resources[1], vm => changedTo = vm);

        cut.Find(".deck-resource-select__trigger").KeyDown(new KeyboardEventArgs { Key = "ArrowUp" });

        Assert.Same(resources[0], changedTo);
    }
}
