// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Collections.Immutable;
using Aspire.Dashboard.Components.Resize;
using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Resources;
using Aspire.Tests.Shared.DashboardModel;
using Bunit;
using Microsoft.AspNetCore.Components;
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

        Assert.Null(restartButton.GetAttribute("title"));
        Assert.Null(consoleLogsButton.GetAttribute("title"));
        Assert.Null(actionsButton.GetAttribute("title"));

        AssertTooltip(cut, restartButton.Id, "Restart the resource");
        AssertTooltip(cut, consoleLogsButton.Id, resourcesLoc[nameof(Dashboard.Resources.Resources.ResourceActionConsoleLogsText)].Value);
        AssertTooltip(cut, actionsButton.Id, controlsLoc[nameof(ControlsStrings.ActionsButtonText)].Value);
    }

    private static void AssertTooltip(IRenderedFragment cut, string? anchor, string text)
    {
        Assert.NotNull(anchor);
        Assert.Contains(cut.FindComponents<AspireTooltip>(), tooltip => tooltip.Instance.Anchor == anchor && tooltip.Instance.Text == text);
    }
}
