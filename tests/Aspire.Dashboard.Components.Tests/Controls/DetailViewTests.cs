// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Controls;
using Aspire.Dashboard.Components.Resize;
using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Resources;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.FluentUI.AspNetCore.Components;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Controls;

[UseCulture("en-US")]
public class DetailViewTests : DashboardTestContext
{
    [Fact]
    public void Render_HeaderActionsProvideKeyboardAccessibleTooltips()
    {
        FluentUISetupHelpers.AddCommonDashboardServices(this);

        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var cut = RenderComponent<DetailView>(builder =>
        {
            builder.AddCascadingValue(viewport);
            builder.Add(p => p.DetailsTitle, "Project: app");
            builder.Add(p => p.Details, (RenderFragment)(contentBuilder => contentBuilder.AddContent(0, "Details")));
            builder.Add(p => p.HandleToggleOrientation, () => Task.CompletedTask);
            builder.Add(p => p.HandleDismissAsync, () => Task.CompletedTask);
            builder.Add(p => p.Orientation, Orientation.Horizontal);
        });

        var loc = Services.GetRequiredService<IStringLocalizer<ControlsStrings>>();

        AssertTooltip(cut, loc[nameof(ControlsStrings.SummaryDetailsViewSplitHorizontal)].Value);
        AssertTooltip(cut, loc[nameof(ControlsStrings.SummaryDetailsViewCloseView)].Value);

        Assert.All(cut.FindAll(".header-actions fluent-button"), button => Assert.Null(button.GetAttribute("title")));
    }

    private static void AssertTooltip(IRenderedFragment cut, string text)
    {
        Assert.Contains(cut.FindComponents<AspireTooltip>(), tooltip => tooltip.Instance.Text == text);
    }
}
