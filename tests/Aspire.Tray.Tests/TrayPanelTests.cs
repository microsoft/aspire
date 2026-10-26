// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Xml.Linq;
using Aspire.Tray.Tests.Helpers;

namespace Aspire.Tray.Tests;

public class TrayPanelTests
{
    private static readonly string[] s_sampleNames =
    [
        "typical", "empty", "connecting", "errors", "disconnected", "many",
        "confirm-clear", "confirm-missing", "settings", "settings-unavailable", "actions", "actions-recent", "actions-open-in"
    ];

    public static TheoryData<string> SampleNames => new(s_sampleNames);

    private static readonly string[] s_applications = ["Visual Studio Code", "Terminal"];

    private static TrayViewState Sample(string name) => name switch
    {
        "typical" => TrayPanelSamples.Typical(),
        "empty" => TrayPanelSamples.Empty(),
        "connecting" => TrayPanelSamples.Connecting(),
        "errors" => TrayPanelSamples.Errors(),
        "disconnected" => TrayPanelSamples.Disconnected(),
        "many" => TrayPanelSamples.Many(),
        _ => TrayPanelSamples.Typical()
    };

    /// <summary>Creates a panel for a sample, driving it into the sample's interactive state.</summary>
    private static TrayPanel CreatePanel(string name, TrayPanelPalette palette, double maxHeight = TrayPanel.DefaultMaxHeight)
    {
        var state = Sample(name);
        var panel = new TrayPanel(state, palette) { MaxHeight = maxHeight, OpenInApplications = s_applications };
        switch (name)
        {
            case "actions":
                Click(panel, Target(panel, PanelTargetKind.More, state.AppHosts[3]).HitBounds);
                break;
            case "actions-open-in":
                Click(panel, Target(panel, PanelTargetKind.More, state.AppHosts[0]).HitBounds);
                Click(panel, Target(panel, PanelTargetKind.OpenInMenu, state.AppHosts[0]).HitBounds);
                break;
            case "actions-recent":
                Click(panel, panel.Scene.Targets.Single(target => target.Kind == PanelTargetKind.ToggleRecent).HitBounds);
                Click(panel, Target(panel, PanelTargetKind.More, state.RecentAppHosts[0]).HitBounds);
                break;
            case "confirm-clear":
                Click(panel, panel.Scene.Targets.Single(target => target.Kind == PanelTargetKind.ClearRecent).HitBounds);
                break;
            case "confirm-missing":
                panel.ConfirmRemoveMissing(state.RecentAppHosts[1]);
                break;
            case "settings":
                panel.ShowSettings(new(new TrayStartupState(true, true, null), null, Preview: false));
                break;
            case "settings-unavailable":
                panel.ShowSettings(new(new TrayStartupState(false, false, null), null, Preview: false));
                break;
        }
        return panel;
    }

    [Theory]
    [MemberData(nameof(SampleNames))]
    public void LayoutKeepsEveryTargetInsideThePanelAndFooterAtTheBottom(string name)
    {
        var panel = CreatePanel(name, TrayPanelPalette.Light, maxHeight: 600);
        var scene = panel.Scene;
        var bounds = new PanelRect(0, 0, scene.Width, scene.Height);

        Assert.Equal(TrayPanel.Width, scene.Width);
        Assert.InRange(scene.Height, TrayPanel.HeaderHeight + TrayPanel.FooterHeight, 600);
        foreach (var target in scene.Targets)
        {
            Assert.True(bounds.Contains(target.HitBounds) || target.HitBounds.IsEmpty, $"{target.Key} at {target.HitBounds}");
            Assert.True(target.HitBounds.IsEmpty || target.Bounds.Contains(target.HitBounds));
        }
        var footer = scene.Targets.Where(target => target.Kind is PanelTargetKind.Documentation or PanelTargetKind.Settings or PanelTargetKind.Quit).ToList();
        Assert.Equal(3, footer.Count);
        Assert.All(footer, target => Assert.InRange(target.Bounds.Y, scene.Height - TrayPanel.FooterHeight, scene.Height - target.Bounds.Height));
        Assert.All(scene.Targets.Where(target => target.Kind is not (PanelTargetKind.Documentation or PanelTargetKind.Settings
                or PanelTargetKind.Quit or PanelTargetKind.Back)),
            target => Assert.True(target.HitBounds.IsEmpty || panel.Viewport.Contains(target.HitBounds)));
    }

    [Theory]
    [MemberData(nameof(SampleNames))]
    public void SiblingTargetsDoNotOverlapAndButtonsSitInsideTheirRow(string name)
    {
        var scene = CreatePanel(name, TrayPanelPalette.Dark).Scene;
        var rows = scene.Targets.Where(target => target.Kind is PanelTargetKind.Row).ToList();
        var buttons = scene.Targets.Where(target => target.Kind is PanelTargetKind.Dashboard or PanelTargetKind.Stop
            or PanelTargetKind.Start or PanelTargetKind.More).ToList();

        AssertNoOverlap(rows);
        AssertNoOverlap(buttons);
        foreach (var button in buttons)
        {
            var row = Assert.Single(rows, row => row.Key.Host == button.Key.Host && row.Key.Section == button.Key.Section);
            Assert.True(row.Bounds.Contains(button.Bounds), $"{button.Key} outside its row");
        }
    }

    [Theory]
    [MemberData(nameof(SampleNames))]
    public void RowTextNeverRunsUnderTheReservedButtons(string name)
    {
        var scene = CreatePanel(name, TrayPanelPalette.Light).Scene;
        var buttons = scene.Targets.Where(target => target.Kind is PanelTargetKind.Dashboard or PanelTargetKind.Stop
            or PanelTargetKind.Start or PanelTargetKind.More).Select(target => target.Bounds).ToList();
        var texts = scene.Commands.OfType<TextCommand>().ToList();

        Assert.All(texts, text => Assert.DoesNotContain(buttons, button => button.IntersectsWith(text.Bounds)));
    }

    [Theory]
    [MemberData(nameof(SampleNames))]
    public void ClipsAreBalanced(string name)
    {
        var commands = CreatePanel(name, TrayPanelPalette.Light).Scene.Commands;

        Assert.Equal(commands.Count(command => command is PushClipCommand), commands.Count(command => command is PopClipCommand));
    }

    [Fact]
    public void SectionsSplitRunningPinnedAndRecentHosts()
    {
        var scene = new TrayPanel(TrayPanelSamples.Typical(), TrayPanelPalette.Light).Scene;
        var rows = scene.Targets.Where(target => target.Kind == PanelTargetKind.Row).ToList();

        Assert.Equal([PanelSection.Running, PanelSection.Running, PanelSection.Running, PanelSection.Pinned],
            rows.Select(row => row.Key.Section));
        Assert.Contains(scene.Commands.OfType<TextCommand>(), text => text.Text == "RECENT · 2");
        Assert.Contains(scene.Targets, target => target.Kind == PanelTargetKind.ClearRecent);
    }

    [Fact]
    public void RecentListIsCollapsedWhenAppHostsAreListedAndExpandsOnToggle()
    {
        var panel = new TrayPanel(TrayPanelSamples.Typical(), TrayPanelPalette.Light);
        Assert.False(panel.IsRecentExpanded);
        var toggle = Assert.Single(panel.Scene.Targets, target => target.Kind == PanelTargetKind.ToggleRecent);

        var response = Click(panel, toggle.HitBounds);

        Assert.Equal(PanelActionKind.ToggleRecent, response.Action?.Kind);
        Assert.True(panel.IsRecentExpanded);
        Assert.Equal(2, panel.Scene.Targets.Count(target => target.Kind == PanelTargetKind.Row && target.Key.Section == PanelSection.Recent));
    }

    [Fact]
    public void RecentListIsExpandedWhenNothingElseIsListed()
    {
        var panel = new TrayPanel(TrayPanelSamples.Empty(), TrayPanelPalette.Light);

        Assert.True(panel.IsRecentExpanded);
        Assert.Contains(panel.Scene.Commands.OfType<TextCommand>(), text => text.Text == "No AppHosts running");
        Assert.Equal(2, panel.Scene.Targets.Count(target => target.Kind == PanelTargetKind.Row));
    }

    [Fact]
    public void ClickingARowDoesNothingWhateverItsState()
    {
        var state = TrayPanelSamples.Typical();
        var panel = new TrayPanel(state, TrayPanelPalette.Light);

        foreach (var host in state.AppHosts)
        {
            var response = Click(panel, Target(panel, PanelTargetKind.Row, host).HitBounds, offsetX: 10);

            Assert.Null(response.Action);
            Assert.Null(panel.ActionsRow);
        }
        var focused = Target(panel, PanelTargetKind.Row, state.AppHosts[0]).Key;
        panel.Focus(focused);
        Assert.Null(panel.Key(PanelKey.Enter).Action);
    }

    [Fact]
    public void MoreOpensTheRowsActionListUnderIt()
    {
        var state = TrayPanelSamples.Typical();
        var panel = new TrayPanel(state, TrayPanelPalette.Light) { OpenInApplications = s_applications };
        var host = state.AppHosts[2];
        var row = Target(panel, PanelTargetKind.Row, host);

        var response = Click(panel, Target(panel, PanelTargetKind.More, host).HitBounds);

        Assert.Null(response.Action);
        Assert.Equal((PanelSection.Running, host.Id), panel.ActionsRow);
        var items = panel.Scene.Targets.Where(target => target.Host == host && target.Bounds.Y > row.Bounds.Bottom).ToList();
        Assert.Equal(
        [
            "Open Redis in…",
            "Show Redis in Finder",
            "Copy the folder path of Redis",
            "Pin Redis"
        ], items.Select(target => target.AccessibleName));
        var next = Target(panel, PanelTargetKind.Row, state.AppHosts[3]);
        Assert.All(items, item => Assert.True(item.Bounds.Bottom <= next.Bounds.Y, $"{item.Key} overlaps the next row"));
        Assert.Equal(
            ["Open in", "Show in Finder", "Copy path", "Pin"],
            panel.Scene.Commands.OfType<TextCommand>().Where(text => text.Bounds.Y >= items[0].Bounds.Y && text.Bounds.Bottom <= items[^1].Bounds.Bottom)
                .Select(text => text.Text));
    }

    [Fact]
    public void ActionListItemsReturnTheirActionsAndCloseTheList()
    {
        var state = TrayPanelSamples.Typical();
        var panel = new TrayPanel(state, TrayPanelPalette.Light) { OpenInApplications = s_applications, FileManagerName = "File Explorer" };
        var host = state.AppHosts[0];

        PanelAction? Pick(PanelTargetKind kind, int item = 0)
        {
            Click(panel, Target(panel, PanelTargetKind.More, host).HitBounds);
            if (kind == PanelTargetKind.OpenIn)
            {
                Click(panel, Target(panel, PanelTargetKind.OpenInMenu, host).HitBounds);
            }
            var target = panel.Scene.Targets.Single(target => target.Key == new PanelTargetKey(kind, PanelSection.Running, host.Id, item));
            var action = Click(panel, target.HitBounds).Action;
            Assert.Null(panel.ActionsRow);
            return action;
        }

        Assert.Equal(new PanelAction(PanelActionKind.OpenIn, host, default) { Application = "Terminal" },
            Pick(PanelTargetKind.OpenIn, 1)! with { Anchor = default });
        Assert.Equal(PanelActionKind.ShowInFolder, Pick(PanelTargetKind.ShowInFolder)?.Kind);
        Assert.Equal(PanelActionKind.CopyPath, Pick(PanelTargetKind.CopyPath)?.Kind);
        var pin = Pick(PanelTargetKind.TogglePin);
        Assert.Equal(PanelActionKind.TogglePin, pin?.Kind);
        Assert.Equal(host, pin?.Host);
        Click(panel, Target(panel, PanelTargetKind.More, host).HitBounds);
        Assert.Contains(panel.Scene.Commands.OfType<TextCommand>(), text => text.Text == "Show in File Explorer");
    }

    [Fact]
    public void PinnedRowsOfferUnpinAndRecentRowsOfferRemove()
    {
        var state = TrayPanelSamples.Typical();
        var panel = new TrayPanel(state, TrayPanelPalette.Light);
        Click(panel, Target(panel, PanelTargetKind.More, state.AppHosts[3]).HitBounds);

        Assert.Equal("Unpin Weather", panel.Scene.Targets.Single(target => target.Kind == PanelTargetKind.TogglePin).AccessibleName);
        Assert.DoesNotContain(panel.Scene.Targets, target => target.Kind == PanelTargetKind.RemoveRecent);

        Click(panel, panel.Scene.Targets.Single(target => target.Kind == PanelTargetKind.ToggleRecent).HitBounds);
        var recent = state.RecentAppHosts[0];
        Click(panel, Target(panel, PanelTargetKind.More, recent).HitBounds);
        var remove = panel.Scene.Targets.Single(target => target.Kind == PanelTargetKind.RemoveRecent);
        var action = Click(panel, remove.HitBounds).Action;

        Assert.Equal(new PanelAction(PanelActionKind.RemoveRecent, recent, remove.Bounds) { Recent = true }, action);
        Assert.Null(panel.ActionsRow);
    }

    [Fact]
    public void ClickingMoreAgainOrAnywhereElseClosesTheActionList()
    {
        var state = TrayPanelSamples.Typical();
        var panel = new TrayPanel(state, TrayPanelPalette.Light);

        Click(panel, Target(panel, PanelTargetKind.More, state.AppHosts[0]).HitBounds);
        Assert.Equal("Hide actions for eShop", Target(panel, PanelTargetKind.More, state.AppHosts[0]).AccessibleName);
        Click(panel, Target(panel, PanelTargetKind.More, state.AppHosts[0]).HitBounds);
        Assert.Null(panel.ActionsRow);

        Click(panel, Target(panel, PanelTargetKind.More, state.AppHosts[0]).HitBounds);
        Click(panel, Target(panel, PanelTargetKind.More, state.AppHosts[1]).HitBounds);
        Assert.Equal((PanelSection.Running, state.AppHosts[1].Id), panel.ActionsRow);

        var stop = Click(panel, Target(panel, PanelTargetKind.Stop, state.AppHosts[0]).HitBounds);
        Assert.Equal(PanelActionKind.Stop, stop.Action?.Kind);
        Assert.Null(panel.ActionsRow);
    }

    [Fact]
    public void KeyboardMovesIntoTheActionListAndEscapeReturnsToMore()
    {
        var state = TrayPanelSamples.Typical();
        var panel = new TrayPanel(state, TrayPanelPalette.Light) { OpenInApplications = s_applications };
        var host = state.AppHosts[0].Id;
        var more = Target(panel, PanelTargetKind.More, state.AppHosts[0]).Key;
        panel.Focus(more);

        panel.Key(PanelKey.Enter);
        Assert.Equal((PanelSection.Running, host), panel.ActionsRow);
        panel.Key(PanelKey.Down);
        Assert.Equal(new PanelTargetKey(PanelTargetKind.OpenInMenu, PanelSection.Running, host), panel.FocusedTarget);
        panel.Key(PanelKey.Enter);
        panel.Key(PanelKey.Down);
        panel.Key(PanelKey.Down);
        Assert.Equal(new PanelTargetKey(PanelTargetKind.OpenIn, PanelSection.Running, host, 1), panel.FocusedTarget);

        // Escape steps out of the applications, then out of the list, then closes the panel.
        Assert.Null(panel.Key(PanelKey.Escape).Action);
        Assert.Equal(new PanelTargetKey(PanelTargetKind.OpenInMenu, PanelSection.Running, host), panel.FocusedTarget);
        Assert.DoesNotContain(panel.Scene.Targets, target => target.Kind == PanelTargetKind.OpenIn);
        panel.Key(PanelKey.Down);
        Assert.Equal(new PanelTargetKey(PanelTargetKind.ShowInFolder, PanelSection.Running, host), panel.FocusedTarget);
        Assert.Null(panel.Key(PanelKey.Escape).Action);
        Assert.Null(panel.ActionsRow);
        Assert.Equal(more, panel.FocusedTarget);
        Assert.Equal(PanelActionKind.Close, panel.Key(PanelKey.Escape).Action?.Kind);
    }

    [Fact]
    public void OpenInShowsTheApplicationsWithTheirIconsUnderIt()
    {
        var state = TrayPanelSamples.Typical();
        var panel = new TrayPanel(state, TrayPanelPalette.Light) { OpenInApplications = s_applications };
        var host = state.AppHosts[0];
        Click(panel, Target(panel, PanelTargetKind.More, host).HitBounds);
        Assert.Empty(panel.Scene.Commands.OfType<AppIconCommand>());

        var openIn = Target(panel, PanelTargetKind.OpenInMenu, host);
        Assert.Null(Click(panel, openIn.HitBounds).Action);

        Assert.Equal((PanelSection.Running, host.Id), panel.ActionsRow);
        Assert.Equal($"Hide applications to open {host.DisplayName} in", Target(panel, PanelTargetKind.OpenInMenu, host).AccessibleName);
        var applications = panel.Scene.Targets.Where(target => target.Kind == PanelTargetKind.OpenIn).ToList();
        Assert.Equal(["Open eShop in Visual Studio Code", "Open eShop in Terminal"], applications.Select(target => target.AccessibleName));
        var icons = panel.Scene.Commands.OfType<AppIconCommand>().ToList();
        Assert.Equal(s_applications, icons.Select(icon => icon.Application));
        Assert.All(icons, icon => Assert.NotEmpty(icon.Fallback));
        // Nested: the icons start where the labels of the other items do, under "Open in".
        Assert.All(icons, icon => Assert.True(icon.Bounds.X > openIn.Bounds.X + 30));
        Assert.All(applications, target => Assert.True(target.Bounds.Y >= openIn.Bounds.Bottom));

        Click(panel, Target(panel, PanelTargetKind.OpenInMenu, host).HitBounds);
        Assert.DoesNotContain(panel.Scene.Targets, target => target.Kind == PanelTargetKind.OpenIn);
    }

    [Fact]
    public void NoOpenInItemWithoutInstalledApplications()
    {
        var state = TrayPanelSamples.Typical();
        var panel = new TrayPanel(state, TrayPanelPalette.Light);
        Click(panel, Target(panel, PanelTargetKind.More, state.AppHosts[0]).HitBounds);

        Assert.DoesNotContain(panel.Scene.Targets, target => target.Kind is PanelTargetKind.OpenInMenu or PanelTargetKind.OpenIn);
    }

    [Fact]
    public void ActionListClosesWhenItsAppHostGoesAway()
    {
        var state = TrayPanelSamples.Typical();
        var panel = new TrayPanel(state, TrayPanelPalette.Light);
        Click(panel, Target(panel, PanelTargetKind.More, state.AppHosts[0]).HitBounds);

        panel.Update(state with { AppHosts = state.AppHosts.Skip(1).ToArray() });

        Assert.DoesNotContain(panel.Scene.Targets, target => target.Kind == PanelTargetKind.CopyPath);
        Assert.Null(panel.ActionsRow);
    }

    [Fact]
    public void ActionListBelowTheFoldIsScrolledIntoView()
    {
        var state = TrayPanelSamples.Many();
        var panel = new TrayPanel(state, TrayPanelPalette.Light) { MaxHeight = 400 };
        var last = state.AppHosts[^1];
        panel.Focus(Target(panel, PanelTargetKind.Row, last).Key);

        panel.Invoke(Target(panel, PanelTargetKind.More, last).Key);

        Assert.All(panel.Scene.Targets.Where(target => target.Kind is PanelTargetKind.CopyPath or PanelTargetKind.TogglePin),
            target => Assert.True(panel.Viewport.Contains(target.Bounds), $"{target.Key} at {target.Bounds}"));
    }

    [Fact]
    public void FocusFirstShowsKeyboardFocusOnTheFirstControl()
    {
        var state = TrayPanelSamples.Typical();
        var panel = new TrayPanel(state, TrayPanelPalette.Light);

        Assert.True(panel.FocusFirst().Redraw);

        Assert.Equal(Target(panel, PanelTargetKind.Row, state.AppHosts[0]).Key, panel.FocusedTarget);
        Assert.Contains(panel.Scene.Commands.OfType<StrokePathCommand>(), stroke => stroke.Color == TrayPanelPalette.Light.Focus);
    }

    [Fact]
    public void RowButtonsReturnTheirActions()
    {
        var state = TrayPanelSamples.Typical();
        var panel = new TrayPanel(state, TrayPanelPalette.Light);

        Assert.Equal(PanelActionKind.Stop, Click(panel, Target(panel, PanelTargetKind.Stop, state.AppHosts[0]).HitBounds).Action?.Kind);
        Assert.Equal(PanelActionKind.OpenDashboard, Click(panel, Target(panel, PanelTargetKind.Dashboard, state.AppHosts[0]).HitBounds).Action?.Kind);
        var start = Click(panel, Target(panel, PanelTargetKind.Start, state.AppHosts[3]).HitBounds).Action;
        Assert.Equal(PanelActionKind.Start, start?.Kind);
        Assert.Equal(state.AppHosts[3].Id.AppHostPath, start?.Host?.Id.AppHostPath);
        Assert.Equal(PanelActionKind.Settings, Click(panel, panel.Scene.Targets.Single(t => t.Kind == PanelTargetKind.Settings).HitBounds).Action?.Kind);
        Assert.Equal(PanelActionKind.Quit, Click(panel, panel.Scene.Targets.Single(t => t.Kind == PanelTargetKind.Quit).HitBounds).Action?.Kind);
        Assert.Equal(PanelActionKind.Documentation, Click(panel, panel.Scene.Targets.Single(t => t.Kind == PanelTargetKind.Documentation).HitBounds).Action?.Kind);
    }

    [Fact]
    public void DisabledButtonsDoNothing()
    {
        var state = TrayPanelSamples.Typical();
        var panel = new TrayPanel(state, TrayPanelPalette.Light);
        var dashboard = Target(panel, PanelTargetKind.Dashboard, state.AppHosts[2]);

        Assert.False(dashboard.Enabled);
        Assert.Null(Click(panel, dashboard.HitBounds).Action);
    }

    [Fact]
    public void ReleasingOutsideThePressedTargetCancelsTheClick()
    {
        var state = TrayPanelSamples.Typical();
        var panel = new TrayPanel(state, TrayPanelPalette.Light);
        var stop = Target(panel, PanelTargetKind.Stop, state.AppHosts[0]);
        var more = Target(panel, PanelTargetKind.More, state.AppHosts[0]);

        panel.PointerDown(stop.HitBounds.CenterX, stop.HitBounds.CenterY);
        var response = panel.PointerUp(more.HitBounds.CenterX, more.HitBounds.CenterY);

        Assert.Null(response.Action);
    }

    [Fact]
    public void StateUpdatesAreDeferredWhileAPressIsInProgress()
    {
        var state = TrayPanelSamples.Typical();
        var panel = new TrayPanel(state, TrayPanelPalette.Light);
        var stop = Target(panel, PanelTargetKind.Stop, state.AppHosts[0]);

        panel.PointerDown(stop.HitBounds.CenterX, stop.HitBounds.CenterY);
        Assert.False(panel.Update(TrayPanelSamples.Empty()));
        Assert.Same(state, panel.State);
        var response = panel.PointerUp(stop.HitBounds.CenterX, stop.HitBounds.CenterY);

        Assert.Equal(PanelActionKind.Stop, response.Action?.Kind);
        Assert.Equal(state.AppHosts[0], response.Action?.Host);
        Assert.Empty(panel.State.AppHosts);
    }

    [Fact]
    public void RowButtonsAreDrawnOnlyWhileTheRowIsHovered()
    {
        var state = TrayPanelSamples.Typical();
        var panel = new TrayPanel(state, TrayPanelPalette.Light);
        var before = panel.Scene.Commands.Count;
        var row = Target(panel, PanelTargetKind.Row, state.AppHosts[0]);

        Assert.True(panel.PointerMoved(row.HitBounds.X + 10, row.HitBounds.CenterY).Redraw);
        var hovered = panel.Scene.Commands.Count;
        Assert.False(panel.PointerMoved(row.HitBounds.X + 11, row.HitBounds.CenterY).Redraw);
        Assert.True(panel.PointerLeft().Redraw);

        // One hover fill plus the dashboard (two strokes), stop (one fill) and more (three fills) icons.
        Assert.Equal(before + 7, hovered);
        Assert.Equal(before, panel.Scene.Commands.Count);
    }

    [Fact]
    public void ManyRowsScrollInsideAFixedHeaderAndFooter()
    {
        var panel = new TrayPanel(TrayPanelSamples.Many(), TrayPanelPalette.Light) { MaxHeight = 480 };
        var scene = panel.Scene;
        var footer = scene.Targets.Single(target => target.Kind == PanelTargetKind.Quit).Bounds;

        Assert.Equal(480, scene.Height);
        Assert.Equal(0, panel.ScrollOffset);
        Assert.True(panel.Scroll(-100).Redraw);
        Assert.Equal(100, panel.ScrollOffset);
        Assert.Equal(footer, panel.Scene.Targets.Single(target => target.Kind == PanelTargetKind.Quit).Bounds);
        Assert.True(panel.Scroll(-100_000).Redraw);
        var max = panel.ScrollOffset;
        Assert.False(panel.Scroll(-1).Redraw);
        Assert.Equal(max, panel.ScrollOffset);
        var last = panel.Scene.Targets.Last(target => target.Kind == PanelTargetKind.ToggleRecent);
        Assert.InRange(last.Bounds.Bottom, panel.Viewport.Y, panel.Viewport.Bottom);
        Assert.True(panel.Scroll(100_000).Redraw);
        Assert.Equal(0, panel.ScrollOffset);
    }

    [Fact]
    public void RowsScrolledOutOfViewCannotBeClicked()
    {
        var state = TrayPanelSamples.Many();
        var panel = new TrayPanel(state, TrayPanelPalette.Light) { MaxHeight = 400 };
        panel.Scroll(-200);
        var first = panel.Scene.Targets.FirstOrDefault(target => target.Kind == PanelTargetKind.Row && target.Host == state.AppHosts[0]);

        Assert.NotNull(first);
        Assert.True(first.HitBounds.IsEmpty);
        Assert.Null(panel.HitTest(100, TrayPanel.HeaderHeight - 1));
    }

    [Fact]
    public void KeyboardNavigationMovesAcrossRowsAndScrollsTheFocusIntoView()
    {
        var state = TrayPanelSamples.Many();
        var panel = new TrayPanel(state, TrayPanelPalette.Light) { MaxHeight = 400 };

        panel.Key(PanelKey.Down);
        Assert.Equal(new PanelTargetKey(PanelTargetKind.Row, PanelSection.Running, state.AppHosts[0].Id), panel.FocusedTarget);
        for (var i = 1; i < state.AppHosts.Count; i++)
        {
            panel.Key(PanelKey.Down);
        }

        Assert.Equal(new PanelTargetKey(PanelTargetKind.Row, PanelSection.Running, state.AppHosts[^1].Id), panel.FocusedTarget);
        var focused = panel.Scene.Targets.Single(target => target.Key == panel.FocusedTarget);
        Assert.True(panel.Viewport.Contains(focused.Bounds));
        Assert.True(panel.ScrollOffset > 0);
        Assert.Contains(panel.Scene.Commands.OfType<StrokePathCommand>(), stroke => stroke.Color == TrayPanelPalette.Light.Focus);

    }

    [Fact]
    public void TabVisitsInlineButtonsAndSkipsDisabledOnes()
    {
        var state = TrayPanelSamples.Typical();
        var panel = new TrayPanel(state, TrayPanelPalette.Light);
        var visited = new List<PanelTargetKey>();
        var enabled = panel.Scene.Targets.Count(target => target.Enabled);
        for (var i = 0; i < enabled; i++)
        {
            panel.Key(PanelKey.Tab);
            visited.Add(panel.FocusedTarget!.Value);
        }

        Assert.Equal(panel.Scene.Targets.Where(target => target.Enabled).Select(target => target.Key), visited);
        Assert.DoesNotContain(new PanelTargetKey(PanelTargetKind.Dashboard, PanelSection.Running, state.AppHosts[2].Id), visited);
        panel.Key(PanelKey.Tab);
        Assert.Equal(visited[0], panel.FocusedTarget);
        panel.Key(PanelKey.ShiftTab);
        Assert.Equal(visited[^1], panel.FocusedTarget);
    }

    [Fact]
    public void EscapeClosesThePanel()
    {
        var panel = new TrayPanel(TrayPanelSamples.Typical(), TrayPanelPalette.Light);

        Assert.Equal(PanelActionKind.Close, panel.Key(PanelKey.Escape).Action?.Kind);
    }

    [Fact]
    public void StopIsSentAtOnceWithoutAConfirmation()
    {
        // Like the dashboard, stopping an AppHost doesn't ask first.
        var state = TrayPanelSamples.Typical();
        var panel = new TrayPanel(state, TrayPanelPalette.Light);

        var action = Click(panel, Target(panel, PanelTargetKind.Stop, state.AppHosts[1]).HitBounds).Action;

        Assert.Equal(new PanelAction(PanelActionKind.Stop, state.AppHosts[1], Target(panel, PanelTargetKind.Stop, state.AppHosts[1]).Bounds), action);
        Assert.Null(panel.Confirmation);
        Assert.Contains(panel.Scene.Targets, target => target.Kind == PanelTargetKind.Row && target.Host == state.AppHosts[1]);
    }

    [Fact]
    public void ClickingAStartingOrStoppingRowDoesNothing()
    {
        var state = TrayPanelSamples.Errors();
        var panel = new TrayPanel(state, TrayPanelPalette.Light);
        var stopping = state.AppHosts.Single(host => host.IsStopping);
        var starting = state.AppHosts.Single(host => host.IsStarting);

        Assert.False(Target(panel, PanelTargetKind.Dashboard, stopping).Enabled);
        Assert.Null(Click(panel, Target(panel, PanelTargetKind.Row, stopping).HitBounds, offsetX: 10).Action);
        Assert.Null(Click(panel, Target(panel, PanelTargetKind.Row, starting).HitBounds, offsetX: 10).Action);
    }

    [Fact]
    public void EscapeCancelsAConfirmationBeforeClosingThePanel()
    {
        var panel = new TrayPanel(TrayPanelSamples.Typical(), TrayPanelPalette.Light);
        Click(panel, panel.Scene.Targets.Single(target => target.Kind == PanelTargetKind.ClearRecent).HitBounds);

        var first = panel.Key(PanelKey.Escape);

        Assert.True(first.Redraw);
        Assert.Null(first.Action);
        Assert.Null(panel.Confirmation);
        Assert.Equal(PanelActionKind.Close, panel.Key(PanelKey.Escape).Action?.Kind);
    }

    [Fact]
    public void CancelButtonDismissesTheConfirmation()
    {
        var panel = new TrayPanel(TrayPanelSamples.Typical(), TrayPanelPalette.Light);
        Click(panel, panel.Scene.Targets.Single(target => target.Kind == PanelTargetKind.ClearRecent).HitBounds);

        var response = Click(panel, panel.Scene.Targets.Single(target => target.Kind == PanelTargetKind.ConfirmCancel).HitBounds);

        Assert.Null(response.Action);
        Assert.Null(panel.Confirmation);
        Assert.Contains(panel.Scene.Targets, target => target.Kind == PanelTargetKind.ToggleRecent);
    }

    [Fact]
    public void ActingOnAnotherTargetAbandonsTheConfirmation()
    {
        var panel = new TrayPanel(TrayPanelSamples.Typical(), TrayPanelPalette.Light);
        Click(panel, panel.Scene.Targets.Single(target => target.Kind == PanelTargetKind.ClearRecent).HitBounds);

        var action = Click(panel, panel.Scene.Targets.Single(target => target.Kind == PanelTargetKind.Documentation).HitBounds).Action;

        Assert.Equal(PanelActionKind.Documentation, action?.Kind);
        Assert.Null(panel.Confirmation);
    }

    [Fact]
    public void ConfirmationIsDroppedWhenItsAppHostGoesAway()
    {
        var state = TrayPanelSamples.Typical();
        var panel = new TrayPanel(state, TrayPanelPalette.Light);
        panel.ConfirmRemoveMissing(state.RecentAppHosts[1]);

        panel.Update(state with { RecentAppHosts = state.RecentAppHosts.Take(1).ToArray() });
        _ = panel.Scene;

        Assert.Null(panel.Confirmation);
    }

    [Fact]
    public void ClearRecentAsksInlineAndReturnsClearRecentWhenConfirmed()
    {
        var panel = new TrayPanel(TrayPanelSamples.Typical(), TrayPanelPalette.Light);

        var response = Click(panel, panel.Scene.Targets.Single(target => target.Kind == PanelTargetKind.ClearRecent).HitBounds);

        Assert.Null(response.Action);
        Assert.Equal(PanelConfirmKind.ClearRecent, panel.Confirmation);
        var texts = panel.Scene.Commands.OfType<TextCommand>().Select(text => text.Text).ToList();
        Assert.Contains("Clear recent AppHosts?", texts);
        Assert.Contains("Pinned AppHosts are kept.", texts);
        Assert.DoesNotContain(panel.Scene.Targets, target => target.Kind is PanelTargetKind.ToggleRecent);
        Assert.Equal(new PanelTargetKey(PanelTargetKind.ConfirmCancel, PanelSection.Recent, null), panel.FocusedTarget);
        var accept = panel.Scene.Targets.Single(target => target.Kind == PanelTargetKind.ConfirmAccept);
        Assert.Equal(PanelActionKind.ClearRecent, Click(panel, accept.HitBounds).Action?.Kind);
        Assert.Null(panel.Confirmation);
    }

    [Fact]
    public void MissingRecentAppHostIsOfferedForRemovalInline()
    {
        var state = TrayPanelSamples.Typical();
        var panel = new TrayPanel(state, TrayPanelPalette.Light);
        var missing = state.RecentAppHosts[1];

        panel.ConfirmRemoveMissing(missing);

        Assert.True(panel.IsRecentExpanded);
        Assert.Equal(PanelConfirmKind.RemoveMissing, panel.Confirmation);
        Assert.Contains(panel.Scene.Commands.OfType<TextCommand>(), text => text.Text == missing.Id.AppHostPath);
        var action = Click(panel, Target(panel, PanelTargetKind.ConfirmAccept, missing).HitBounds).Action;
        Assert.Equal(PanelActionKind.RemoveRecent, action?.Kind);
        Assert.Equal(missing, action?.Host);
        Assert.True(action?.Recent);
    }

    [Fact]
    public void SettingsViewShowsTogglesAndReturnsTheirNewValues()
    {
        var panel = new TrayPanel(TrayPanelSamples.Typical(), TrayPanelPalette.Light);
        Assert.Equal(PanelActionKind.Settings, Click(panel, panel.Scene.Targets.Single(t => t.Kind == PanelTargetKind.Settings).HitBounds).Action?.Kind);
        Assert.False(panel.IsSettingsOpen);

        panel.ShowSettings(new(new TrayStartupState(false, true, null), null, Preview: false));

        Assert.True(panel.IsSettingsOpen);
        Assert.DoesNotContain(panel.Scene.Targets, target => target.Kind == PanelTargetKind.Row);
        Assert.Equal(new PanelTargetKey(PanelTargetKind.Back, PanelSection.None, null), panel.FocusedTarget);
        var launch = panel.Scene.Targets.Single(target => target.Kind == PanelTargetKind.LaunchAtSignIn);
        Assert.Equal(TraySettingsText.PanelStartupOption, launch.AccessibleName);
        Assert.Equal(PanelAccessibleRole.Switch, launch.Role);
        Assert.False(launch.Checked);
        var setLaunch = Click(panel, launch.HitBounds).Action;
        Assert.Equal(PanelActionKind.SetLaunchAtSignIn, setLaunch?.Kind);
        Assert.True(setLaunch?.Enabled);
        var texts = panel.Scene.Commands.OfType<TextCommand>().Select(text => text.Text).ToList();
        Assert.Contains(TraySettingsText.GetAboutDescription(preview: false), texts);
        Assert.Contains(texts, text => text.StartsWith("Version: ", StringComparison.Ordinal));

        panel.UpdateSettings(new(new TrayStartupState(true, true, null), null, Preview: false));
        Assert.True(panel.Scene.Targets.Single(target => target.Kind == PanelTargetKind.LaunchAtSignIn).Checked);
    }

    [Fact]
    public void LaunchAtSignInCannotBeTurnedOnWithoutAStableInstallation()
    {
        var panel = new TrayPanel(TrayPanelSamples.Typical(), TrayPanelPalette.Light);
        panel.ShowSettings(new(new TrayStartupState(false, false, null), null, Preview: false));

        var launch = panel.Scene.Targets.Single(target => target.Kind == PanelTargetKind.LaunchAtSignIn);

        Assert.False(launch.Enabled);
        Assert.Null(Click(panel, launch.HitBounds).Action);
        Assert.Contains(panel.Scene.Commands.OfType<TextCommand>(), text => text.Text == TraySettingsText.StableNativeInstallationRequired);
    }

    [Fact]
    public void SettingsErrorsAreShownUnderTheToggle()
    {
        var panel = new TrayPanel(TrayPanelSamples.Typical(), TrayPanelPalette.Light);
        panel.ShowSettings(new(null, "Could not read launch at sign-in: denied", Preview: false));

        var launch = panel.Scene.Targets.Single(target => target.Kind == PanelTargetKind.LaunchAtSignIn);

        Assert.False(launch.Enabled);
        Assert.Contains(panel.Scene.Commands.OfType<TextCommand>(),
            text => text.Text == "Could not read launch at sign-in: denied" && text.Color == TrayPanelPalette.Light.Danger);
    }

    [Fact]
    public void BackAndEscapeLeaveSettingsAndRestoreTheList()
    {
        var panel = new TrayPanel(TrayPanelSamples.Many(), TrayPanelPalette.Light) { MaxHeight = 400 };
        panel.Scroll(-120);
        var scroll = panel.ScrollOffset;
        panel.ShowSettings(new(new TrayStartupState(false, true, null), null, Preview: false));
        Assert.Equal(0, panel.ScrollOffset);

        var escape = panel.Key(PanelKey.Escape);

        Assert.Null(escape.Action);
        Assert.False(panel.IsSettingsOpen);
        Assert.Equal(scroll, panel.ScrollOffset);
        Assert.Equal(new PanelTargetKey(PanelTargetKind.Settings, PanelSection.None, null), panel.FocusedTarget);

        panel.ShowSettings(new(new TrayStartupState(false, true, null), null, Preview: false));
        Assert.Null(Click(panel, panel.Scene.Targets.Single(target => target.Kind == PanelTargetKind.Back).HitBounds).Action);
        Assert.False(panel.IsSettingsOpen);
    }

    [Fact]
    public void ResetLeavesConfirmationsAndSettings()
    {
        var state = TrayPanelSamples.Typical();
        var panel = new TrayPanel(state, TrayPanelPalette.Light);
        Click(panel, Target(panel, PanelTargetKind.Stop, state.AppHosts[0]).HitBounds);
        panel.Reset();
        Assert.Null(panel.Confirmation);

        panel.ShowSettings(new(null, null, Preview: false));
        panel.Reset();
        Assert.False(panel.IsSettingsOpen);

        Click(panel, Target(panel, PanelTargetKind.More, state.AppHosts[0]).HitBounds);
        panel.Reset();
        Assert.Null(panel.ActionsRow);
    }

    [Fact]
    public void ToggleGlyphStaysInsideItsBox()
    {
        foreach (var on in new[] { true, false })
        {
            var commands = new List<PanelCommand>();
            var box = new PanelRect(10, 20, 32, 18);
            TrayPanelGlyphs.AddToggle(commands, on, enabled: true, box, TrayPanelPalette.Light);

            Assert.All(commands, command => Assert.True(box.Inset(-0.5, -0.5).Contains(GetBounds(command)), $"{command}"));
        }
    }

    [Fact]
    public void FocusOnARemovedRowIsForgotten()
    {
        var panel = new TrayPanel(TrayPanelSamples.Typical(), TrayPanelPalette.Light);
        panel.Key(PanelKey.Down);
        Assert.NotNull(panel.FocusedTarget);

        panel.Update(TrayPanelSamples.Connecting());
        _ = panel.Scene;

        Assert.Null(panel.FocusedTarget);
    }

    [Fact]
    public void ResetClearsInteractionState()
    {
        var panel = new TrayPanel(TrayPanelSamples.Many(), TrayPanelPalette.Light) { MaxHeight = 400 };
        panel.Key(PanelKey.End);
        panel.Scroll(-50);

        panel.Reset();

        Assert.Null(panel.FocusedTarget);
        Assert.Equal(0, panel.ScrollOffset);
    }

    [Theory]
    [InlineData((int)DiscoveryState.Connecting, "Connecting…")]
    [InlineData((int)DiscoveryState.Disconnected, "Reconnecting…")]
    [InlineData((int)DiscoveryState.Incompatible, "Incompatible CLI")]
    [InlineData((int)DiscoveryState.LimitExceeded, "Discovery limit exceeded")]
    public void HeaderStatusReportsDiscoveryProblems(int discovery, string expected)
    {
        var (text, _) = TrayPanel.GetHeaderStatus(new((DiscoveryState)discovery, [], ""), TrayPanelPalette.Light);

        Assert.Equal(expected, text);
    }

    [Fact]
    public void HeaderStatusSummarizesRunningAndAttention()
    {
        Assert.Equal("3 running · 1 needs attention", TrayPanel.GetHeaderStatus(TrayPanelSamples.Typical(), TrayPanelPalette.Light).Text);
        Assert.Equal("No AppHosts running", TrayPanel.GetHeaderStatus(TrayPanelSamples.Empty(), TrayPanelPalette.Light).Text);
        Assert.Equal("3 running · 1 starting · 2 need attention",
            TrayPanel.GetHeaderStatus(TrayPanelSamples.Errors(), TrayPanelPalette.Light).Text);
    }

    [Fact]
    public void RowDetailShowsStatusLocationAndPidOnlyForDuplicateNames()
    {
        var host = TrayPanelSamples.Running("/Users/dev/src/eShop/src/eShop.AppHost/eShop.AppHost.csproj", 48213, AppHostHealth.Warning);
        // The location uses the platform's separator: "src/eShop.AppHost", or "src\eShop.AppHost" on Windows.
        var location = Path.Combine("src", "eShop.AppHost");

        Assert.Equal(($"Needs attention · {location}", false), TrayPanel.GetRowDetail(host, live: true, showPid: false));
        Assert.Equal(($"Needs attention · {location} · PID 48213", false), TrayPanel.GetRowDetail(host, live: true, showPid: true));
        Assert.Equal(($"Status unavailable · {location}", false), TrayPanel.GetRowDetail(host, live: false, showPid: false));
        Assert.Equal(("Boom.", true), TrayPanel.GetRowDetail(host with { Error = "Boom." }, live: true, showPid: false));

        var scene = new TrayPanel(TrayPanelSamples.Errors(), TrayPanelPalette.Light).Scene;
        Assert.Contains(scene.Commands.OfType<TextCommand>(), text => text.Text == $"Unhealthy · {location} · PID 48213");
    }

    [Theory]
    [InlineData(true, false, false, (int)AppHostHealth.Healthy, (int)PanelStatusGlyph.Healthy)]
    [InlineData(true, false, false, (int)AppHostHealth.Warning, (int)PanelStatusGlyph.Warning)]
    [InlineData(true, false, false, (int)AppHostHealth.Unhealthy, (int)PanelStatusGlyph.Unhealthy)]
    [InlineData(true, false, false, (int)AppHostHealth.Unknown, (int)PanelStatusGlyph.Unknown)]
    [InlineData(false, false, false, (int)AppHostHealth.Healthy, (int)PanelStatusGlyph.Unknown)]
    [InlineData(true, true, false, (int)AppHostHealth.Healthy, (int)PanelStatusGlyph.Busy)]
    [InlineData(true, false, true, (int)AppHostHealth.Healthy, (int)PanelStatusGlyph.Warning)]
    public void StatusGlyphFollowsHealthAndActivity(bool live, bool stopping, bool error, int health, int expected)
    {
        var host = TrayPanelSamples.Running("/src/A/A.AppHost.csproj", 1, (AppHostHealth)health, stopping: stopping, error: error ? "Failed." : null);

        Assert.Equal((PanelStatusGlyph)expected, TrayPanel.GetStatusGlyph(host, live));
    }

    [Fact]
    public void OfflineRowsUseStoppedOrBusyGlyphs()
    {
        Assert.Equal(PanelStatusGlyph.Stopped, TrayPanel.GetStatusGlyph(TrayPanelSamples.Offline("/src/A/A.cs"), live: true));
        Assert.Equal(PanelStatusGlyph.Busy, TrayPanel.GetStatusGlyph(TrayPanelSamples.Offline("/src/A/A.cs", starting: true), live: true));
        Assert.Equal(PanelStatusGlyph.Warning, TrayPanel.GetStatusGlyph(TrayPanelSamples.Offline("/src/A/A.cs", error: "No."), live: true));
    }

    [Theory]
    [MemberData(nameof(SampleNames))]
    public void EverySingleLineTextStartsOnAColumnAndSitsOnItsLine(string name)
    {
        var scene = CreatePanel(name, TrayPanelPalette.Light).Scene;
        double[] columns = [TrayPanel.EdgeMargin, TrayPanel.TextLeft, TrayPanel.NestedTextLeft];
        foreach (var text in scene.Commands.OfType<TextCommand>().Where(text => text.MaxLines == 1))
        {
            // Leading text starts on a column, except the header status, which follows the
            // title on its baseline.
            if (text.Alignment == PanelTextAlignment.Leading && text.ExplicitBaseline is null)
            {
                Assert.True(columns.Contains(text.Bounds.X), $"{name}: \"{text.Text}\" starts at {text.Bounds.X}, not on a column");
            }
            // Its capitals are centered on the line it belongs to, or it shares that line's baseline.
            var capMiddle = text.Baseline - PanelFonts.CapHeight(text.Font) / 2;
            var lines = scene.Guides.Where(guide => guide.Bounds.Contains(text.Bounds.CenterX, text.Bounds.CenterY)).ToList();
            Assert.True(lines.Any(guide => guide.Kind == PanelGuideKind.Center && Math.Abs(guide.Position - capMiddle) < 0.01
                    || guide.Kind == PanelGuideKind.Baseline && Math.Abs(guide.Position - text.Baseline) < 0.01),
                $"{name}: \"{text.Text}\" has its capitals centered at {capMiddle:F2}, on none of the lines {string.Join(", ", lines.Where(l => l.Kind != PanelGuideKind.Column).Select(l => $"{l.Label} {l.Position}"))}");
        }
    }

    [Theory]
    [MemberData(nameof(SampleNames))]
    public void IconButtonsAndGlyphsSitOnTheLeadingAndTrailingColumns(string name)
    {
        var scene = CreatePanel(name, TrayPanelPalette.Dark).Scene;
        // Trailing buttons are 28 DIPs wide with a 2-DIP gap: centers at 330, 300 and 270.
        double[] trailing = [TrayPanel.TrailingCenter, TrayPanel.TrailingCenter - 30, TrayPanel.TrailingCenter - 60];
        var centers = scene.Guides.Where(guide => guide.Kind == PanelGuideKind.Center).ToList();
        foreach (var button in scene.Targets.Where(target => target.Kind is PanelTargetKind.Dashboard or PanelTargetKind.Stop
            or PanelTargetKind.Start or PanelTargetKind.More or PanelTargetKind.Settings or PanelTargetKind.Documentation
            or PanelTargetKind.Quit or PanelTargetKind.Back).Where(target => !target.HitBounds.IsEmpty))
        {
            Assert.Equal(TrayPanel.ButtonSize, button.Bounds.Width);
            if (button.Kind == PanelTargetKind.Back)
            {
                Assert.Equal(TrayPanel.LeadingCenter, button.Bounds.CenterX);
            }
            else
            {
                Assert.Contains(button.Bounds.CenterX, trailing);
            }
            Assert.True(centers.Any(guide => guide.Bounds.Contains(button.Bounds.CenterX, button.Bounds.CenterY) && guide.Position == button.Bounds.CenterY),
                $"{name}: {button.Key} is centered at y {button.Bounds.CenterY}, on no line");
        }
        foreach (var box in scene.Guides.Where(guide => guide.Kind == PanelGuideKind.Box))
        {
            if (box.Label is "footer icon" or "row button icon" or "recent chevron")
            {
                Assert.Contains(box.Bounds.CenterX, trailing);
            }
            else if (box.Label == "open-in app icon")
            {
                // Nested applications draw their own icon on the text column; the guide must be where it is drawn.
                Assert.Contains(scene.Commands.OfType<AppIconCommand>(), icon => icon.Bounds == box.Bounds);
                Assert.Equal(TrayPanel.TextLeft, box.Bounds.X);
            }
            else if (box.Label != "empty art")
            {
                Assert.Equal(TrayPanel.LeadingCenter, box.Bounds.CenterX);
            }
        }
        // Row highlights and cards share the row inset on both sides.
        Assert.All(scene.Targets.Where(target => target.Kind is PanelTargetKind.Row or PanelTargetKind.LaunchAtSignIn),
            row => Assert.True(row.Bounds.X == TrayPanel.RowInset && row.Bounds.Right == TrayPanel.Width - TrayPanel.RowInset, $"{name}: {row.Key} at {row.Bounds}"));
    }

    [Fact]
    public void OpenInApplicationGuidesSitOnTheirDrawnIcons()
    {
        var scene = CreatePanel("actions-open-in", TrayPanelPalette.Light).Scene;
        var icons = scene.Commands.OfType<AppIconCommand>().Select(icon => icon.Bounds).ToList();
        var guides = scene.Guides.Where(guide => guide.Label == "open-in app icon").Select(guide => guide.Bounds).ToList();

        Assert.NotEmpty(icons);
        Assert.Equal(icons, guides);
        Assert.All(scene.Guides.Where(guide => guide.Label == "action icon"), guide => Assert.Equal(TrayPanel.LeadingCenter, guide.Bounds.CenterX));
    }

    [Fact]
    public void LogoTitleAndStatusShareTheHeaderLine()
    {
        var scene = new TrayPanel(TrayPanelSamples.Typical(), TrayPanelPalette.Light).Scene;
        var logo = scene.Commands.OfType<LogoCommand>().Single();
        var title = scene.Commands.OfType<TextCommand>().Single(text => text.Text == "Aspire");
        var status = scene.Commands.OfType<TextCommand>().Single(text => text.ExplicitBaseline is not null);
        var dot = scene.Commands.OfType<FillPathCommand>().Single(fill => fill.Path.GetControlBounds() is { Width: 8, Height: 8 } bounds
            && bounds.CenterY < TrayPanel.HeaderHeight);

        // The logo and the title's capitals are centered on the bar, like the footer's icons.
        Assert.Equal(TrayPanel.HeaderHeight / 2, logo.Bounds.CenterY);
        Assert.Equal(TrayPanel.HeaderHeight / 2, title.Baseline - PanelFonts.CapHeight(PanelFontRole.Title) / 2);
        Assert.Equal(TrayPanel.LeadingCenter, logo.Bounds.CenterX);
        Assert.Equal(TrayPanel.TextLeft, title.Bounds.X);
        // The smaller status shares the title's baseline, and the dot is centered on its capitals.
        Assert.Equal(title.Baseline, status.Baseline);
        Assert.Equal(status.Baseline - PanelFonts.CapHeight(PanelFontRole.Body) / 2, dot.Path.GetControlBounds().CenterY, 3);
        Assert.Equal(TrayPanel.Width - TrayPanel.EdgeMargin, status.Bounds.Right);
    }

    [Theory]
    [MemberData(nameof(SampleNames))]
    public void HeaderIsAOneLineBarMatchingTheFooter(string name)
    {
        foreach (var palette in new[] { TrayPanelPalette.Light, TrayPanelPalette.Dark,
            TrayPanelPalette.FromSystemColors(new(0, 0, 0), new(255, 255, 255), new(26, 235, 255), new(63, 242, 63)) })
        {
            var panel = CreatePanel(name, palette);
            var scene = panel.Scene;
            var gutters = scene.Commands.OfType<FillPathCommand>().Where(fill => fill.Color == palette.Gutter)
                .Select(fill => fill.Path.GetControlBounds()).ToList();

            Assert.Equal(TrayPanel.FooterHeight, TrayPanel.HeaderHeight);
            // Rounded corners come from arc math, so compare with a tolerance.
            static bool Near(PanelRect a, PanelRect b)
                => Math.Abs(a.X - b.X) < 1e-9 && Math.Abs(a.Y - b.Y) < 1e-9 && Math.Abs(a.Right - b.Right) < 1e-9 && Math.Abs(a.Bottom - b.Bottom) < 1e-9;
            Assert.Contains(gutters, bounds => Near(bounds, new(0, 0, TrayPanel.Width, TrayPanel.HeaderHeight)));
            Assert.Contains(gutters, bounds => Near(bounds, new(0, scene.Height - TrayPanel.FooterHeight, TrayPanel.Width, TrayPanel.FooterHeight)));
            var separators = scene.Commands.OfType<FillPathCommand>().Where(fill => fill.Color == palette.Separator)
                .Select(fill => fill.Path.GetControlBounds()).ToList();
            Assert.Contains(new PanelRect(0, TrayPanel.HeaderHeight - 1, TrayPanel.Width, 1), separators);
            Assert.Contains(new PanelRect(0, scene.Height - TrayPanel.FooterHeight, TrayPanel.Width, 1), separators);

            // Everything in the header is centered on one line, like the footer's buttons.
            var middle = TrayPanel.HeaderHeight / 2;
            var header = scene.Commands.OfType<TextCommand>().Where(text => text.Bounds.Bottom <= TrayPanel.HeaderHeight).ToList();
            Assert.NotEmpty(header);
            Assert.All(header, text => Assert.Equal(middle, text.Bounds.CenterY));
            Assert.All(scene.Commands.OfType<LogoCommand>(), logo => Assert.Equal(new PanelRect(20, middle - 10, 20, 20), logo.Bounds));
            Assert.All(scene.Targets.Where(target => target.Kind == PanelTargetKind.Back),
                back => Assert.Equal(middle, back.Bounds.CenterY));
            Assert.All(scene.Targets.TakeLast(3), button => Assert.Equal(scene.Height - TrayPanel.FooterHeight / 2, button.Bounds.CenterY));
        }
    }

    [Fact]
    public void HeaderReadsTitleThenStatusOnOneLine()
    {
        var state = TrayPanelSamples.Typical();
        var scene = new TrayPanel(state, TrayPanelPalette.Light).Scene;
        var title = scene.Accessibility.Single(element => element.Key.Label == "header.title");
        var status = scene.Accessibility.Single(element => element.Key.Label == "header.status");

        Assert.Equal("Aspire", title.Name);
        Assert.Equal(TrayPanel.GetHeaderStatus(state, TrayPanelPalette.Light).Text, status.Name);
        Assert.Equal(title.Bounds.CenterY, status.Bounds.CenterY);
        Assert.True(title.Bounds.Right < status.Bounds.X);
    }

    [Fact]
    public void NoticeShowsABannerAndPushesTheListDown()
    {
        var withNotice = new TrayPanel(TrayPanelSamples.Errors(), TrayPanelPalette.Light);
        var without = new TrayPanel(TrayPanelSamples.Errors() with { Notice = null }, TrayPanelPalette.Light);

        Assert.Contains(withNotice.Scene.Commands.OfType<TextCommand>(), text => text.Text.StartsWith("Unable to save", StringComparison.Ordinal));
        Assert.True(withNotice.Viewport.Y > without.Viewport.Y);
        Assert.Equal(TrayPanel.HeaderHeight, without.Viewport.Y);
    }

    [Theory]
    [InlineData(0, 90)]
    [InlineData(-60, 300)]
    [InlineData(45, -200)]
    [InlineData(0, 360)]
    public void ArcsUseSegmentsOfAtMostNinetyDegreesThatStayOnTheCircle(double start, double sweep)
    {
        var path = new PanelPath().Arc(10, 20, 5, start, sweep);
        var cubics = path.Segments.Where(segment => segment.Verb == PanelPathVerb.CubicTo).ToList();

        Assert.Equal((int)Math.Ceiling(Math.Abs(sweep) / 90), cubics.Count);
        foreach (var cubic in cubics)
        {
            Assert.Equal(5, Math.Sqrt(Math.Pow(cubic.X3 - 10, 2) + Math.Pow(cubic.Y3 - 20, 2)), 6);
            // The curve midpoint of a 4/3 tan(φ/4) approximation is within 0.03% of the radius.
            var mid = Midpoint(path.Segments[path.Segments.ToList().IndexOf(cubic) - 1], cubic);
            Assert.Equal(5, Math.Sqrt(Math.Pow(mid.X - 10, 2) + Math.Pow(mid.Y - 20, 2)), 2);
        }
        var end = (start + sweep) * Math.PI / 180;
        Assert.Equal(10 + 5 * Math.Cos(end), cubics[^1].X3, 6);
        Assert.Equal(20 + 5 * Math.Sin(end), cubics[^1].Y3, 6);
    }

    [Fact]
    public void QuarterArcControlPointsMatchTheStandardKappa()
    {
        var cubic = new PanelPath().Arc(0, 0, 1, 0, 90).Segments[1];

        Assert.Equal(PanelPath.QuarterArcKappa, cubic.Y1, 6);
        Assert.Equal(PanelPath.QuarterArcKappa, cubic.X2, 6);
    }

    [Fact]
    public void RoundedRectStaysInsideItsBounds()
    {
        var rect = new PanelRect(8, 10, 100, 40);
        var path = PanelPath.RoundedRect(rect, 6);

        Assert.Equal(rect, path.GetControlBounds());
        Assert.Equal(PanelPathVerb.Close, path.Segments[^1].Verb);
    }

    [Theory]
    [InlineData((int)PanelStatusGlyph.Healthy)]
    [InlineData((int)PanelStatusGlyph.Warning)]
    [InlineData((int)PanelStatusGlyph.Unhealthy)]
    [InlineData((int)PanelStatusGlyph.Unknown)]
    [InlineData((int)PanelStatusGlyph.Stopped)]
    [InlineData((int)PanelStatusGlyph.Busy)]
    public void StatusGlyphsStayInsideTheirBox(int value)
    {
        var glyph = (PanelStatusGlyph)value;
        var commands = new List<PanelCommand>();
        var box = new PanelRect(40, 60, 20, 20);
        TrayPanelGlyphs.AddStatus(commands, glyph, box, TrayPanelPalette.Light);

        Assert.NotEmpty(commands);
        Assert.All(commands, command => Assert.True(box.Contains(GetBounds(command)), $"{glyph}: {GetBounds(command)}"));
    }

    [Theory]
    [InlineData((int)PanelIcon.ExternalLink)]
    [InlineData((int)PanelIcon.Stop)]
    [InlineData((int)PanelIcon.Play)]
    [InlineData((int)PanelIcon.More)]
    [InlineData((int)PanelIcon.Gear)]
    [InlineData((int)PanelIcon.Power)]
    [InlineData((int)PanelIcon.Globe)]
    [InlineData((int)PanelIcon.ChevronDown)]
    [InlineData((int)PanelIcon.ChevronRight)]
    [InlineData((int)PanelIcon.Alert)]
    [InlineData((int)PanelIcon.Layers)]
    [InlineData((int)PanelIcon.AppWindow)]
    [InlineData((int)PanelIcon.Folder)]
    [InlineData((int)PanelIcon.Copy)]
    [InlineData((int)PanelIcon.Pin)]
    [InlineData((int)PanelIcon.Unpin)]
    [InlineData((int)PanelIcon.Remove)]
    public void IconsStayInsideTheirBox(int value)
    {
        var icon = (PanelIcon)value;
        var commands = new List<PanelCommand>();
        var box = new PanelRect(4, 8, 16, 16);
        TrayPanelGlyphs.AddIcon(commands, icon, box, TrayPanelPalette.Light.TextPrimary);

        Assert.NotEmpty(commands);
        Assert.All(commands, command => Assert.True(box.Contains(GetBounds(command)), $"{icon}: {GetBounds(command)}"));
    }

    [Fact]
    public void PalettesParseTheSpecifiedColors()
    {
        Assert.Equal(new PanelColor(0xF9, 0xF9, 0xFB), TrayPanelPalette.Light.Background);
        Assert.Equal(new PanelColor(0x6A, 0x4B, 0xD6), TrayPanelPalette.Light.Accent);
        Assert.Equal(new PanelColor(0, 0, 0, 20), TrayPanelPalette.Light.Separator);
        Assert.Equal(new PanelColor(0x25, 0x25, 0x29), TrayPanelPalette.Dark.Background);
        Assert.Equal(new PanelColor(255, 255, 255, 18), TrayPanelPalette.Dark.Hover);
        Assert.Equal("#11223344", PanelColor.FromHex("#11223344").ToHex());
        Assert.Equal(new PanelColor(128, 128, 128), new PanelColor(255, 255, 255, 128).Over(new(0, 0, 0)));
    }

    [Theory]
    [InlineData("light")]
    [InlineData("dark")]
    public void ConfirmationButtonLabelsKeepReadableContrast(string name)
    {
        var palette = name == "dark" ? TrayPanelPalette.Dark : TrayPanelPalette.Light;
        var state = TrayPanelSamples.Typical();
        var panel = new TrayPanel(state, palette);
        Click(panel, panel.Scene.Targets.Single(target => target.Kind == PanelTargetKind.ClearRecent).HitBounds);
        var accept = panel.Scene.Targets.Single(target => target.Kind == PanelTargetKind.ConfirmAccept);
        var label = Assert.Single(panel.Scene.Commands.OfType<TextCommand>(), text => text.Bounds == accept.Bounds);
        var fill = panel.Scene.Commands.OfType<FillPathCommand>().Last(fill => fill.Path.GetControlBounds().Contains(accept.Bounds.CenterX, accept.Bounds.CenterY)
            && fill.Path.GetControlBounds().Width <= accept.Bounds.Width + 1);

        // 12 px labels need 4.5:1 (WCAG AA), also against the darkened hover and pressed fills.
        Assert.Equal(palette.DangerFill, fill.Color);
        Assert.True(TrayPanelPalette.ContrastRatio(label.Color, fill.Color) >= 4.5, $"{name}: {TrayPanelPalette.ContrastRatio(label.Color, fill.Color):F2}:1");
        Assert.True(TrayPanelPalette.ContrastRatio(palette.OnColor, PanelColor.FromRgba(0, 0, 0, 0.22).Over(palette.DangerFill)) >= 4.5);
    }

    [Theory]
    [InlineData("light")]
    [InlineData("dark")]
    public void SmallTextKeepsContrastOnEveryListBackground(string name)
    {
        // Section labels (TextTertiary), subtitles (TextSecondary) and error lines (Danger) are
        // 11 and 12 px, so they need 4.5:1 on the panel background and on the hover and pressed
        // tints drawn over it; the unhealthy disc is filled so its white cross keeps 4.5:1 too.
        var palette = name == "dark" ? TrayPanelPalette.Dark : TrayPanelPalette.Light;
        var backgrounds = new[] { palette.Background, palette.Hover.Over(palette.Background), palette.Pressed.Over(palette.Background) };

        foreach (var text in new[] { palette.TextSecondary, palette.TextTertiary, palette.Danger })
        {
            Assert.All(backgrounds, background => Assert.True(TrayPanelPalette.ContrastRatio(text, background) >= 4.5,
                $"{name}: {text.ToHex()} on {background.ToHex()} is {TrayPanelPalette.ContrastRatio(text, background):F2}:1"));
        }
        Assert.True(TrayPanelPalette.ContrastRatio(palette.OnColor, palette.DangerFill) >= 4.5);
        var commands = new List<PanelCommand>();
        TrayPanelGlyphs.AddStatus(commands, PanelStatusGlyph.Unhealthy, new(0, 0, 20, 20), palette);
        Assert.Equal(palette.DangerFill, commands.OfType<FillPathCommand>().First().Color);
    }

    public static TheoryData<string> ContrastPaletteNames => new(
        "light", "dark", "light-contrast", "dark-contrast", "hc-black", "hc-white", "hc-aquatic", "hc-desert", "hc-low-gray");

    /// <summary>
    /// Palettes to check status marks in. The high-contrast ones use the Windows 11 contrast
    /// themes' window, window text, highlight and gray text colors, plus a theme whose gray text
    /// is too close to its window color for the window color alone to work as a mark.
    /// </summary>
    private static TrayPanelPalette ContrastPalette(string name) => name switch
    {
        "light" => TrayPanelPalette.Light,
        "dark" => TrayPanelPalette.Dark,
        "light-contrast" => TrayPanelPalette.Light.WithIncreasedContrast(),
        "dark-contrast" => TrayPanelPalette.Dark.WithIncreasedContrast(),
        "hc-black" => TrayPanelPalette.FromSystemColors(new(0, 0, 0), new(255, 255, 255), new(26, 235, 255), new(63, 242, 63)),
        "hc-white" => TrayPanelPalette.FromSystemColors(new(255, 255, 255), new(0, 0, 0), new(55, 0, 110), new(96, 0, 0)),
        "hc-aquatic" => TrayPanelPalette.FromSystemColors(new(32, 32, 32), new(255, 255, 255), new(142, 227, 240), new(167, 167, 167)),
        "hc-desert" => TrayPanelPalette.FromSystemColors(new(255, 250, 239), new(61, 61, 61), new(144, 57, 4), new(103, 103, 103)),
        _ => TrayPanelPalette.FromSystemColors(new(0, 0, 0), new(255, 255, 255), new(26, 235, 255), new(40, 40, 40))
    };

    [Theory]
    [MemberData(nameof(ContrastPaletteNames))]
    public void StatusMarksKeepThreeToOneAgainstTheirDisc(string name)
    {
        // The check, "!", cross and "?" are what tell the states apart without color, so each
        // needs 3:1 against its disc (WCAG 1.4.11, non-text contrast).
        var palette = ContrastPalette(name);
        foreach (var (glyph, disc, mark) in new[]
        {
            (PanelStatusGlyph.Healthy, palette.Success, palette.SuccessMark),
            (PanelStatusGlyph.Warning, palette.Warning, palette.WarningMark),
            (PanelStatusGlyph.Unhealthy, palette.DangerFill, palette.DangerMark),
            (PanelStatusGlyph.Unknown, palette.Neutral, palette.NeutralMark)
        })
        {
            var ratio = TrayPanelPalette.ContrastRatio(mark, disc);
            Assert.True(ratio >= TrayPanelPalette.MinimumMarkContrast, $"{name} {glyph}: {mark.ToHex()} on {disc.ToHex()} is {ratio:F2}:1");

            // The glyph draws exactly those colors: the disc first, then only the mark.
            var commands = new List<PanelCommand>();
            TrayPanelGlyphs.AddStatus(commands, glyph, new(0, 0, 20, 20), palette);
            Assert.Equal(disc, Assert.IsType<FillPathCommand>(commands[0]).Color);
            Assert.All(commands.Skip(1), command => Assert.Equal(mark, command switch
            {
                FillPathCommand fill => fill.Color,
                StrokePathCommand stroke => stroke.Color,
                _ => throw new InvalidOperationException()
            }));
        }
    }

    [Fact]
    public void StatusMarksStayWhiteWhereWhitePassesAndDarkenOnlyWhereItFails()
    {
        var white = PanelColor.FromHex("#FFFFFF");
        var dark = PanelColor.FromHex("#1C1C1E");

        Assert.Equal([white, dark, white, white], Marks(TrayPanelPalette.Light));
        Assert.Equal([dark, dark, white, white], Marks(TrayPanelPalette.Dark));
        // The switch knob and the danger button keep their white label color.
        Assert.Equal(white, TrayPanelPalette.Light.OnColor);
        Assert.Equal(white, TrayPanelPalette.Dark.OnColor);

        static PanelColor[] Marks(TrayPanelPalette palette)
            => [palette.SuccessMark, palette.WarningMark, palette.DangerMark, palette.NeutralMark];
    }

    [Fact]
    public void HighContrastPaletteUsesSystemColors()
    {
        var palette = TrayPanelPalette.FromSystemColors(new(0, 0, 0), new(255, 255, 255), new(26, 235, 255), new(63, 242, 63));
        var scene = new TrayPanel(TrayPanelSamples.Errors(), palette).Scene;

        Assert.True(palette.IsHighContrast);
        Assert.Equal(new PanelColor(255, 255, 255), palette.TextSecondary);
        Assert.All(scene.Commands.OfType<TextCommand>().Where(text => text.Color != palette.Accent),
            text => Assert.Equal(new PanelColor(255, 255, 255), text.Color));
    }

    [Theory]
    [MemberData(nameof(SampleNames))]
    public void SvgRendererProducesWellFormedDocuments(string name)
    {
        var svg = TrayPanelSvgRenderer.Render(CreatePanel(name, TrayPanelPalette.Dark).Scene);
        var document = XDocument.Parse(svg);

        Assert.Equal("svg", document.Root!.Name.LocalName);
        Assert.Contains(document.Descendants(), element => element.Name.LocalName == "text");
    }

    /// <summary>
    /// Writes preview images of each state to ASPIRE_TRAY_PANEL_PREVIEW_DIR, or to the test output
    /// directory, for design review and documentation.
    /// </summary>
    [Fact]
    public void WritePreviewImages()
    {
        var directory = Environment.GetEnvironmentVariable("ASPIRE_TRAY_PANEL_PREVIEW_DIR") is { Length: > 0 } configured
            ? configured
            : Path.Combine(AppContext.BaseDirectory, "TrayPanelPreviews");
        Directory.CreateDirectory(directory);
        var logo = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Assets", "aspire.png"));
        var written = new List<string>();
        foreach (var palette in new[] { TrayPanelPalette.Light, TrayPanelPalette.Dark })
        {
            foreach (var name in s_sampleNames)
            {
                var panel = CreatePanel(name, palette, maxHeight: 520);
                switch (name)
                {
                    case "typical":
                        // Show the hover affordance on the second row.
                        var row = panel.Scene.Targets.Where(target => target.Kind == PanelTargetKind.Row).ElementAt(1);
                        panel.PointerMoved(row.Bounds.X + 40, row.Bounds.CenterY);
                        break;
                    case "many":
                        panel.Scroll(-140);
                        panel.PointerMoved(200, panel.Viewport.Y + 140);
                        break;
                }
                var path = Path.Combine(directory, $"{name}-{palette.Name}.svg");
                File.WriteAllText(path, TrayPanelSvgRenderer.Render(panel.Scene, logo, margin: 24));
                written.Add(path);
                // The same preview with the alignment grid drawn over it.
                var guides = Path.Combine(directory, $"{name}-{palette.Name}-guides.svg");
                File.WriteAllText(guides, TrayPanelSvgRenderer.Render(panel.Scene, logo, margin: 24, guides: true));
                written.Add(guides);
            }
        }

        Assert.All(written, path => Assert.True(new FileInfo(path).Length > 0));
    }

    [Theory]
    [MemberData(nameof(SampleNames))]
    public void AccessibilityListsEveryTargetOnceInReadingOrder(string name)
    {
        var scene = CreatePanel(name, TrayPanelPalette.Light).Scene;

        Assert.Equal(scene.Targets, scene.Accessibility.Where(element => element.Target is not null).Select(element => element.Target));
        Assert.Equal(scene.Accessibility.Count, scene.Accessibility.Select(element => element.Key).Distinct().Count());
        Assert.Equal([PanelTargetKind.Settings, PanelTargetKind.Documentation, PanelTargetKind.Quit],
            scene.Accessibility.TakeLast(3).Select(element => element.Target!.Kind));
        Assert.All(scene.Accessibility, element => Assert.False(string.IsNullOrWhiteSpace(element.Name)));
    }

    [Fact]
    public void AccessibilityReadsTheHeaderAndSectionTitlesAsText()
    {
        var state = TrayPanelSamples.Typical();
        var scene = new TrayPanel(state, TrayPanelPalette.Light).Scene;
        var (status, _) = TrayPanel.GetHeaderStatus(state, TrayPanelPalette.Light);

        Assert.Equal(
            [("Aspire", PanelAccessibleRole.Text), (status, PanelAccessibleRole.Text), ("Running", PanelAccessibleRole.Text)],
            scene.Accessibility.Take(3).Select(element => (element.Name, element.Role)));
        var firstRow = scene.Accessibility[3];
        Assert.Equal(PanelTargetKind.Row, firstRow.Target?.Kind);
        Assert.Equal(state.AppHosts[0], firstRow.Target?.Host);
        Assert.Equal(PanelAccessibleRole.Button, firstRow.Role);
        var recent = scene.Accessibility.Single(element => element.Target?.Kind == PanelTargetKind.ToggleRecent);
        Assert.Equal("Show 2 recent AppHosts", recent.Name);
    }

    [Fact]
    public void ConfirmationStripReadsItsQuestionBeforeItsControls()
    {
        var panel = new TrayPanel(TrayPanelSamples.Typical(), TrayPanelPalette.Light);
        panel.Invoke(panel.Scene.Targets.Single(target => target.Kind == PanelTargetKind.ClearRecent).Key);

        var strip = panel.Scene.Accessibility.SkipWhile(element => element.Key.Label != "confirm.question").Take(4).ToList();

        Assert.Equal(
            [
                ("Clear recent AppHosts?", PanelAccessibleRole.Text, (bool?)null),
                ("Pinned AppHosts are kept.", PanelAccessibleRole.Text, null),
                ("Cancel", PanelAccessibleRole.Button, null),
                ("Clear recent AppHosts", PanelAccessibleRole.Button, null)
            ],
            strip.Select(element => (element.Name, element.Role, element.Checked)));
    }

    [Fact]
    public void InvokeActivatesLikeAClickAndMovesFocus()
    {
        var panel = new TrayPanel(TrayPanelSamples.Typical(), TrayPanelPalette.Light);

        Assert.Null(panel.Invoke(panel.Scene.Targets.Single(target => target.Kind == PanelTargetKind.ClearRecent).Key).Action);
        Assert.Equal(PanelConfirmKind.ClearRecent, panel.Confirmation);
        Assert.Equal(PanelTargetKind.ConfirmCancel, panel.FocusedTarget?.Kind);

        var accept = panel.Scene.Targets.Single(target => target.Kind == PanelTargetKind.ConfirmAccept).Key;
        var clear = panel.Invoke(accept).Action;
        Assert.Equal(PanelActionKind.ClearRecent, clear?.Kind);
        Assert.Null(panel.Confirmation);
    }

    [Fact]
    public void InvokeIgnoresDisabledAndMissingTargets()
    {
        var state = TrayPanelSamples.Typical();
        var panel = new TrayPanel(state, TrayPanelPalette.Light);
        var disabled = Target(panel, PanelTargetKind.Dashboard, state.AppHosts[2]);
        Assert.False(disabled.Enabled);

        Assert.Equal(PanelResponse.None, panel.Invoke(disabled.Key));
        Assert.Equal(PanelResponse.None, panel.Invoke(new(PanelTargetKind.Back, PanelSection.None, null)));
        Assert.Null(panel.FocusedTarget);
    }

    [Fact]
    public void FocusScrollsAnOffscreenElementIntoView()
    {
        var panel = new TrayPanel(TrayPanelSamples.Many(), TrayPanelPalette.Light) { MaxHeight = 400 };
        var last = panel.Scene.Accessibility.Last(element => element.Target?.Kind == PanelTargetKind.Row);
        Assert.True(last.IsOffscreen);
        var status = panel.Scene.Accessibility.Single(element => element.Key.Label == "header.status");
        Assert.Equal(status.Bounds, status.VisibleBounds);

        Assert.True(panel.Focus(last.Target!.Key).Redraw);

        Assert.Equal(last.Target.Key, panel.FocusedTarget);
        Assert.True(panel.ScrollOffset > 0);
        var visible = panel.Scene.Accessibility.Single(element => element.Key == last.Key);
        Assert.False(visible.IsOffscreen);
        Assert.Equal(visible.Bounds, visible.VisibleBounds);
        Assert.True(panel.Scene.Accessibility.Single(element => element.Key.Label == "section.running").IsOffscreen);
    }

    [Fact]
    public void SettingsSwitchesCarryTheirDescription()
    {
        var panel = new TrayPanel(TrayPanelSamples.Typical(), TrayPanelPalette.Light);
        panel.ShowSettings(new(new TrayStartupState(false, true, null), null, Preview: false));

        var launch = panel.Scene.Accessibility.Single(element => element.Target?.Kind == PanelTargetKind.LaunchAtSignIn);

        Assert.Equal(PanelAccessibleRole.Switch, launch.Role);
        Assert.Equal(TraySettingsText.PanelStartupOption, launch.Name);
        Assert.Equal(TraySettingsText.StartupDescription, launch.Description);
        Assert.False(launch.Checked);
        Assert.Equal(["Back to AppHosts", TraySettingsText.PanelTitle, "General"],
            panel.Scene.Accessibility.Take(3).Select(element => element.Name));
    }

    [Fact]
    public void IconButtonsHaveTooltipsAndLabeledControlsDoNot()
    {
        var state = TrayPanelSamples.Typical();
        var panel = new TrayPanel(state, TrayPanelPalette.Light);
        var running = state.AppHosts[0];
        var stopped = state.AppHosts[3];

        Assert.Equal("Open dashboard", Target(panel, PanelTargetKind.Dashboard, running).Tooltip);
        Assert.Equal("Stop", Target(panel, PanelTargetKind.Stop, running).Tooltip);
        Assert.Equal("Start", Target(panel, PanelTargetKind.Start, stopped).Tooltip);
        Assert.Equal("More actions", Target(panel, PanelTargetKind.More, running).Tooltip);
        Assert.Equal(["Settings", "Documentation", "Quit the tray. AppHosts keep running."],
            panel.Scene.Targets.TakeLast(3).Select(target => target.Tooltip));
        Assert.All(panel.Scene.Targets.Where(target => target.Kind is PanelTargetKind.Row or PanelTargetKind.ToggleRecent or PanelTargetKind.ClearRecent),
            target => Assert.Null(target.Tooltip));

        Click(panel, Target(panel, PanelTargetKind.More, running).HitBounds);
        Assert.Equal("Hide actions", Target(panel, PanelTargetKind.More, running).Tooltip);
        panel.ShowSettings(new(null, null, Preview: false));
        Assert.Equal("Back", panel.Scene.Targets.Single(target => target.Kind == PanelTargetKind.Back).Tooltip);
        Assert.Equal("Close settings", panel.Scene.Targets.Single(target => target.Kind == PanelTargetKind.Settings).Tooltip);
    }

    [Theory]
    [InlineData("Quit")]
    [InlineData("Exit")]
    public void QuitSaysItClosesOnlyTheTray(string verb)
    {
        var panel = new TrayPanel(TrayPanelSamples.Typical(), TrayPanelPalette.Light) { QuitVerb = verb };
        var quit = panel.Scene.Accessibility.Single(element => element.Target?.Kind == PanelTargetKind.Quit);

        Assert.Equal(verb, quit.Name);
        Assert.Equal($"{verb} the tray. AppHosts keep running.", quit.Description);
        Assert.Equal($"{verb} the tray. AppHosts keep running.", quit.Target!.Tooltip);
        Assert.Equal(PanelActionKind.Quit, panel.Invoke(quit.Target.Key).Action?.Kind);
    }

    [Fact]
    public void FooterButtonsAreIconsOnTheRightInSettingsDocumentationQuitOrder()
    {
        var scene = new TrayPanel(TrayPanelSamples.Typical(), TrayPanelPalette.Light).Scene;
        var footer = scene.Targets.TakeLast(3).ToList();

        Assert.Equal([PanelTargetKind.Settings, PanelTargetKind.Documentation, PanelTargetKind.Quit], footer.Select(target => target.Kind));
        Assert.All(footer, target => Assert.Equal(TrayPanel.ButtonSize, target.Bounds.Width));
        Assert.True(footer[0].Bounds.Right <= footer[1].Bounds.X && footer[1].Bounds.Right <= footer[2].Bounds.X);
        Assert.DoesNotContain(scene.Commands.OfType<TextCommand>(), text => text.Text == "Documentation");
    }

    private static PanelHitTarget Target(TrayPanel panel, PanelTargetKind kind, AppHostMenuItem host)
        => panel.Scene.Targets.Single(target => target.Kind == kind && target.Host == host);

    private static PanelResponse Click(TrayPanel panel, PanelRect bounds, double? offsetX = null)
    {
        var x = offsetX is { } dx ? bounds.X + dx : bounds.CenterX;
        panel.PointerMoved(x, bounds.CenterY);
        panel.PointerDown(x, bounds.CenterY);
        return panel.PointerUp(x, bounds.CenterY);
    }

    private static void AssertNoOverlap(List<PanelHitTarget> targets)
    {
        for (var i = 0; i < targets.Count; i++)
        {
            for (var j = i + 1; j < targets.Count; j++)
            {
                Assert.False(targets[i].Bounds.IntersectsWith(targets[j].Bounds), $"{targets[i].Key} overlaps {targets[j].Key}");
            }
        }
    }

    private static PanelRect GetBounds(PanelCommand command)
    {
        var (path, width) = command switch
        {
            FillPathCommand fill => (fill.Path, 0.0),
            StrokePathCommand stroke => (stroke.Path, stroke.Width),
            _ => throw new InvalidOperationException()
        };
        // Control points bound the curve; a stroke extends half its width beyond the path.
        return path.GetControlBounds().Inset(-width / 2, -width / 2);
    }

    private static (double X, double Y) Midpoint(PanelPathSegment previous, PanelPathSegment cubic)
        => (0.125 * previous.X3 + 0.375 * cubic.X1 + 0.375 * cubic.X2 + 0.125 * cubic.X3,
            0.125 * previous.Y3 + 0.375 * cubic.Y1 + 0.375 * cubic.Y2 + 0.125 * cubic.Y3);
}
