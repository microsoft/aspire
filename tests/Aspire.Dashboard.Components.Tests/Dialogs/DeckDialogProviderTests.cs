// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Dialogs;
using Aspire.Dashboard.Components.Layout;
using Aspire.Dashboard.Components.Tests.Shared;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Dialogs;

public class DeckDialogProviderTests : DashboardTestContext
{
    private (IRenderedFragment Cut, DeckDialogService Service, BunitJSModuleInterop Module) SetUp()
    {
        DashboardSetupHelpers.SetupDialogInfrastructure(this);
        var module = DashboardSetupHelpers.SetupDialogProvider(this);
        var cut = DashboardSetupHelpers.RenderDialogProviderCore(this);
        var service = Services.GetRequiredService<DeckDialogService>();
        return (cut, service, module);
    }

    [Fact]
    public async Task ShowDialog_RendersModalWithAccessibleAttributes()
    {
        var (cut, service, _) = SetUp();

        await service.ShowDialogAsync<TestDeckDialogContent>(new DeckDialogParameters
        {
            Id = "my-dialog",
            Title = "Hello world",
        });

        cut.WaitForAssertion(() =>
        {
            var dialog = cut.Find("[role='dialog']");

            Assert.Equal("true", dialog.GetAttribute("aria-modal"));
            Assert.Equal("deck-dialog-my-dialog", dialog.GetAttribute("id"));

            var labelledBy = dialog.GetAttribute("aria-labelledby");
            Assert.Equal("deck-dialog-title-my-dialog", labelledBy);
            Assert.Null(dialog.GetAttribute("aria-label"));

            var header = cut.Find($"#{labelledBy}");
            Assert.Contains("Hello world", header.TextContent);
        });
    }

    [Fact]
    public async Task ShowConfirmation_UsesMessageAsAccessibleNameWhenTitleIsMissing()
    {
        var (cut, service, _) = SetUp();

        await service.ShowConfirmationAsync("Delete this resource?", "Delete", "Cancel");

        cut.WaitForAssertion(() =>
        {
            var dialog = cut.Find("[role='dialog']");
            Assert.Equal("Delete this resource?", dialog.GetAttribute("aria-label"));
            Assert.Null(dialog.GetAttribute("aria-labelledby"));
        });
    }

    [Fact]
    public async Task ShowToolbarPanel_UsesExplicitAccessibleNameWithoutHeader()
    {
        var (cut, service, _) = SetUp();

        await service.ShowPanelAsync<ToolbarPanel>(
            new AspirePageContentLayout.MobileToolbar(
                ToolbarSection: builder => builder.AddMarkupContent(0, "<div>filters</div>"),
                MobileToolbarButtonText: "View filters"),
            new DeckDialogParameters
            {
                Title = "Filters",
                AccessibleName = "Filters",
            });

        cut.WaitForAssertion(() =>
        {
            var dialog = cut.Find("[role='dialog']");
            Assert.Equal("Filters", dialog.GetAttribute("aria-label"));
            Assert.Null(dialog.GetAttribute("aria-labelledby"));
            Assert.Empty(cut.FindAll(".deck-dialog__header"));
        });
    }

    [Fact]
    public async Task ShowDialog_WithoutTitle_FallsBackToLocalizedGenericName()
    {
        var (cut, service, _) = SetUp();

        await service.ShowDialogAsync<TestDeckDialogContent>(new DeckDialogParameters());

        cut.WaitForAssertion(() =>
        {
            var dialog = cut.Find("[role='dialog']");
            Assert.Equal(Aspire.Dashboard.Resources.Dialogs.DeckDialogGenericDialogLabel, dialog.GetAttribute("aria-label"));
            Assert.Null(dialog.GetAttribute("aria-labelledby"));
        });
    }

    [Fact]
    public async Task ShowDialog_InitializesAccessibilityJsModuleWithDialogElementId()
    {
        var (cut, service, module) = SetUp();

        await service.ShowDialogAsync<TestDeckDialogContent>(new DeckDialogParameters
        {
            Id = "js-dialog",
            Title = "Focus me",
            TrapFocus = true,
            PreventScroll = true,
        });

        cut.WaitForAssertion(() =>
        {
            var initialize = Assert.Single(module.Invocations["initialize"]);
            Assert.Equal("deck-dialog-js-dialog", Assert.IsType<string>(initialize.Arguments[0]));
        });
    }

    [Fact]
    public async Task CloseDialog_DisposesAccessibilityJsModuleForDialogElementId()
    {
        var (cut, service, module) = SetUp();

        var reference = await service.ShowDialogAsync<TestDeckDialogContent>(new DeckDialogParameters
        {
            Id = "close-dialog",
            Title = "Closing",
        });

        cut.WaitForAssertion(() => Assert.NotEmpty(module.Invocations["initialize"]));

        await cut.InvokeAsync(reference.CloseAsync);

        cut.WaitForAssertion(() =>
        {
            var dispose = Assert.Single(module.Invocations["dispose"]);
            Assert.Equal("deck-dialog-close-dialog", Assert.IsType<string>(dispose.Arguments[0]));
        });
    }

    [Fact]
    public async Task ShowPanel_UsesPanelBoxAndEndAlignment()
    {
        var (cut, service, _) = SetUp();

        await service.ShowPanelAsync<TestDeckDialogContent>(new DeckDialogParameters
        {
            Id = "panel",
            Title = "Panel",
            Alignment = DeckDialogAlignment.End,
        });

        cut.WaitForAssertion(() =>
        {
            var dialog = cut.Find("[role='dialog']");
            Assert.Equal("deck-dialog deck-dialog--panel", dialog.GetAttribute("class"));

            var overlay = cut.Find(".deck-dialog-overlay");
            Assert.Contains("deck-dialog-overlay--end", overlay.GetAttribute("class"));
        });
    }

    [Fact]
    public async Task ShowDialog_WithAlignment_StaysCenteredModalNotPanel()
    {
        // Mobile Settings/Notifications open as a Dialog while still carrying Alignment.End (the
        // shared parameters used for the desktop panel). The dialog must remain a centered modal
        // rather than being turned into a full-height edge panel by the alignment.
        var (cut, service, _) = SetUp();

        await service.ShowDialogAsync<TestDeckDialogContent>(new DeckDialogParameters
        {
            Id = "aligned-dialog",
            Title = "Settings",
            Alignment = DeckDialogAlignment.End,
            Width = "320px",
        });

        cut.WaitForAssertion(() =>
        {
            var dialog = cut.Find("[role='dialog']");

            // The box class is modal-only; the exact value proves no panel class leaks in from the
            // alignment.
            Assert.Equal("deck-dialog deck-dialog--modal", dialog.GetAttribute("class"));

            // The explicit width is applied and, being a modal, is capped by max-width:90vw in CSS so
            // it stays viewport-safe on narrow screens.
            Assert.Contains("width: 320px", dialog.GetAttribute("style"));

            // A modal Dialog keeps the default centered overlay (only the modal scrim class), with no
            // edge-dock alignment class.
            var overlay = cut.Find(".deck-dialog-overlay");
            Assert.Equal("deck-dialog-overlay deck-dialog-overlay--modal", overlay.GetAttribute("class")?.Trim());
        });
    }
}
