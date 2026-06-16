// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Threading.Channels;
using AngleSharp.Dom;
using Aspire.Dashboard.Components.Dialogs;
using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Configuration;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Model.ManageData;
using Aspire.Dashboard.Tests.Shared;
using Aspire.Tests.Shared.DashboardModel;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.FluentUI.AspNetCore.Components;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Dialogs;

[UseCulture("en-US")]
public sealed class ManageDataDialogTests : DashboardTestContext
{
    [Fact]
    public async Task Render_SelectionControlsExposeAccessibleNamesCheckboxRoleAndState()
    {
        var basketResource = ModelTestHelpers.CreateResource(
            resourceName: "basket",
            displayName: "Basket service",
            state: KnownResourceState.Running);
        var catalogResource = ModelTestHelpers.CreateResource(
            resourceName: "catalog",
            displayName: "Catalog service",
            state: KnownResourceState.Running);
        var resourcesChannel = Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>();
        var dashboardClient = new TestDashboardClient(
            isEnabled: true,
            initialResources: [basketResource, catalogResource],
            resourceChannelProvider: () => resourcesChannel);
        SetupManageDataDialogServices(dashboardClient);

        var cut = RenderComponent<ManageDataDialog>();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains(JSInterop.Invocations, invocation => invocation.Identifier == "initializeIconCheckboxKeyboard");
            AssertSelectionCheckboxCount(cut, 3);
            AssertSelectionCheckbox(cut, "All data", "true");
            AssertSelectionCheckbox(cut, "Basket service", "true");
            AssertSelectionCheckbox(cut, "Catalog service", "true");
            AssertSelectionCheckboxesHaveNoActionLabels(cut);
            AssertNoButtonHasAccessibleName(cut, "Name");
        });

        await ClickSelectionCheckboxAsync(cut, "Basket service", "true");

        cut.WaitForAssertion(() =>
        {
            AssertSelectionCheckboxCount(cut, 3);
            AssertSelectionCheckbox(cut, "All data", "mixed");
            AssertSelectionCheckbox(cut, "Basket service", "false");
            AssertSelectionCheckbox(cut, "Catalog service", "true");
        });

        await ClickSelectionCheckboxAsync(cut, "Basket service", "false");

        cut.WaitForAssertion(() =>
        {
            AssertSelectionCheckboxCount(cut, 3);
            AssertSelectionCheckbox(cut, "All data", "true");
            AssertSelectionCheckbox(cut, "Basket service", "true");
            AssertSelectionCheckbox(cut, "Catalog service", "true");
        });

        await ExpandResourceRowsAsync(cut, expectedCount: 2);

        cut.WaitForAssertion(() =>
        {
            AssertAllSelectionControlsSelected(cut);
            AssertNoSelectionCheckboxHasAccessibleName(cut, "Console logs");
            AssertNoButtonHasAccessibleName(cut, "Name");
        });

        await ClickSelectionCheckboxAsync(cut, "Console logs for Basket service", "true");

        cut.WaitForAssertion(() =>
        {
            AssertSelectionCheckboxCount(cut, 7);
            AssertSelectionCheckbox(cut, "All data", "mixed");
            AssertSelectionCheckbox(cut, "Basket service", "mixed");
            AssertSelectionCheckbox(cut, "Catalog service", "true");
            AssertSelectionCheckbox(cut, "Resource for Basket service", "true");
            AssertSelectionCheckbox(cut, "Console logs for Basket service", "false");
            AssertSelectionCheckbox(cut, "Resource for Catalog service", "true");
            AssertSelectionCheckbox(cut, "Console logs for Catalog service", "true");
            AssertSelectionCheckboxesHaveNoActionLabels(cut);
            AssertNoSelectionCheckboxHasAccessibleName(cut, "Console logs");
            AssertNoButtonHasAccessibleName(cut, "Name");
        });

        await ClickSelectionCheckboxAsync(cut, "All data", "mixed");

        cut.WaitForAssertion(() =>
        {
            AssertAllSelectionControlsSelected(cut);
            AssertNoSelectionCheckboxHasAccessibleName(cut, "Console logs");
            AssertNoButtonHasAccessibleName(cut, "Name");
        });

        await ClickSelectionCheckboxAsync(cut, "All data", "true");

        cut.WaitForAssertion(() =>
        {
            AssertSelectionCheckboxCount(cut, 7);
            AssertSelectionCheckbox(cut, "All data", "false");
            AssertSelectionCheckbox(cut, "Basket service", "false");
            AssertSelectionCheckbox(cut, "Catalog service", "false");
            AssertSelectionCheckbox(cut, "Resource for Basket service", "false");
            AssertSelectionCheckbox(cut, "Console logs for Basket service", "false");
            AssertSelectionCheckbox(cut, "Resource for Catalog service", "false");
            AssertSelectionCheckbox(cut, "Console logs for Catalog service", "false");
            AssertSelectionCheckboxesHaveNoActionLabels(cut);
            AssertNoSelectionCheckboxHasAccessibleName(cut, "Console logs");
            AssertNoButtonHasAccessibleName(cut, "Name");
        });
    }

    [Fact]
    public async Task IconCheckbox_DoesNotInvokeClickWhenDisabled()
    {
        var clickCount = 0;
        SetupIconCheckboxJs();

        var cut = RenderComponent<IconCheckbox>(parameters => parameters
            .Add(p => p.CheckState, IconCheckboxState.Checked)
            .Add(p => p.Disabled, true)
            .Add(p => p.AccessibleLabel, "All data")
            .Add(p => p.OnClick, () => clickCount++));

        var checkbox = cut.Find("[role='checkbox']");

        Assert.Equal("true", checkbox.GetAttribute("aria-disabled"));
        await cut.InvokeAsync(() => checkbox.Click());

        Assert.Equal(0, clickCount);
    }

    private void SetupManageDataDialogServices(TestDashboardClient dashboardClient)
    {
        FluentUISetupHelpers.AddCommonDashboardServices(this);
        Services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        Services.AddOptions<DashboardOptions>().Configure(options => options.UI.DisableImport = true);
        Services.AddSingleton<IDashboardClient>(dashboardClient);
        Services.AddSingleton<IconResolver>();
        Services.AddSingleton<ConsoleLogsManager>();
        Services.AddSingleton<ConsoleLogsFetcher>();
        Services.AddSingleton<TelemetryExportService>();
        Services.AddSingleton<TelemetryImportService>();

        FluentUISetupHelpers.SetupFluentUIComponents(this);
        FluentUISetupHelpers.SetupFluentButton(this);
        FluentUISetupHelpers.SetupFluentDataGrid(this);

        SetupIconCheckboxJs();
    }

    private void SetupIconCheckboxJs()
    {
        var module = JSInterop.SetupModule("./Components/Controls/IconCheckbox.razor.js");
        module.SetupVoid("initializeIconCheckboxKeyboard", _ => true);
        module.SetupVoid("disposeIconCheckboxKeyboard", _ => true);
    }

    private static async Task ExpandResourceRowsAsync(IRenderedComponent<ManageDataDialog> cut, int expectedCount)
    {
        for (var i = 0; i < expectedCount; i++)
        {
            await cut.InvokeAsync(() =>
            {
                var toggleButtons = cut.FindAll("fluent-button[aria-label='Toggle nesting']");

                Assert.Equal(expectedCount, toggleButtons.Count);

                toggleButtons[i].Click();
            });
        }
    }

    private static void AssertAllSelectionControlsSelected(IRenderedComponent<ManageDataDialog> cut)
    {
        AssertSelectionCheckboxCount(cut, 7);
        AssertSelectionCheckbox(cut, "All data", "true");
        AssertSelectionCheckbox(cut, "Basket service", "true");
        AssertSelectionCheckbox(cut, "Catalog service", "true");
        AssertSelectionCheckbox(cut, "Resource for Basket service", "true");
        AssertSelectionCheckbox(cut, "Console logs for Basket service", "true");
        AssertSelectionCheckbox(cut, "Resource for Catalog service", "true");
        AssertSelectionCheckbox(cut, "Console logs for Catalog service", "true");
        AssertSelectionCheckboxesHaveNoActionLabels(cut);
    }

    private static IElement AssertSelectionCheckbox(IRenderedComponent<ManageDataDialog> cut, string accessibleName, string ariaChecked)
    {
        var checkbox = Assert.Single(GetSelectionCheckboxes(cut), checkbox => ElementHasAccessibleName(checkbox, accessibleName));

        Assert.Equal("span", checkbox.LocalName);
        Assert.Equal("checkbox", checkbox.GetAttribute("role"));
        Assert.Equal(ariaChecked, checkbox.GetAttribute("aria-checked"));
        Assert.Equal("0", checkbox.GetAttribute("tabindex"));
        Assert.Empty(checkbox.QuerySelectorAll("fluent-button"));

        return checkbox;
    }

    private static void AssertSelectionCheckboxCount(IRenderedComponent<ManageDataDialog> cut, int expectedCount)
    {
        Assert.Empty(cut.FindAll("fluent-button[role='checkbox']"));
        Assert.Equal(expectedCount, GetSelectionCheckboxes(cut).Count);
    }

    private static void AssertSelectionCheckboxesHaveNoActionLabels(IRenderedComponent<ManageDataDialog> cut)
    {
        Assert.DoesNotContain(GetSelectionCheckboxes(cut), checkbox =>
        {
            var accessibleName = checkbox.GetAttribute("aria-label");

            return accessibleName is not null &&
                (accessibleName.StartsWith("Select ", StringComparison.Ordinal) ||
                 accessibleName.StartsWith("Deselect ", StringComparison.Ordinal));
        });
    }

    private static void AssertNoSelectionCheckboxHasAccessibleName(IRenderedComponent<ManageDataDialog> cut, string accessibleName) =>
        Assert.DoesNotContain(GetSelectionCheckboxes(cut), checkbox => ElementHasAccessibleName(checkbox, accessibleName));

    private static void AssertNoButtonHasAccessibleName(IRenderedComponent<ManageDataDialog> cut, string accessibleName) =>
        Assert.DoesNotContain(
            cut.FindAll("fluent-button"),
            element =>
                string.Equals(element.GetAttribute("title"), accessibleName, StringComparison.Ordinal) ||
                string.Equals(element.GetAttribute("aria-label"), accessibleName, StringComparison.Ordinal));

    private static bool ElementHasAccessibleName(IElement element, string accessibleName) =>
        string.Equals(element.GetAttribute("title"), accessibleName, StringComparison.Ordinal) &&
        string.Equals(element.GetAttribute("aria-label"), accessibleName, StringComparison.Ordinal);

    private static Task ClickSelectionCheckboxAsync(IRenderedComponent<ManageDataDialog> cut, string accessibleName, string ariaChecked) =>
        cut.InvokeAsync(() => AssertSelectionCheckbox(cut, accessibleName, ariaChecked).Click());

    private static IReadOnlyList<IElement> GetSelectionCheckboxes(IRenderedComponent<ManageDataDialog> cut)
    {
        return cut.FindComponent<FluentDataGrid<ManageDataGridItem>>().FindAll("[role='checkbox']");
    }
}
