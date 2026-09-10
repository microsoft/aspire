// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Controls;
using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Model;
using Bunit;
using Microsoft.FluentUI.AspNetCore.Components;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Controls;

[UseCulture("en-US")]
public sealed class ResourceSelectTests : DashboardTestContext
{
    [Fact]
    public void SelectedResource_DisplaysResourceName()
    {
        FluentUISetupHelpers.AddCommonDashboardServices(this);
        FluentUISetupHelpers.SetupFluentList(this);

        var selectedResource = new SelectViewModel<ResourceTypeDetails>
        {
            Id = null,
            Name = "frontend"
        };

        var cut = RenderComponent<ResourceSelect>(parameters => parameters
            .Add(component => component.Resources, [selectedResource])
            .Add(component => component.SelectedResource, selectedResource));

        var select = cut.FindComponent<FluentSelect<SelectViewModel<ResourceTypeDetails>, SelectViewModel<ResourceTypeDetails>>>();
        Assert.NotNull(select.Instance.OptionText);
        Assert.Equal("frontend", select.Instance.OptionText!(selectedResource));
        Assert.Null(select.Instance.OptionText(null));
        Assert.False(select.Instance.OptionDisabled!(null));
        Assert.Equal("frontend", cut.Find("fluent-option").GetAttribute("text"));
    }
}