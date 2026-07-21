// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Collections.Immutable;
using AngleSharp.Dom;
using Aspire.Dashboard.Components.Resize;
using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Resources;
using Aspire.Tests.Shared.DashboardModel;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.FluentUI.AspNetCore.Components;
using Xunit;
using InteractionInput = Aspire.DashboardService.Proto.V1.InteractionInput;

namespace Aspire.Dashboard.Components.Tests.Controls;

[UseCulture("en-US")]
public class ResourceActionsTests : DashboardTestContext
{
    [Fact]
    public void Render_DesktopActionsProvideKeyboardAccessibleLabels()
    {
        var (cut, restartButton, consoleLogsButton, actionsButton, resourcesLoc, controlsLoc) = RenderDesktopResourceActions();

        Assert.Null(restartButton.GetAttribute("title"));
        Assert.Null(consoleLogsButton.GetAttribute("title"));
        Assert.Null(actionsButton.GetAttribute("title"));

        Assert.Empty(cut.FindComponents<AspireTooltip>());

        restartButton.TriggerEvent("onfocusin", new FocusEventArgs());
        AssertTooltip(cut, restartButton.GetAttribute("id"), "Restart the resource");

        consoleLogsButton.TriggerEvent("onfocusin", new FocusEventArgs());
        AssertTooltip(cut, consoleLogsButton.GetAttribute("id"), resourcesLoc[nameof(Dashboard.Resources.Resources.ResourceActionConsoleLogsText)].Value);

        actionsButton.TriggerEvent("onfocusin", new FocusEventArgs());
        AssertTooltip(cut, actionsButton.GetAttribute("id"), controlsLoc[nameof(ControlsStrings.ActionsButtonText)].Value);
    }

    [Fact]
    public void Render_FocusThenMouseEnterThenMouseLeave_TooltipStaysVisibleUntilFocusout()
    {
        var (cut, restartButton, _, _, _, _) = RenderDesktopResourceActions();
        var restartButtonId = restartButton.GetAttribute("id");

        restartButton.TriggerEvent("onfocusin", new FocusEventArgs());
        AssertTooltip(cut, restartButtonId, "Restart the resource");

        restartButton.TriggerEvent("onmouseenter", new MouseEventArgs());
        AssertTooltip(cut, restartButtonId, "Restart the resource");

        // Moving the mouse away while keyboard focus remains must not hide the tooltip: focus
        // and hover are tracked independently, so only the hover signal clears here.
        restartButton.TriggerEvent("onmouseleave", new MouseEventArgs());
        AssertTooltip(cut, restartButtonId, "Restart the resource");

        // Once focus also leaves, neither signal remains active and the tooltip hides.
        restartButton.TriggerEvent("onfocusout", new FocusEventArgs());
        Assert.Empty(cut.FindComponents<AspireTooltip>());
    }

    [Fact]
    public void Render_HoverOnly_TooltipShowsAndHidesWithMouseEvents()
    {
        var (cut, restartButton, _, _, _, _) = RenderDesktopResourceActions();
        var restartButtonId = restartButton.GetAttribute("id");

        restartButton.TriggerEvent("onmouseenter", new MouseEventArgs());
        AssertTooltip(cut, restartButtonId, "Restart the resource");

        restartButton.TriggerEvent("onmouseleave", new MouseEventArgs());
        Assert.Empty(cut.FindComponents<AspireTooltip>());
    }

    [Fact]
    public void Render_FocusOnly_TooltipShowsAndHidesWithFocusEvents()
    {
        var (cut, restartButton, _, _, _, _) = RenderDesktopResourceActions();
        var restartButtonId = restartButton.GetAttribute("id");

        restartButton.TriggerEvent("onfocusin", new FocusEventArgs());
        AssertTooltip(cut, restartButtonId, "Restart the resource");

        restartButton.TriggerEvent("onfocusout", new FocusEventArgs());
        Assert.Empty(cut.FindComponents<AspireTooltip>());
    }

    private (IRenderedComponent<ResourceActions> Cut, IElement RestartButton, IElement ConsoleLogsButton, IElement ActionsButton, IStringLocalizer<Dashboard.Resources.Resources> ResourcesLoc, IStringLocalizer<ControlsStrings> ControlsLoc) RenderDesktopResourceActions()
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        ResourceSetupHelpers.SetupResourcesPage(this, viewport);

        var command = new CommandViewModel(
            name: CommandViewModel.RestartCommand,
            state: CommandViewModelState.Enabled,
            displayName: "Restart",
            displayDescription: "Restart the resource",
            confirmationMessage: "",
            argumentInputs: ImmutableArray<InteractionInput>.Empty,
            isHighlighted: true,
            iconName: "",
            iconVariant: IconVariant.Regular);
        var resource = ModelTestHelpers.CreateResource(resourceName: "app", commands: [command]);
        var resourceByName = new ConcurrentDictionary<string, ResourceViewModel>([
            new KeyValuePair<string, ResourceViewModel>(resource.Name, resource)
        ]);

        var cut = RenderComponent<ResourceActions>(builder =>
        {
            builder.AddCascadingValue(viewport);
            builder.Add(p => p.CommandSelected, EventCallback.Factory.Create<CommandViewModel>(this, _ => Task.CompletedTask));
            builder.Add(p => p.IsCommandExecuting, (_, _) => false);
            builder.Add(p => p.OnViewDetails, EventCallback.Factory.Create<string?>(this, _ => Task.CompletedTask));
            builder.Add(p => p.Resource, resource);
            builder.Add(p => p.MaxHighlightedCount, 1);
            builder.Add(p => p.ResourceByName, resourceByName);
        });

        var resourcesLoc = Services.GetRequiredService<IStringLocalizer<Dashboard.Resources.Resources>>();
        var controlsLoc = Services.GetRequiredService<IStringLocalizer<ControlsStrings>>();

        var restartButton = cut.Find("fluent-button[aria-label='Restart the resource']");
        var consoleLogsButton = cut.Find($"fluent-button[aria-label='{resourcesLoc[nameof(Dashboard.Resources.Resources.ResourceActionConsoleLogsText)].Value}']");
        var actionsButton = cut.Find($"fluent-button[aria-label='{controlsLoc[nameof(ControlsStrings.ActionsButtonText)].Value}']");

        return (cut, restartButton, consoleLogsButton, actionsButton, resourcesLoc, controlsLoc);
    }

    private static void AssertTooltip(IRenderedFragment cut, string? anchor, string text)
    {
        Assert.NotNull(anchor);
        Assert.Contains(cut.FindComponents<AspireTooltip>(), tooltip => tooltip.Instance.Anchor == anchor && tooltip.Instance.Text == text);
    }
}
