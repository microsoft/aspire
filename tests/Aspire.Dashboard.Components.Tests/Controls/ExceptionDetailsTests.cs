// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Resources;
using Bunit;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Controls;

[UseCulture("en-US")]
public class ExceptionDetailsTests : DashboardTestContext
{
    [Fact]
    public void Render_ProvidesTooltipOnlyWhileFocused()
    {
        ResourceSetupHelpers.SetupResourceDetails(this);

        var cut = RenderComponent<ExceptionDetails>(builder =>
        {
            builder.Add(p => p.ExceptionText, "System.InvalidOperationException: Broken");
        });

        var controlsLoc = Services.GetRequiredService<IStringLocalizer<ControlsStrings>>();
        var exceptionDetailsTitle = controlsLoc[nameof(ControlsStrings.ExceptionDetailsTitle)].Value;
        var button = cut.Find($"fluent-button[aria-label='{exceptionDetailsTitle}']");
        var buttonId = button.GetAttribute("id");

        Assert.Null(button.GetAttribute("title"));
        Assert.NotNull(buttonId);
        Assert.Empty(cut.FindComponents<AspireTooltip>());

        button.TriggerEvent("onfocusin", new FocusEventArgs());

        Assert.Contains(cut.FindComponents<AspireTooltip>(), tooltip => tooltip.Instance.Anchor == buttonId && tooltip.Instance.Text == exceptionDetailsTitle);

        button.TriggerEvent("onfocusout", new FocusEventArgs());

        Assert.Empty(cut.FindComponents<AspireTooltip>());
    }
}
