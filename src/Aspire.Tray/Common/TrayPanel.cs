// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Tray;

/// <summary>
/// The flyout panel shown when the tray icon is activated. It is the tray's only UI: it owns the
/// layout and every bit of interaction state (hover, press, keyboard focus, scrolling, the
/// collapsed recent list, a row's open action list) and turns <see cref="TrayViewState"/> into a
/// <see cref="TrayPanelScene"/>. The macOS and Windows hosts only forward input, draw the display
/// list, and run the returned actions, so both platforms look and behave the same.
/// </summary>
/// <remarks>
/// Not thread-safe: hosts call it from their UI thread only.
/// </remarks>
internal sealed class TrayPanel
{
    public const double Width = 360;
    public const double CornerRadius = 8;
    public const double HeaderHeight = 44;
    public const double FooterHeight = 44;
    public const double RowHeight = 52;
    public const double SectionHeaderHeight = 28;
    public const double EmptyStateHeight = 132;
    public const double ListTopPadding = 4;
    public const double ListBottomPadding = 8;
    public const double ButtonSize = 28;
    public const double DefaultMaxHeight = 600;
    public const double ConfirmHeight = 92;
    public const double SettingsRowHeight = 62;
    public const double AboutHeight = 84;
    public const double ActionItemHeight = 30;

    // The action list is a card under its row: 4 px above and below its items, plus a 6 px gap
    // below the card so it doesn't touch the next row.
    private const double ActionListPadding = 4;
    private const double ActionListGap = 6;

    // The alignment grid. Everything in the panel lines up on these columns, which the scene
    // also emits as guides (TrayPanelScene.Guides) for tests and screenshot overlays:
    //
    //   8   RowInset: the left edge of row highlights, cards (action lists, confirmations) and
    //       the notice banner; Width - 8 is their right edge.
    //   20  EdgeMargin: the left edge of the leading column (status glyphs, the header logo,
    //       list icons) and of text that has no glyph (section labels, Settings text).
    //   30  LeadingCenter: the center of the leading column's 20-DIP glyphs and 16-DIP icons.
    //   52  TextLeft: where names, titles and item labels start.
    //   78  NestedTextLeft: the labels of items nested under another (the Open in applications),
    //       after their 16-DIP icon at TextLeft and a 10-DIP gap.
    //   330 TrailingCenter: the center of the last trailing button (More, Quit) and of trailing
    //       chevrons, mirroring LeadingCenter.
    //   340 Width - EdgeMargin: the right edge of trailing text and controls that aren't buttons.
    //   344 RowContentRight: the right edge of trailing 28-DIP buttons (TrailingCenter + 14).
    //
    // Vertically, each kind of line has one center: the header and footer bars at 22, a row's
    // glyph and buttons at its middle (26) with its name and detail lines centered at 17 and 35
    // around it, section labels at 16, and action list items at their middle (15).
    public const double RowInset = 8;
    public const double EdgeMargin = 20;
    public const double LeadingCenter = 30;
    public const double TextLeft = 52;
    public const double NestedTextLeft = TextLeft + 26;
    public const double TrailingCenter = Width - LeadingCenter;
    public const double RowContentRight = TrailingCenter + ButtonSize / 2;
    private const double ButtonGap = 2;

    // The banner estimates whether its text needs a second line from an average glyph width
    // for 12 px system UI text, rather than measuring, so both platforms lay it out the same.
    private const double AverageBodyCharacterWidth = 6.4;

    private TrayViewState _state;
    private TrayViewState? _pendingState;
    private TrayPanelPalette _palette;
    private TrayPanelScene? _scene;
    private double _maxHeight = DefaultMaxHeight;
    private double _scroll;
    private double _contentHeight;
    private PanelRect _viewport;
    private (double X, double Y)? _pointer;
    private PanelTargetKey? _hover;
    private PanelTargetKey? _pressed;
    private PanelTargetKey? _focus;
    private List<PanelAccessibleElement> _accessible = [];
    private bool _focusVisible;
    private bool? _recentExpanded;
    private PanelConfirm? _confirm;
    private PanelSettings? _settings;
    private (PanelSection Section, AppHostId Host)? _actions;
    private bool _openInExpanded;
    private IReadOnlyList<string> _openInApplications = [];
    private string _fileManagerName = "Finder";
    private string _quitVerb = "Quit";
    private double _listScroll;

    public TrayPanel(TrayViewState state, TrayPanelPalette palette)
    {
        _state = state;
        _palette = palette;
    }

    public TrayViewState State => _state;

    public TrayPanelPalette Palette
    {
        get => _palette;
        set
        {
            if (_palette != value)
            {
                _palette = value;
                _scene = null;
            }
        }
    }

    /// <summary>The tallest the panel may be, usually the work area height minus a margin.</summary>
    public double MaxHeight
    {
        get => _maxHeight;
        set
        {
            value = Math.Max(HeaderHeight + FooterHeight + RowHeight, value);
            if (_maxHeight != value)
            {
                _maxHeight = value;
                _scene = null;
            }
        }
    }

    public TrayPanelScene Scene => _scene ??= Build();

    /// <summary>The scrolled list area in panel coordinates.</summary>
    public PanelRect Viewport
    {
        get
        {
            _ = Scene;
            return _viewport;
        }
    }

    public double ScrollOffset
    {
        get
        {
            _ = Scene;
            return _scroll;
        }
    }

    public PanelTargetKey? FocusedTarget => _focus;

    public bool IsRecentExpanded => _recentExpanded ?? _state.AppHosts.Count == 0;

    /// <summary>The inline confirmation being shown, if any.</summary>
    public PanelConfirmKind? Confirmation => _confirm?.Kind;

    /// <summary>Whether the panel shows its Settings view instead of the AppHost list.</summary>
    public bool IsSettingsOpen => _settings is not null;

    /// <summary>The row whose action list is open, if any.</summary>
    public (PanelSection Section, AppHostId Host)? ActionsRow => _actions;

    /// <summary>
    /// The installed applications a row's action list offers to open the AppHost folder in, in
    /// display order. The host discovers them; each one becomes an "Open in" item.
    /// </summary>
    public IReadOnlyList<string> OpenInApplications
    {
        get => _openInApplications;
        set
        {
            if (!_openInApplications.SequenceEqual(value))
            {
                _openInApplications = [.. value];
                _scene = null;
            }
        }
    }

    /// <summary>The platform's file manager, for the "Show in …" item: "Finder" or "File Explorer".</summary>
    public string FileManagerName
    {
        get => _fileManagerName;
        set
        {
            if (_fileManagerName != value)
            {
                _fileManagerName = value;
                _scene = null;
            }
        }
    }

    /// <summary>
    /// The platform's verb for leaving an app: "Quit" on macOS, "Exit" on Windows, as tray apps
    /// there say. The button only closes the tray, so its tooltip says the AppHosts keep running.
    /// </summary>
    public string QuitVerb
    {
        get => _quitVerb;
        set
        {
            if (_quitVerb != value)
            {
                _quitVerb = value;
                _scene = null;
            }
        }
    }

    /// <summary>
    /// Shows the keyboard focus on the first control, for a panel opened from the keyboard or by a
    /// screen reader, so the user can see where Tab and the arrow keys start.
    /// </summary>
    public PanelResponse FocusFirst()
        => FocusAt(Scene.Targets.Where(target => target.Enabled && IsPrimary(target.Kind)).ToList(), 0);

    /// <summary>
    /// Switches to the Settings view, or refreshes it when it is already open. Focus starts on
    /// the back arrow so Enter or Escape returns to the list.
    /// </summary>
    public PanelResponse ShowSettings(PanelSettings settings)
    {
        if (_settings is null)
        {
            _confirm = null;
            _actions = null;
            _listScroll = _scroll;
            _scroll = 0;
            _focus = new(PanelTargetKind.Back, PanelSection.None, null);
        }
        _settings = settings;
        RefreshHover();
        return PanelResponse.Changed;
    }

    /// <summary>Refreshes the Settings view after a toggle was applied; ignored when it is closed.</summary>
    public PanelResponse UpdateSettings(PanelSettings settings)
    {
        if (_settings is null)
        {
            return PanelResponse.None;
        }
        _settings = settings;
        _scene = null;
        return PanelResponse.Changed;
    }

    /// <summary>
    /// Replaces a recent row with a prompt to remove it, after the host found its source file
    /// missing on Start. Confirming returns <see cref="PanelActionKind.RemoveRecent"/>.
    /// </summary>
    public PanelResponse ConfirmRemoveMissing(AppHostMenuItem host)
    {
        _settings = null;
        _actions = null;
        _recentExpanded = true;
        BeginConfirm(new(PanelConfirmKind.RemoveMissing, PanelSection.Recent, host));
        RefreshHover();
        return PanelResponse.Changed;
    }

    /// <summary>
    /// Replaces the state. While a press is in progress the update is deferred until release,
    /// so the row under the pointer cannot move between press and release.
    /// </summary>
    public bool Update(TrayViewState state)
    {
        if (_pressed is not null)
        {
            _pendingState = state;
            return false;
        }
        _state = state;
        _scene = null;
        return true;
    }

    /// <summary>Clears transient interaction state when the panel is shown again.</summary>
    public void Reset()
    {
        if (_pendingState is not null)
        {
            _state = _pendingState;
            _pendingState = null;
        }
        _pointer = null;
        _hover = null;
        _pressed = null;
        _focus = null;
        _focusVisible = false;
        _scroll = 0;
        _listScroll = 0;
        _confirm = null;
        _actions = null;
        _openInExpanded = false;
        _settings = null;
        _scene = null;
    }

    /// <summary>
    /// Activates a target the way a click would, for a screen reader's press (AXPress, UIA Invoke
    /// or Toggle). The target also takes keyboard focus, so a follow-up key press continues from it.
    /// </summary>
    public PanelResponse Invoke(PanelTargetKey key)
    {
        var target = Scene.Targets.FirstOrDefault(target => target.Key == key);
        if (target is not { Enabled: true })
        {
            return PanelResponse.None;
        }
        _focus = key;
        var action = Activate(target);
        if (_pendingState is not null)
        {
            _state = _pendingState;
            _pendingState = null;
        }
        _scene = null;
        return new(true, action);
    }

    /// <summary>
    /// Moves keyboard focus to a target when a screen reader sets focus on its element, and
    /// scrolls it into view like arrow keys do.
    /// </summary>
    public PanelResponse Focus(PanelTargetKey key)
    {
        var targets = Scene.Targets.Where(target => target.Enabled).ToList();
        var index = targets.FindIndex(target => target.Key == key);
        return index < 0 || _focus == key ? PanelResponse.None : FocusAt(targets, index);
    }

    public PanelHitTarget? HitTest(double x, double y)
    {
        var targets = Scene.Targets;
        // Later targets sit on top: row buttons are added after their row.
        for (var i = targets.Count - 1; i >= 0; i--)
        {
            if (targets[i].HitBounds.Contains(x, y))
            {
                return targets[i];
            }
        }
        return null;
    }

    public PanelResponse PointerMoved(double x, double y)
    {
        _pointer = (x, y);
        return SetHover(HitTest(x, y)?.Key);
    }

    public PanelResponse PointerLeft()
    {
        _pointer = null;
        return SetHover(null);
    }

    public PanelResponse PointerDown(double x, double y)
    {
        _pointer = (x, y);
        var target = HitTest(x, y);
        _hover = target?.Key;
        _pressed = target is { Enabled: true } ? target.Key : null;
        _focusVisible = false;
        _scene = null;
        return PanelResponse.Changed;
    }

    public PanelResponse PointerUp(double x, double y)
    {
        _pointer = (x, y);
        var target = HitTest(x, y);
        var action = _pressed is { } pressed && target is { Enabled: true } && target.Key == pressed ? Activate(target) : null;
        _pressed = null;
        if (_pendingState is not null)
        {
            _state = _pendingState;
            _pendingState = null;
        }
        _scene = null;
        // Hit test again: a deferred state update or a toggled section may have moved rows.
        _hover = HitTest(x, y)?.Key;
        _scene = null;
        return new(true, action);
    }

    /// <summary>
    /// Scrolls the list by <paramref name="deltaY"/> DIPs. A positive delta reveals content above,
    /// matching both NSEvent.scrollingDeltaY and a positive WM_MOUSEWHEEL delta (wheel pushed away).
    /// </summary>
    public PanelResponse Scroll(double deltaY)
    {
        var before = ScrollOffset;
        _scroll = Math.Clamp(before - deltaY, 0, Math.Max(0, _contentHeight - _viewport.Height));
        if (_scroll == before)
        {
            return PanelResponse.None;
        }
        _scene = null;
        if (_pointer is { } pointer)
        {
            _hover = HitTest(pointer.X, pointer.Y)?.Key;
            _scene = null;
        }
        return PanelResponse.Changed;
    }

    public PanelResponse Key(PanelKey key)
    {
        switch (key)
        {
            case PanelKey.Escape:
                // Escape steps back one level: out of a confirmation or an action list, then
                // out of Settings, and only then closes the panel.
                if (_confirm is not null)
                {
                    CancelConfirm();
                    return PanelResponse.Changed;
                }
                if (_actions is { } open && _openInExpanded)
                {
                    // Out of the "Open in" applications first, back to the item that showed them.
                    _openInExpanded = false;
                    _focus = new(PanelTargetKind.OpenInMenu, open.Section, open.Host);
                    _scene = null;
                    return PanelResponse.Changed;
                }
                if (_actions is not null)
                {
                    CloseActions(returnFocus: true);
                    return PanelResponse.Changed;
                }
                if (_settings is not null)
                {
                    CloseSettings();
                    return PanelResponse.Changed;
                }
                return new(false, new PanelAction(PanelActionKind.Close));
            case PanelKey.Enter or PanelKey.Space:
                var focused = Scene.Targets.FirstOrDefault(target => target.Key == _focus);
                if (focused is not { Enabled: true })
                {
                    return PanelResponse.None;
                }
                var action = Activate(focused);
                _scene = null;
                return new(true, action);
            case PanelKey.Tab or PanelKey.ShiftTab:
                return MoveFocus(Scene.Targets.Where(target => target.Enabled).ToList(), key == PanelKey.Tab ? 1 : -1, wrap: true);
            default:
                var primary = Scene.Targets.Where(target => target.Enabled && IsPrimary(target.Kind)).ToList();
                return key switch
                {
                    PanelKey.Home => FocusAt(primary, 0),
                    PanelKey.End => FocusAt(primary, primary.Count - 1),
                    _ => MoveFocus(primary, key == PanelKey.Down ? 1 : -1, wrap: false)
                };
        }
    }

    private static bool IsPrimary(PanelTargetKind kind)
        => kind is PanelTargetKind.Row or PanelTargetKind.ToggleRecent or PanelTargetKind.Documentation
            or PanelTargetKind.Settings or PanelTargetKind.Quit or PanelTargetKind.ConfirmCancel or PanelTargetKind.Back
            or PanelTargetKind.LaunchAtSignIn || IsActionItem(kind);

    private static bool IsConfirmTarget(PanelTargetKind kind)
        => kind is PanelTargetKind.ConfirmCancel or PanelTargetKind.ConfirmAccept;

    /// <summary>The items of a row's action list.</summary>
    private static bool IsActionItem(PanelTargetKind kind)
        => kind is PanelTargetKind.OpenInMenu or PanelTargetKind.OpenIn or PanelTargetKind.ShowInFolder or PanelTargetKind.CopyPath
            or PanelTargetKind.TogglePin or PanelTargetKind.RemoveRecent;

    /// <summary>
    /// Opens the action list of a row under it, or closes it when it is already open. The list
    /// takes the place of the native context menu: Open in, Show in Finder or File Explorer,
    /// Copy path, Pin or Unpin, and Remove for a recent row.
    /// </summary>
    private void ToggleActions(PanelTargetKey row)
    {
        if (_actions == (row.Section, row.Host!.Value))
        {
            CloseActions(returnFocus: true);
            return;
        }
        _confirm = null;
        _actions = (row.Section, row.Host!.Value);
        _openInExpanded = false;
        _scene = null;
        // Focus stays on the button that opened it: Down moves into the list.
        RevealActions(row);
    }

    /// <summary>
    /// Scrolls an action list that is below the fold into view, after it opened or grew, without
    /// ever scrolling its row out of view.
    /// </summary>
    private void RevealActions(PanelTargetKey row)
    {
        var items = Scene.Targets.Where(target => IsActionItem(target.Kind)).ToList();
        if (items.Count > 0)
        {
            var bottom = items.Max(target => target.Bounds.Bottom) + ActionListPadding;
            var top = Scene.Targets.FirstOrDefault(target => target.Key == row with { Kind = PanelTargetKind.Row, Item = 0 })?.Bounds.Y ?? bottom;
            if (bottom > _viewport.Bottom)
            {
                _scroll += Math.Min(bottom - _viewport.Bottom, Math.Max(0, top - _viewport.Y));
            }
            _scene = null;
        }
    }

    private void CloseActions(bool returnFocus)
    {
        if (_actions is not { } actions)
        {
            return;
        }
        _actions = null;
        _openInExpanded = false;
        if (returnFocus)
        {
            // Return focus to the button that opened the list.
            _focus = new(PanelTargetKind.More, actions.Section, actions.Host);
        }
        _scene = null;
    }

    private void BeginConfirm(PanelConfirm confirm)
    {
        _confirm = confirm;
        _actions = null;
        // Focus starts on Cancel, so Enter or Space right after the click never confirms.
        _focus = new(PanelTargetKind.ConfirmCancel, confirm.Section, confirm.Host?.Id);
        _scene = null;

        // The strip is taller than the row it replaces and may be below the fold (a recent row
        // reached by scrolling), so scroll the whole strip into view.
        var buttons = Scene.Targets.Where(target => IsConfirmTarget(target.Kind)).ToList();
        if (buttons.Count > 0)
        {
            var bottom = buttons.Max(target => target.Bounds.Bottom) + 14;
            var top = bottom - ConfirmHeight;
            if (bottom > _viewport.Bottom)
            {
                _scroll += bottom - _viewport.Bottom;
            }
            if (top < _viewport.Y)
            {
                _scroll -= _viewport.Y - top;
            }
            _scene = null;
        }
    }

    private void CancelConfirm()
    {
        if (_confirm is not { } confirm)
        {
            return;
        }
        _confirm = null;
        // Return focus to the control that opened the confirmation.
        _focus = confirm.Kind switch
        {
            PanelConfirmKind.ClearRecent => new(PanelTargetKind.ClearRecent, confirm.Section, null),
            _ => new(PanelTargetKind.Start, confirm.Section, confirm.Host?.Id)
        };
        _scene = null;
    }

    private void CloseSettings()
    {
        _settings = null;
        _scroll = _listScroll;
        _focus = new(PanelTargetKind.Settings, PanelSection.None, null);
        RefreshHover();
    }

    /// <summary>Hit tests the pointer again after the layout changed under it.</summary>
    private void RefreshHover()
    {
        _scene = null;
        if (_pointer is { } pointer)
        {
            _hover = HitTest(pointer.X, pointer.Y)?.Key;
            _scene = null;
        }
    }

    private PanelResponse MoveFocus(List<PanelHitTarget> order, int step, bool wrap)
    {
        if (order.Count == 0)
        {
            return PanelResponse.None;
        }
        var index = order.FindIndex(target => target.Key == _focus);
        if (index < 0 && _focus is { Host: not null } current)
        {
            // Up and Down from an inline button continue from that button's row.
            index = order.FindIndex(target => target.Kind == PanelTargetKind.Row && target.Key.Host == current.Host
                && target.Key.Section == current.Section);
        }
        var next = index < 0
            ? (step > 0 ? 0 : order.Count - 1)
            : wrap ? (index + step + order.Count) % order.Count : Math.Clamp(index + step, 0, order.Count - 1);
        return FocusAt(order, next);
    }

    private PanelResponse FocusAt(List<PanelHitTarget> order, int index)
    {
        if (index < 0 || index >= order.Count)
        {
            return PanelResponse.None;
        }
        var target = order[index];
        _focus = target.Key;
        _focusVisible = true;
        if (IsChrome(target.Kind))
        {
            // Header and footer buttons never scroll.
        }
        else if (target.Bounds.Y < _viewport.Y)
        {
            _scroll -= _viewport.Y - target.Bounds.Y;
        }
        else if (target.Bounds.Bottom > _viewport.Bottom)
        {
            _scroll += target.Bounds.Bottom - _viewport.Bottom;
        }
        _scene = null;
        return PanelResponse.Changed;
    }

    private PanelResponse SetHover(PanelTargetKey? key)
    {
        if (_hover == key)
        {
            return PanelResponse.None;
        }
        _hover = key;
        _focusVisible = false;
        _scene = null;
        return PanelResponse.Changed;
    }

    private PanelAction? Activate(PanelHitTarget target)
    {
        var host = target.Host;
        var recent = target.Key.Section == PanelSection.Recent;
        if (_confirm is not null && !IsConfirmTarget(target.Kind))
        {
            // Acting on anything else abandons the pending confirmation.
            _confirm = null;
            _scene = null;
        }
        if (_actions is not null && target.Kind is not (PanelTargetKind.More or PanelTargetKind.Row) && !IsActionItem(target.Kind))
        {
            // Likewise, acting on anything but the list (or the button that toggles it) closes it.
            CloseActions(returnFocus: false);
        }
        switch (target.Kind)
        {
            // Clicking the row itself does nothing, whatever the AppHost's state, so it behaves
            // the same for running and stopped AppHosts; its buttons carry the actions. The row
            // stays a target so hover and keyboard focus reveal those buttons.
            case PanelTargetKind.Row:
                return null;
            case PanelTargetKind.Dashboard:
                return new(PanelActionKind.OpenDashboard, host, target.Bounds) { Recent = recent };
            case PanelTargetKind.More:
                ToggleActions(target.Key);
                return null;
            case PanelTargetKind.Stop:
                return new(PanelActionKind.Stop, host, target.Bounds) { Recent = recent };
            case PanelTargetKind.Start:
                return new(PanelActionKind.Start, host, target.Bounds) { Recent = recent };
            case PanelTargetKind.OpenInMenu:
                // Show or hide the applications under "Open in", keeping the list open.
                _openInExpanded = !_openInExpanded;
                _scene = null;
                RevealActions(target.Key);
                return null;
            case PanelTargetKind.OpenIn:
                CloseActions(returnFocus: true);
                return new(PanelActionKind.OpenIn, host, target.Bounds)
                {
                    Recent = recent,
                    Application = target.Key.Item < _openInApplications.Count ? _openInApplications[target.Key.Item] : null
                };
            case PanelTargetKind.ShowInFolder:
                CloseActions(returnFocus: true);
                return new(PanelActionKind.ShowInFolder, host, target.Bounds) { Recent = recent };
            case PanelTargetKind.CopyPath:
                CloseActions(returnFocus: true);
                return new(PanelActionKind.CopyPath, host, target.Bounds) { Recent = recent };
            case PanelTargetKind.TogglePin:
                CloseActions(returnFocus: true);
                return new(PanelActionKind.TogglePin, host, target.Bounds) { Recent = recent };
            case PanelTargetKind.RemoveRecent:
                CloseActions(returnFocus: false);
                return new(PanelActionKind.RemoveRecent, host, target.Bounds) { Recent = true };
            case PanelTargetKind.ToggleRecent:
                _recentExpanded = !IsRecentExpanded;
                _scene = null;
                return new(PanelActionKind.ToggleRecent, Anchor: target.Bounds);
            case PanelTargetKind.ClearRecent:
                BeginConfirm(new(PanelConfirmKind.ClearRecent, PanelSection.Recent, null));
                return null;
            case PanelTargetKind.ConfirmCancel:
                CancelConfirm();
                return null;
            case PanelTargetKind.ConfirmAccept when _confirm is { } confirmed:
                _confirm = null;
                _scene = null;
                return confirmed.Kind switch
                {
                    PanelConfirmKind.ClearRecent => new(PanelActionKind.ClearRecent, Anchor: target.Bounds),
                    _ => new(PanelActionKind.RemoveRecent, host, target.Bounds) { Recent = true }
                };
            case PanelTargetKind.Back:
            case PanelTargetKind.Settings when _settings is not null:
                CloseSettings();
                return null;
            case PanelTargetKind.LaunchAtSignIn:
                return new(PanelActionKind.SetLaunchAtSignIn, Anchor: target.Bounds) { Enabled = _settings?.Startup?.Enabled != true };
            case PanelTargetKind.Documentation:
                return new(PanelActionKind.Documentation, Anchor: target.Bounds);
            case PanelTargetKind.Settings:
                return new(PanelActionKind.Settings, Anchor: target.Bounds);
            case PanelTargetKind.Quit:
                return new(PanelActionKind.Quit, Anchor: target.Bounds);
            default:
                return null;
        }
    }

    private TrayPanelScene Build()
    {
        var palette = _palette;
        var state = _state;
        var live = state.Discovery == DiscoveryState.Live;
        var commands = new List<PanelCommand>();
        var targets = new List<PanelHitTarget>();
        // AddTarget and AddLabel append here in drawing order, which is also reading order.
        var accessible = new List<PanelAccessibleElement>();
        _accessible = accessible;
        _guides = [];

        // Lay out the list first: its height decides the panel height and the scroll range.
        var items = new List<ListItem>();
        var y = ListTopPadding;
        var running = state.AppHosts.Where(host => host.Id.AppHostPid != 0).ToList();
        var pinned = state.AppHosts.Where(host => host.Id.AppHostPid == 0).ToList();
        if (_settings is { } settings)
        {
            AddSettingsItems(settings);
        }
        else
        {
            RevalidateConfirm();
            RevalidateActions();
            if (state.AppHosts.Count == 0)
            {
                Add(ListItemKind.Empty, EmptyStateHeight, PanelSection.None, null);
            }
            AddSection(PanelSection.Running, running, expanded: true);
            AddSection(PanelSection.Pinned, pinned, expanded: true);
            AddSection(PanelSection.Recent, state.RecentAppHosts, IsRecentExpanded);
        }
        _contentHeight = y + ListBottomPadding;

        void Add(ListItemKind kind, double height, PanelSection section, AppHostMenuItem? host, PanelTargetKind target = default)
        {
            items.Add(new(kind, y, height, section, host, target));
            y += height;
        }

        void AddSection(PanelSection section, IReadOnlyList<AppHostMenuItem> hosts, bool expanded)
        {
            if (hosts.Count == 0)
            {
                return;
            }
            // An inline confirmation takes the place of the item it is about, so the list
            // keeps its order and the strip sits exactly where the user clicked.
            if (section == PanelSection.Recent && _confirm is { Kind: PanelConfirmKind.ClearRecent })
            {
                Add(ListItemKind.Confirm, ConfirmHeight, section, null);
            }
            else
            {
                Add(ListItemKind.SectionHeader, SectionHeaderHeight, section, null);
            }
            if (!expanded)
            {
                return;
            }
            foreach (var host in hosts)
            {
                var confirming = _confirm is { Kind: PanelConfirmKind.RemoveMissing } confirm
                    && confirm.Section == section && confirm.Host?.Id == host.Id;
                Add(confirming ? ListItemKind.Confirm : ListItemKind.Row, confirming ? ConfirmHeight : RowHeight, section, host);
                if (!confirming && _actions == (section, host.Id))
                {
                    var count = GetRowActions(host, section).Count;
                    Add(ListItemKind.Actions, count * ActionItemHeight + 2 * ActionListPadding + ActionListGap, section, host);
                }
            }
        }

        void AddSettingsItems(PanelSettings settings)
        {
            Add(ListItemKind.SectionHeader, SectionHeaderHeight, PanelSection.None, null, PanelTargetKind.LaunchAtSignIn);
            Add(ListItemKind.SettingsToggle, SettingsRowHeight, PanelSection.None, null, PanelTargetKind.LaunchAtSignIn);
            Add(ListItemKind.SectionHeader, SectionHeaderHeight, PanelSection.None, null, PanelTargetKind.Documentation);
            Add(ListItemKind.About, AboutHeight, PanelSection.None, null);
        }

        var notice = string.IsNullOrWhiteSpace(state.Notice) ? null : state.Notice.Trim();
        var noticeSingleLine = notice is not null && notice.Length * AverageBodyCharacterWidth <= NoticeTextWidth;
        // The banner sits 8 px below the header bar and 8 px above the list.
        var noticeHeight = notice is null ? 0 : (noticeSingleLine ? 34 : 50) + 16;
        var chrome = HeaderHeight + noticeHeight + FooterHeight;
        var height = Math.Min(_maxHeight, chrome + _contentHeight);
        _viewport = new(0, HeaderHeight + noticeHeight, Width, Math.Max(0, height - chrome));
        _scroll = Math.Clamp(_scroll, 0, Math.Max(0, _contentHeight - _viewport.Height));
        var listOrigin = _viewport.Y - _scroll;

        // Panel background and footer strip. The window itself is transparent (macOS) or
        // rounded by DWM or a region (Windows), so the panel draws its own rounded body.
        var panel = new PanelRect(0, 0, Width, height);
        var footer = new PanelRect(0, height - FooterHeight, Width, FooterHeight);
        commands.Add(new FillPathCommand(PanelPath.RoundedRect(panel, CornerRadius), palette.Background));
        commands.Add(new FillPathCommand(PanelPath.RoundedRect(footer, 0, 0, CornerRadius, CornerRadius), palette.Gutter));
        commands.Add(new FillPathCommand(PanelPath.Rectangle(new(0, footer.Y, Width, 1)), palette.Separator));
        // The header is a bar like the footer: the same height and color, with a separator that
        // mirrors the footer's on its bottom edge.
        commands.Add(new FillPathCommand(PanelPath.RoundedRect(new(0, 0, Width, HeaderHeight), CornerRadius, CornerRadius, 0, 0), palette.Gutter));
        commands.Add(new FillPathCommand(PanelPath.Rectangle(new(0, HeaderHeight - 1, Width, 1)), palette.Separator));

        if (_settings is null)
        {
            AddHeader(commands, state, palette);
        }
        else
        {
            AddSettingsHeader(commands, targets, palette);
        }
        if (notice is not null)
        {
            AddNotice(commands, notice, noticeSingleLine, noticeHeight - 16, palette);
        }
        if (_scroll > 0 && notice is not null)
        {
            // Mark the edge only when content is scrolled under the banner; under the header
            // bar, its own separator already does.
            commands.Add(new FillPathCommand(PanelPath.Rectangle(new(0, _viewport.Y, Width, 1)), palette.Separator));
        }

        var duplicateNames = running.GroupBy(host => host.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        commands.Add(new PushClipCommand(_viewport));
        var hidden = new List<PanelCommand>();
        foreach (var item in items)
        {
            var top = listOrigin + item.Y;
            // Items scrolled out of view are not drawn but keep their targets (with empty hit
            // bounds), so keyboard navigation can reach them and scroll them into view.
            var sink = top + item.Height <= _viewport.Y || top >= _viewport.Bottom ? hidden : commands;
            switch (item.Kind)
            {
                case ListItemKind.Empty:
                    AddEmptyState(sink, state.Discovery, top, palette);
                    break;
                case ListItemKind.SectionHeader when _settings is not null:
                    var general = item.Target == PanelTargetKind.LaunchAtSignIn;
                    var headerBounds = new PanelRect(20, top + 4, 200, 24);
                    sink.Add(new TextCommand(headerBounds, general ? "GENERAL" : "ABOUT", PanelFontRole.Section, palette.TextTertiary));
                    // Screen readers get the title case: some spell out all-caps words letter by letter.
                    AddLabel(general ? "settings.general" : "settings.about", general ? "General" : "About", headerBounds, chrome: false);
                    break;
                case ListItemKind.SectionHeader:
                    AddSectionHeader(sink, targets, item.Section, top, state, palette);
                    break;
                case ListItemKind.Row:
                    AddRow(sink, targets, item.Host!, item.Section, top, live,
                        item.Section == PanelSection.Running && duplicateNames.Contains(item.Host!.DisplayName), palette);
                    break;
                case ListItemKind.Confirm:
                    AddConfirm(sink, targets, _confirm!, item.Host, top, palette);
                    break;
                case ListItemKind.Actions:
                    AddActions(sink, targets, item.Host!, item.Section, top, item.Height, palette);
                    break;
                case ListItemKind.SettingsToggle:
                    AddSettingsToggle(sink, targets, item.Target, _settings!, top, palette);
                    break;
                case ListItemKind.About:
                    AddAbout(sink, _settings!, top, palette);
                    break;
            }
        }
        commands.Add(new PopClipCommand());

        if (_contentHeight > _viewport.Height && _viewport.Height > 0)
        {
            var thumb = Math.Max(24, _viewport.Height * _viewport.Height / _contentHeight);
            var travel = _viewport.Height - thumb - 8;
            var thumbY = _viewport.Y + 4 + travel * _scroll / (_contentHeight - _viewport.Height);
            commands.Add(new FillPathCommand(PanelPath.RoundedRect(new(Width - 7, thumbY, 4, thumb), 2), palette.TextTertiary.WithAlpha(0.4)));
        }

        AddFooter(commands, targets, footer, palette);
        commands.Add(new StrokePathCommand(PanelPath.RoundedRect(panel.Inset(0.5, 0.5), CornerRadius - 0.5), palette.Border, 1));

        // Forget hover and focus on targets that disappeared, such as a row that was removed.
        if (_focus is { } focus && !targets.Any(target => target.Key == focus))
        {
            _focus = null;
            _focusVisible = false;
        }
        if (_hover is { } hover && !targets.Any(target => target.Key == hover))
        {
            _hover = null;
        }
        if (_focusVisible && targets.FirstOrDefault(target => target.Key == _focus) is { } focused)
        {
            var clip = IsChrome(focused.Kind) ? panel : _viewport;
            commands.Add(new PushClipCommand(clip));
            commands.Add(new StrokePathCommand(PanelPath.RoundedRect(focused.Bounds.Inset(-1, -1), 7), palette.Focus, 2));
            commands.Add(new PopClipCommand());
        }

        AddGuides(items, listOrigin, height);
        return new(Width, height, commands, targets, accessible) { Guides = _guides };
    }

    private List<PanelGuide> _guides = [];

    /// <summary>
    /// Emits the alignment grid for the scene just laid out: the columns over the whole panel,
    /// then the centers and glyph boxes of the header, each visible list item and the footer.
    /// </summary>
    private void AddGuides(List<ListItem> items, double listOrigin, double height)
    {
        var all = new PanelRect(0, 0, Width, height);
        foreach (var (x, label) in new[]
        {
            (RowInset, "row/card edge"), (EdgeMargin, "edge"), (LeadingCenter, "leading center"), (TextLeft, "text"), (NestedTextLeft, "nested text"),
            (TrailingCenter, "trailing center"), (Width - EdgeMargin, "edge"), (RowContentRight, "button edge"),
            (Width - RowInset, "row/card edge")
        })
        {
            _guides.Add(new(PanelGuideKind.Column, x, label, all));
        }

        Line(HeaderHeight / 2, "header", new(0, 0, Width, HeaderHeight));
        if (_viewport.Y > HeaderHeight)
        {
            // The notice banner: its icon and its first (or only) line share one center.
            Line(HeaderHeight + 8 + 17, "notice", new(0, HeaderHeight, Width, _viewport.Y - HeaderHeight));
            Box(new(LeadingCenter - 8, HeaderHeight + 8 + 9, 16, 16), "notice icon");
        }
        _guides.Add(new(PanelGuideKind.Baseline, HeaderBaseline, "header baseline", new(TextLeft, 0, Width - TextLeft, HeaderHeight)));
        if (_settings is null)
        {
            Box(new(EdgeMargin, HeaderHeight / 2 - 10, 20, 20), "logo");
        }
        else
        {
            Box(new(LeadingCenter - 8, HeaderHeight / 2 - 8, 16, 16), "back");
        }
        var footerTop = height - FooterHeight;
        Line(footerTop + FooterHeight / 2, "footer", new(0, footerTop, Width, FooterHeight));
        for (var i = 0; i < 3; i++)
        {
            var center = TrailingCenter - i * (ButtonSize + ButtonGap);
            Box(new(center - 8, footerTop + FooterHeight / 2 - 8, 16, 16), "footer icon");
        }

        foreach (var item in items)
        {
            var top = listOrigin + item.Y;
            if (top + item.Height <= _viewport.Y || top >= _viewport.Bottom)
            {
                continue;
            }
            var span = new PanelRect(0, top, Width, item.Height);
            switch (item.Kind)
            {
                case ListItemKind.Row:
                    Line(top + RowHeight / 2, "row", span);
                    Line(top + 17, "name", span);
                    Line(top + 35, "detail", span);
                    Box(new(EdgeMargin, top + RowHeight / 2 - 10, 20, 20), "status");
                    // The buttons' icons, drawn while the row is hovered or focused.
                    for (var i = 0; i < (item.Host!.Id.AppHostPid != 0 ? 3 : 2); i++)
                    {
                        var center = TrailingCenter - i * (ButtonSize + ButtonGap);
                        Box(new(center - 8, top + RowHeight / 2 - 8, 16, 16), "row button icon");
                    }
                    break;
                case ListItemKind.SectionHeader when item.Section == PanelSection.Recent && _settings is null:
                    Line(top + 16, "section", span);
                    Box(new(TrailingCenter - 8, top + 8, 16, 16), "recent chevron");
                    break;
                case ListItemKind.SectionHeader:
                    Line(top + 16, "section", span);
                    break;
                case ListItemKind.Actions:
                    var actions = GetRowActions(item.Host!, item.Section);
                    for (var i = 0; i < actions.Count; i++)
                    {
                        var itemTop = top + ActionListPadding + i * ActionItemHeight;
                        var middle = itemTop + ActionItemHeight / 2;
                        Line(middle, "action", new(0, itemTop, Width, ActionItemHeight));
                        // The applications nested under "Open in" draw their icon on the text column,
                        // as AddActions does; every other item's icon is on the leading column.
                        Box(actions[i].Kind == PanelTargetKind.OpenIn
                            ? new(TextLeft, middle - 8, 16, 16)
                            : new(LeadingCenter - 8, middle - 8, 16, 16),
                            actions[i].Kind == PanelTargetKind.OpenIn ? "open-in app icon" : "action icon");
                    }
                    break;
                case ListItemKind.SettingsToggle:
                    Line(top + 18, "setting", span);
                    break;
                case ListItemKind.Confirm:
                    // The question, the detail and the buttons, in the card that starts at top + 4.
                    Line(top + 4 + 18, "confirm question", span);
                    Line(top + 4 + 36, "confirm detail", span);
                    Line(top + ConfirmHeight - 4 - 10 - ButtonSize / 2, "confirm buttons", span);
                    break;
                case ListItemKind.About:
                    Line(top + 14, "about", span);
                    for (var line = 0; line < 3; line++)
                    {
                        Line(top + 32 + line * 18, "about", span);
                    }
                    break;
                case ListItemKind.Empty:
                    Line(top + 72, "empty title", span);
                    Box(new(Width / 2 - 16, top + 20, 32, 32), "empty art");
                    break;
            }
        }

        void Line(double y, string label, PanelRect bounds) => _guides.Add(new(PanelGuideKind.Center, y, label, bounds));
        void Box(PanelRect bounds, string label) => _guides.Add(new(PanelGuideKind.Box, bounds.CenterY, label, bounds));
    }

    private const double NoticeTextWidth = Width - EdgeMargin - TextLeft;

    // The header's one line: the logo over the rows' status glyphs, "Aspire" where the AppHost
    // names start, then the connection dot and the status. "Aspire" is a fixed string, so its
    // slot is sized for 15 px semibold system text (about 49 DIPs in SF and Segoe UI Variable)
    // rather than measured, which keeps the layout identical on both platforms.
    private const double HeaderTitleWidth = 56;

    /// <summary>
    /// The header's baseline: the title's capitals are centered on the bar like the logo, and the
    /// smaller status text shares the title's baseline, as text of different sizes on one line should.
    /// </summary>
    internal static double HeaderBaseline => HeaderHeight / 2 + PanelFonts.CapHeight(PanelFontRole.Title) / 2;

    private void AddHeader(List<PanelCommand> commands, TrayViewState state, TrayPanelPalette palette)
    {
        var middle = HeaderHeight / 2;
        commands.Add(new LogoCommand(new(EdgeMargin, middle - 10, 20, 20)));
        var title = new PanelRect(TextLeft, middle - 10, HeaderTitleWidth, 20);
        commands.Add(new TextCommand(title, "Aspire", PanelFontRole.Title, palette.TextPrimary));
        AddLabel("header.title", "Aspire", title, chrome: true);
        var (status, dot) = GetHeaderStatus(state, palette);
        // The dot is centered on the status text's capitals and digits ("3 running"), which sit
        // on the shared baseline.
        var statusMiddle = HeaderBaseline - PanelFonts.CapHeight(PanelFontRole.Body) / 2;
        commands.Add(new FillPathCommand(PanelPath.Circle(title.Right + 4, statusMiddle, 4), dot));
        var statusLeft = title.Right + 14;
        var statusBounds = new PanelRect(statusLeft, middle - 8, Width - EdgeMargin - statusLeft, 16);
        commands.Add(new TextCommand(statusBounds, status, PanelFontRole.Body, palette.TextSecondary, ExplicitBaseline: HeaderBaseline));
        AddLabel("header.status", status, statusBounds, chrome: true);
    }

    private void AddNotice(List<PanelCommand> commands, string notice, bool singleLine, double height, TrayPanelPalette palette)
    {
        var banner = new PanelRect(RowInset, HeaderHeight + 8, Width - 2 * RowInset, height);
        AddLabel("notice", notice, banner, chrome: true);
        commands.Add(new FillPathCommand(PanelPath.RoundedRect(banner, 6), palette.Banner));
        if (palette.IsHighContrast)
        {
            commands.Add(new StrokePathCommand(PanelPath.RoundedRect(banner.Inset(0.5, 0.5), 5.5), palette.Border, 1));
        }
        // The icon sits on the leading column and the text on the text column, like a row's.
        TrayPanelGlyphs.AddIcon(commands, PanelIcon.Alert, new(LeadingCenter - 8, banner.Y + 9, 16, 16), palette.BannerText);
        var text = new PanelRect(TextLeft, banner.Y + 8, NoticeTextWidth, height - 16);
        commands.Add(singleLine
            ? new TextCommand(text, notice, PanelFontRole.Body, palette.BannerText)
            : new TextCommand(text, notice, PanelFontRole.Body, palette.BannerText, MaxLines: 2));
    }

    private void AddEmptyState(List<PanelCommand> commands, DiscoveryState discovery, double top, TrayPanelPalette palette)
    {
        var (title, hint) = discovery switch
        {
            DiscoveryState.Live => ("No AppHosts running", "Run “aspire run” in a project, or pin one to keep it here."),
            DiscoveryState.Connecting => ("Looking for AppHosts…", "Connecting to the Aspire CLI."),
            _ => ("AppHosts unavailable", "Running AppHosts will appear here once discovery is back.")
        };
        var art = new PanelRect(Width / 2 - 16, top + 20, 32, 32);
        if (discovery == DiscoveryState.Connecting)
        {
            TrayPanelGlyphs.AddStatus(commands, PanelStatusGlyph.Busy, art.Inset(2, 2), palette);
        }
        else
        {
            TrayPanelGlyphs.AddIcon(commands, PanelIcon.Layers, art, palette.TextTertiary);
        }
        var titleBounds = new PanelRect(20, top + 62, Width - 40, 20);
        var hintBounds = new PanelRect(44, top + 84, Width - 88, 34);
        commands.Add(new TextCommand(titleBounds, title, PanelFontRole.Name, palette.TextPrimary, PanelTextAlignment.Center));
        commands.Add(new TextCommand(hintBounds, hint, PanelFontRole.Body, palette.TextSecondary,
            PanelTextAlignment.Center, MaxLines: 2));
        AddLabel("empty.title", title, titleBounds, chrome: false);
        AddLabel("empty.hint", hint, hintBounds, chrome: false);
    }

    private void AddSectionHeader(List<PanelCommand> commands, List<PanelHitTarget> targets, PanelSection section, double top,
        TrayViewState state, TrayPanelPalette palette)
    {
        var label = section switch
        {
            PanelSection.Running => "RUNNING",
            PanelSection.Pinned => "PINNED",
            _ => $"RECENT · {state.RecentAppHosts.Count}"
        };
        var labelBounds = new PanelRect(20, top + 4, 200, 24);
        if (section != PanelSection.Recent)
        {
            commands.Add(new TextCommand(labelBounds, label, PanelFontRole.Section, palette.TextTertiary));
            AddLabel(section == PanelSection.Running ? "section.running" : "section.pinned",
                section == PanelSection.Running ? "Running" : "Pinned", labelBounds, chrome: false);
            return;
        }

        var expanded = IsRecentExpanded;
        var toggle = new PanelRect(RowInset, top + 4, Width - 2 * RowInset, 24);
        var toggleKey = new PanelTargetKey(PanelTargetKind.ToggleRecent, section, null);
        AddInteractionFill(commands, toggleKey, toggle, palette, includeFocus: false);
        commands.Add(new TextCommand(labelBounds, label, PanelFontRole.Section, palette.TextTertiary));
        TrayPanelGlyphs.AddIcon(commands, expanded ? PanelIcon.ChevronDown : PanelIcon.ChevronRight,
            new(RowContentRight - 22, top + 8, 16, 16), palette.TextTertiary);
        var count = state.RecentAppHosts.Count;
        AddTarget(targets, toggleKey, toggle, expanded ? $"Hide {count} recent AppHosts" : $"Show {count} recent AppHosts", true, null);

        if (state.CanClearRecent)
        {
            var clear = new PanelRect(RowContentRight - 30 - 52, top + 6, 52, 20);
            var clearKey = new PanelTargetKey(PanelTargetKind.ClearRecent, section, null);
            AddInteractionFill(commands, clearKey, clear, palette, includeFocus: false);
            commands.Add(new TextCommand(clear, "Clear", PanelFontRole.Body, palette.Accent, PanelTextAlignment.Center));
            AddTarget(targets, clearKey, clear, "Clear recent AppHosts", true, null);
        }
    }

    private void AddRow(List<PanelCommand> commands, List<PanelHitTarget> targets, AppHostMenuItem host, PanelSection section,
        double top, bool live, bool showPid, TrayPanelPalette palette)
    {
        var rowKey = new PanelTargetKey(PanelTargetKind.Row, section, host.Id);
        var row = new PanelRect(RowInset, top + 2, Width - 2 * RowInset, RowHeight - 4);
        // A row whose action list is open keeps its buttons visible, so its "More" button can be
        // seen as the list's owner and clicked again to close it.
        var expanded = _actions == (section, host.Id);
        var active = expanded || IsWithinRow(_hover, rowKey) || IsWithinRow(_pressed, rowKey) || (_focusVisible && IsWithinRow(_focus, rowKey));
        // No pressed shade for the row itself: clicking it does nothing.
        if (active)
        {
            commands.Add(new FillPathCommand(PanelPath.RoundedRect(row, 6), palette.Hover));
        }

        TrayPanelGlyphs.AddStatus(commands, GetStatusGlyph(host, live), new(20, top + 16, 20, 20), palette);

        var buttons = host.Id.AppHostPid != 0
            ? new[] { PanelTargetKind.Dashboard, PanelTargetKind.Stop, PanelTargetKind.More }
            : [PanelTargetKind.Start, PanelTargetKind.More];
        var textRight = RowContentRight - buttons.Length * ButtonSize - (buttons.Length - 1) * ButtonGap - 8;
        var (detail, isError) = GetRowDetail(host, live, showPid);
        commands.Add(new TextCommand(new(TextLeft, top + 8, textRight - TextLeft, 18), host.DisplayName, PanelFontRole.Name, palette.TextPrimary));
        commands.Add(new TextCommand(new(TextLeft, top + 27, textRight - TextLeft, 16), detail, PanelFontRole.Body,
            isError ? palette.Danger : palette.TextSecondary, Truncation: isError ? PanelTruncation.End : PanelTruncation.Middle));

        AddTarget(targets, rowKey, row, GetRowAccessibleName(host, detail), true, host);
        var x = RowContentRight - buttons.Length * ButtonSize - (buttons.Length - 1) * ButtonGap;
        foreach (var kind in buttons)
        {
            var bounds = new PanelRect(x, top + (RowHeight - ButtonSize) / 2, ButtonSize, ButtonSize);
            x += ButtonSize + ButtonGap;
            var key = rowKey with { Kind = kind };
            var (icon, name, tooltip, enabled) = kind switch
            {
                PanelTargetKind.Dashboard => (PanelIcon.ExternalLink, $"Open dashboard for {host.DisplayName}", "Open dashboard", host.CanOpenDashboard),
                PanelTargetKind.Stop => (PanelIcon.Stop, $"Stop {host.DisplayName}", "Stop", host.CanStop),
                PanelTargetKind.Start => (PanelIcon.Play, $"Start {host.DisplayName}", "Start", host.CanStart),
                _ => (PanelIcon.More, expanded ? $"Hide actions for {host.DisplayName}" : $"More actions for {host.DisplayName}",
                    expanded ? "Hide actions" : "More actions", true)
            };
            // Buttons always keep their space so the text never reflows on hover, and they
            // stay hit-testable: the pointer is inside the row, so they are visible, before any click.
            AddTarget(targets, key, bounds, name, enabled, host, tooltip: tooltip);
            if (!active)
            {
                continue;
            }
            if (kind == PanelTargetKind.More && expanded && _hover != key && _pressed != key)
            {
                // Mark the button as selected while its list is open, like the gear for Settings.
                commands.Add(new FillPathCommand(PanelPath.RoundedRect(bounds, 6), palette.Pressed));
            }
            if (enabled)
            {
                AddInteractionFill(commands, key, bounds, palette, includeFocus: false);
            }
            var color = !enabled ? palette.TextTertiary.WithAlpha(0.45)
                : _hover == key || _pressed == key ? palette.TextPrimary
                : palette.TextSecondary;
            TrayPanelGlyphs.AddIcon(commands, icon, bounds.Inset(6, 6), color);
        }
    }

    /// <summary>Closes an action list whose row went away, such as an AppHost that stopped.</summary>
    private void RevalidateActions()
    {
        if (_actions is not { } actions)
        {
            return;
        }
        var hosts = actions.Section switch
        {
            PanelSection.Running => _state.AppHosts.Where(host => host.Id.AppHostPid != 0),
            PanelSection.Pinned => _state.AppHosts.Where(host => host.Id.AppHostPid == 0),
            _ => IsRecentExpanded ? _state.RecentAppHosts : []
        };
        if (!hosts.Any(host => host.Id == actions.Host))
        {
            CloseActions(returnFocus: false);
        }
    }

    /// <summary>
    /// The items of a row's action list, in display order. "Open in" is one item; while it is
    /// expanded, the installed applications follow it as nested items.
    /// </summary>
    private List<(PanelTargetKind Kind, int Item, PanelIcon Icon, string Label, string Name)> GetRowActions(AppHostMenuItem host, PanelSection section)
    {
        var name = host.DisplayName;
        var actions = new List<(PanelTargetKind, int, PanelIcon, string, string)>();
        if (_openInApplications.Count > 0)
        {
            actions.Add((PanelTargetKind.OpenInMenu, 0, PanelIcon.AppWindow, "Open in",
                _openInExpanded ? $"Hide applications to open {name} in" : $"Open {name} in…"));
            for (var i = 0; _openInExpanded && i < _openInApplications.Count; i++)
            {
                var application = _openInApplications[i];
                actions.Add((PanelTargetKind.OpenIn, i, PanelIcon.AppWindow, application, $"Open {name} in {application}"));
            }
        }
        actions.Add((PanelTargetKind.ShowInFolder, 0, PanelIcon.Folder, $"Show in {_fileManagerName}", $"Show {name} in {_fileManagerName}"));
        actions.Add((PanelTargetKind.CopyPath, 0, PanelIcon.Copy, "Copy path", $"Copy the folder path of {name}"));
        actions.Add(host.IsPinned
            ? (PanelTargetKind.TogglePin, 0, PanelIcon.Unpin, "Unpin", $"Unpin {name}")
            : (PanelTargetKind.TogglePin, 0, PanelIcon.Pin, "Pin", $"Pin {name}"));
        if (section == PanelSection.Recent)
        {
            actions.Add((PanelTargetKind.RemoveRecent, 0, PanelIcon.Remove, "Remove from recent", $"Remove {name} from recent AppHosts"));
        }
        return actions;
    }

    /// <summary>
    /// Draws a row's action list: a card under the row with one item per action, each an icon and
    /// a label. It looks the same on both platforms, unlike the native menus it replaces, and its
    /// items are ordinary targets, so the keyboard and screen readers reach them like any button.
    /// </summary>
    private void AddActions(List<PanelCommand> commands, List<PanelHitTarget> targets, AppHostMenuItem host, PanelSection section,
        double top, double height, TrayPanelPalette palette)
    {
        var card = new PanelRect(RowInset, top, Width - 2 * RowInset, height - ActionListGap);
        commands.Add(new FillPathCommand(PanelPath.RoundedRect(card, 8), palette.Gutter));
        commands.Add(new StrokePathCommand(PanelPath.RoundedRect(card.Inset(0.5, 0.5), 7.5), palette.Border, 1));
        var y = card.Y + ActionListPadding;
        foreach (var (kind, index, icon, label, name) in GetRowActions(host, section))
        {
            var item = new PanelRect(card.X + ActionListPadding, y, card.Width - 2 * ActionListPadding, ActionItemHeight);
            var key = new PanelTargetKey(kind, section, host.Id, index);
            AddInteractionFill(commands, key, item, palette, includeFocus: false);
            var iconColor = _hover == key ? palette.TextPrimary : palette.TextSecondary;
            if (kind == PanelTargetKind.OpenIn)
            {
                // Applications are nested under "Open in": their own icon where the labels above
                // start, and the name after it.
                var fallback = new List<PanelCommand>();
                TrayPanelGlyphs.AddIcon(fallback, PanelIcon.AppWindow, new(TextLeft, item.Y + 7, 16, 16), iconColor);
                commands.Add(new AppIconCommand(new(TextLeft, item.Y + 7, 16, 16), _openInApplications[index], fallback));
                commands.Add(new TextCommand(new(NestedTextLeft, item.Y, item.Right - 8 - NestedTextLeft, item.Height), label,
                    PanelFontRole.Body, palette.TextPrimary));
            }
            else
            {
                // Line the icons up with the status glyphs and the labels with the AppHost names above.
                TrayPanelGlyphs.AddIcon(commands, icon, new(22, item.Y + 7, 16, 16), iconColor);
                var labelRight = item.Right - 8;
                if (kind == PanelTargetKind.OpenInMenu)
                {
                    // A chevron on the right tells that the item opens the applications under it.
                    labelRight -= 24;
                    TrayPanelGlyphs.AddIcon(commands, _openInExpanded ? PanelIcon.ChevronDown : PanelIcon.ChevronRight,
                        new(TrailingCenter - 8, item.Y + 7, 16, 16), palette.TextTertiary);
                }
                commands.Add(new TextCommand(new(TextLeft, item.Y, labelRight - TextLeft, item.Height), label, PanelFontRole.Body, palette.TextPrimary));
            }
            AddTarget(targets, key, item, name, true, host);
            y += ActionItemHeight;
        }
    }

    /// <summary>
    /// Drops a confirmation whose subject went away (the AppHost stopped, or the recent list was
    /// cleared elsewhere) and picks up the subject's latest state for the strip's text.
    /// </summary>
    private void RevalidateConfirm()
    {
        if (_confirm is not { } confirm)
        {
            return;
        }
        var current = confirm.Kind switch
        {
            PanelConfirmKind.RemoveMissing => _state.RecentAppHosts.FirstOrDefault(host => host.Id == confirm.Host!.Id),
            _ => null
        };
        var valid = confirm.Kind == PanelConfirmKind.ClearRecent ? _state.CanClearRecent : current is not null;
        if (!valid)
        {
            CancelConfirm();
        }
        else if (current is not null)
        {
            _confirm = confirm with { Host = current };
        }
    }

    /// <summary>
    /// Draws an inline confirmation strip: a question, one line of detail, and a row of controls
    /// (Cancel, then the destructive button).
    /// </summary>
    private void AddConfirm(List<PanelCommand> commands, List<PanelHitTarget> targets, PanelConfirm confirm, AppHostMenuItem? host,
        double top, TrayPanelPalette palette)
    {
        var card = new PanelRect(RowInset, top + 4, Width - 2 * RowInset, ConfirmHeight - 8);
        commands.Add(new FillPathCommand(PanelPath.RoundedRect(card, 8), palette.Gutter));
        commands.Add(new StrokePathCommand(PanelPath.RoundedRect(card.Inset(0.5, 0.5), 7.5), palette.Border, 1));

        var name = host?.DisplayName ?? "";
        var (question, detail, accept, acceptName) = confirm.Kind switch
        {
            PanelConfirmKind.ClearRecent => ("Clear recent AppHosts?", "Pinned AppHosts are kept.", "Clear", "Clear recent AppHosts"),
            _ => ($"{name} was not found", host!.Id.AppHostPath, "Remove", $"Remove {name} from recent AppHosts")
        };
        var textWidth = card.Width - 24;
        var questionBounds = new PanelRect(card.X + 12, card.Y + 9, textWidth, 18);
        var detailBounds = new PanelRect(card.X + 12, card.Y + 28, textWidth, 16);
        commands.Add(new TextCommand(questionBounds, question, PanelFontRole.Name, palette.TextPrimary));
        commands.Add(new TextCommand(detailBounds, detail, PanelFontRole.Body, palette.TextSecondary,
            Truncation: confirm.Kind == PanelConfirmKind.RemoveMissing ? PanelTruncation.Middle : PanelTruncation.End));
        AddLabel("confirm.question", question, questionBounds, chrome: false);
        AddLabel("confirm.detail", detail, detailBounds, chrome: false);

        var controlsY = card.Bottom - 10 - ButtonSize;
        var section = confirm.Section;
        var id = host?.Id;
        const double ButtonWidth = 64;
        var acceptBounds = new PanelRect(card.Right - 10 - ButtonWidth, controlsY, ButtonWidth, ButtonSize);
        var cancelBounds = new PanelRect(acceptBounds.X - 8 - ButtonWidth, controlsY, ButtonWidth, ButtonSize);
        var cancelKey = new PanelTargetKey(PanelTargetKind.ConfirmCancel, section, id);
        var acceptKey = new PanelTargetKey(PanelTargetKind.ConfirmAccept, section, id);

        // Cancel is a neutral outlined button; the destructive one is filled with the danger
        // fill, which keeps its label readable where the danger text color would not.
        commands.Add(new FillPathCommand(PanelPath.RoundedRect(cancelBounds, 6), palette.Background));
        AddInteractionFill(commands, cancelKey, cancelBounds, palette, includeFocus: false);
        commands.Add(new StrokePathCommand(PanelPath.RoundedRect(cancelBounds.Inset(0.5, 0.5), 5.5), palette.Border, 1));
        commands.Add(new TextCommand(cancelBounds, "Cancel", PanelFontRole.Button, palette.TextPrimary, PanelTextAlignment.Center));
        AddTarget(targets, cancelKey, cancelBounds, "Cancel", true, host);

        commands.Add(new FillPathCommand(PanelPath.RoundedRect(acceptBounds, 6), palette.DangerFill));
        if (_pressed == acceptKey || _hover == acceptKey)
        {
            // Darken the filled button: the shared hover tints are tuned for the panel background.
            commands.Add(new FillPathCommand(PanelPath.RoundedRect(acceptBounds, 6),
                PanelColor.FromRgba(0, 0, 0, _pressed == acceptKey ? 0.22 : 0.12)));
        }
        if (palette.IsHighContrast)
        {
            commands.Add(new StrokePathCommand(PanelPath.RoundedRect(acceptBounds.Inset(0.5, 0.5), 5.5), palette.Border, 1));
        }
        commands.Add(new TextCommand(acceptBounds, accept, PanelFontRole.Button, palette.OnColor, PanelTextAlignment.Center));
        AddTarget(targets, acceptKey, acceptBounds, acceptName, true, host);
    }

    private void AddSettingsHeader(List<PanelCommand> commands, List<PanelHitTarget> targets, TrayPanelPalette palette)
    {
        // The back arrow sits on the leading column, where the logo is in the list view.
        var back = new PanelRect(LeadingCenter - ButtonSize / 2, (HeaderHeight - ButtonSize) / 2, ButtonSize, ButtonSize);
        var backKey = new PanelTargetKey(PanelTargetKind.Back, PanelSection.None, null);
        AddInteractionFill(commands, backKey, back, palette, includeFocus: false);
        TrayPanelGlyphs.AddIcon(commands, PanelIcon.ChevronLeft, back.Inset(6, 6), _hover == backKey ? palette.TextPrimary : palette.TextSecondary);
        AddTarget(targets, backKey, back, "Back to AppHosts", true, null, tooltip: "Back");
        var title = new PanelRect(TextLeft, (HeaderHeight - 20) / 2, Width - EdgeMargin - TextLeft, 20);
        commands.Add(new TextCommand(title, TraySettingsText.PanelTitle, PanelFontRole.Title, palette.TextPrimary));
        AddLabel("settings.title", TraySettingsText.PanelTitle, title, chrome: true);
    }

    private void AddSettingsToggle(List<PanelCommand> commands, List<PanelHitTarget> targets, PanelTargetKind kind, PanelSettings settings,
        double top, TrayPanelPalette palette)
    {
        var row = new PanelRect(RowInset, top + 2, Width - 2 * RowInset, SettingsRowHeight - 4);
        var key = new PanelTargetKey(kind, PanelSection.None, null);
        var startup = settings.Startup;
        var on = startup?.Enabled == true;
        // Turning it off always works; turning it on needs a stable native installation.
        var enabled = startup is not null && (on || startup.CanEnable);
        var title = TraySettingsText.PanelStartupOption;
        var status = startup is null ? settings.StartupError ?? "" : TraySettingsText.GetStartupStatus(startup, settings.StartupError);
        var error = settings.StartupError is not null;
        var description = string.IsNullOrEmpty(status) ? TraySettingsText.StartupDescription : status.Replace(Environment.NewLine, " ");
        if (enabled)
        {
            AddInteractionFill(commands, key, row, palette, includeFocus: false);
        }
        var toggle = new PanelRect(RowContentRight - 36, top + 9, 32, 18);
        var textWidth = toggle.X - 12 - 20;
        commands.Add(new TextCommand(new(20, top + 8, textWidth, 20), title, PanelFontRole.Name,
            enabled ? palette.TextPrimary : palette.TextTertiary));
        commands.Add(new TextCommand(new(20, top + 28, textWidth, 30), description, PanelFontRole.Body,
            error ? palette.Danger : palette.TextSecondary, MaxLines: 2));
        TrayPanelGlyphs.AddToggle(commands, on, enabled, toggle, palette);
        AddTarget(targets, key, row, title, enabled, null, PanelAccessibleRole.Switch, on, description);
    }

    private void AddAbout(List<PanelCommand> commands, PanelSettings settings, double top, TrayPanelPalette palette)
    {
        var name = new PanelRect(20, top + 4, Width - 40, 20);
        commands.Add(new TextCommand(name, "Aspire Tray", PanelFontRole.Name, palette.TextPrimary));
        AddLabel("about.name", "Aspire Tray", name, chrome: false);
        var lines = TraySettingsText.GetVersionText().Split(Environment.NewLine)
            .Append(TraySettingsText.GetAboutDescription(settings.Preview));
        var y = top + 24;
        var index = 0;
        foreach (var line in lines)
        {
            // Informational versions carry a long commit hash at the end; keep both ends visible.
            var bounds = new PanelRect(20, y, Width - 40, 16);
            commands.Add(new TextCommand(bounds, line, PanelFontRole.Body, palette.TextSecondary, Truncation: PanelTruncation.Middle));
            AddLabel($"about.line{index++}", line, bounds, chrome: false);
            y += 18;
        }
    }

    private void AddFooter(List<PanelCommand> commands, List<PanelHitTarget> targets, PanelRect footer, TrayPanelPalette palette)
    {
        // Three icon buttons on the right, each with a tooltip: Settings, Documentation, Quit.
        // Quit closes only the tray, never the AppHosts, so it isn't called "Quit Aspire" and its
        // tooltip and accessibility description say so.
        var right = RowContentRight;
        var quitDescription = $"{_quitVerb} the tray. AppHosts keep running.";
        foreach (var (kind, icon, name, tooltip, description, x) in new[]
        {
            (PanelTargetKind.Settings, PanelIcon.Gear, _settings is null ? "Settings" : "Close settings",
                _settings is null ? "Settings" : "Close settings", (string?)null, right - 3 * ButtonSize - 2 * ButtonGap),
            (PanelTargetKind.Documentation, PanelIcon.Globe, "Open Aspire documentation", "Documentation", null, right - 2 * ButtonSize - ButtonGap),
            (PanelTargetKind.Quit, PanelIcon.Power, _quitVerb, quitDescription, quitDescription, right - ButtonSize)
        })
        {
            var bounds = new PanelRect(x, footer.Y + 8, ButtonSize, ButtonSize);
            var key = new PanelTargetKey(kind, PanelSection.None, null);
            if (kind == PanelTargetKind.Settings && _settings is not null && _hover != key && _pressed != key)
            {
                // Mark the gear as selected while its view is open; clicking it again goes back.
                commands.Add(new FillPathCommand(PanelPath.RoundedRect(bounds, 6), palette.Pressed));
            }
            AddInteractionFill(commands, key, bounds, palette, includeFocus: false);
            TrayPanelGlyphs.AddIcon(commands, icon, bounds.Inset(6, 6), _hover == key ? palette.TextPrimary : palette.TextSecondary);
            AddTarget(targets, key, bounds, name, true, null, description: description, tooltip: tooltip);
        }
    }

    private void AddInteractionFill(List<PanelCommand> commands, PanelTargetKey key, PanelRect bounds, TrayPanelPalette palette, bool includeFocus)
    {
        var color = _pressed == key ? palette.Pressed
            : _hover == key || (includeFocus && _focusVisible && _focus == key) ? palette.Hover
            : (PanelColor?)null;
        if (color is { } fill)
        {
            commands.Add(new FillPathCommand(PanelPath.RoundedRect(bounds, 6), fill));
        }
    }

    private void AddTarget(List<PanelHitTarget> targets, PanelTargetKey key, PanelRect bounds, string name, bool enabled, AppHostMenuItem? host,
        PanelAccessibleRole role = PanelAccessibleRole.Button, bool? isChecked = null, string? description = null, string? tooltip = null)
    {
        var hit = IsChrome(key.Kind) ? bounds : bounds.Intersect(_viewport);
        var target = new PanelHitTarget(key, bounds, hit, name, enabled, host, role, isChecked, tooltip);
        targets.Add(target);
        _accessible.Add(new(PanelAccessibleKey.For(key), role, name, bounds, hit, target, description));
    }

    /// <summary>
    /// Adds read-only text to the accessibility tree. Text in the scrolled list reports only the part
    /// visible through the list clip, like hit targets; <paramref name="chrome"/> text never scrolls.
    /// </summary>
    private void AddLabel(string label, string text, PanelRect bounds, bool chrome)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }
        _accessible.Add(new(PanelAccessibleKey.For(label), PanelAccessibleRole.Text, text, bounds, chrome ? bounds : bounds.Intersect(_viewport)));
    }

    /// <summary>Targets in the fixed header or footer, outside the scrolled list.</summary>
    private static bool IsChrome(PanelTargetKind kind)
        => kind is PanelTargetKind.Documentation or PanelTargetKind.Settings or PanelTargetKind.Quit or PanelTargetKind.Back;

    private static bool IsWithinRow(PanelTargetKey? key, PanelTargetKey row)
        => key is { } value && value.Host == row.Host && value.Section == row.Section
            && value.Kind is PanelTargetKind.Row or PanelTargetKind.Dashboard or PanelTargetKind.Stop
                or PanelTargetKind.Start or PanelTargetKind.More;

    internal static PanelStatusGlyph GetStatusGlyph(AppHostMenuItem host, bool live) => host switch
    {
        { IsStarting: true } or { IsStopping: true } => PanelStatusGlyph.Busy,
        { IsRunning: false } => host.Error is null ? PanelStatusGlyph.Stopped : PanelStatusGlyph.Warning,
        _ when !live => PanelStatusGlyph.Unknown,
        { Error: not null } => PanelStatusGlyph.Warning,
        { Health: AppHostHealth.Healthy } => PanelStatusGlyph.Healthy,
        { Health: AppHostHealth.Warning } => PanelStatusGlyph.Warning,
        { Health: AppHostHealth.Unhealthy } => PanelStatusGlyph.Unhealthy,
        _ => PanelStatusGlyph.Unknown
    };

    /// <summary>
    /// Returns the second line of a row: <c>{short status} · {parent/dir}</c>, with the PID only when
    /// two running rows share a display name. Errors replace the whole line.
    /// </summary>
    internal static (string Text, bool IsError) GetRowDetail(AppHostMenuItem host, bool live, bool showPid)
    {
        if (host.Error is { } error)
        {
            return (error, true);
        }
        var status = host switch
        {
            { IsStarting: true } => "Starting…",
            { IsStopping: true } => "Stopping…",
            // Offline rows carry the controller's source-file probe ("AppHost source file not found").
            { IsRunning: false } when host.Id.AppHostPid == 0 && host.Subtitle != "Stopped" => host.Subtitle,
            { IsRunning: false } => "Stopped",
            _ when !live => "Status unavailable",
            { Health: AppHostHealth.Warning } => "Needs attention",
            { Health: AppHostHealth.Unhealthy } => "Unhealthy",
            _ => "Running"
        };
        var detail = $"{status} · {AppHostPresentation.GetLocation(host.Id.AppHostPath)}";
        return (showPid ? $"{detail} · PID {host.Id.AppHostPid}" : detail, false);
    }

    private static string GetRowAccessibleName(AppHostMenuItem host, string detail) => $"{host.DisplayName}, {detail}";

    /// <summary>Returns the concise header status line and the color of its connection dot.</summary>
    internal static (string Text, PanelColor Dot) GetHeaderStatus(TrayViewState state, TrayPanelPalette palette)
    {
        switch (state.Discovery)
        {
            case DiscoveryState.Connecting:
                return ("Connecting…", palette.Neutral);
            case DiscoveryState.Disconnected:
                return ("Reconnecting…", palette.Warning);
            case DiscoveryState.Incompatible:
                return ("Incompatible CLI", palette.Danger);
            case DiscoveryState.LimitExceeded:
                return ("Discovery limit exceeded", palette.Danger);
        }
        var running = state.AppHosts.Count(host => host.IsRunning && host.Id.AppHostPid != 0);
        var starting = state.AppHosts.Concat(state.RecentAppHosts).Count(host => host.IsStarting);
        var attention = state.AppHosts.Concat(state.RecentAppHosts).Count(host => host.Error is not null
            || (host.IsRunning && host.Id.AppHostPid != 0 && host.Health is AppHostHealth.Warning or AppHostHealth.Unhealthy));
        var parts = new List<string>();
        if (running > 0)
        {
            parts.Add($"{running} running");
        }
        if (starting > 0)
        {
            parts.Add($"{starting} starting");
        }
        if (attention > 0)
        {
            parts.Add($"{attention} need{(attention == 1 ? "s" : "")} attention");
        }
        return (parts.Count == 0 ? "No AppHosts running" : string.Join(" · ", parts), palette.Success);
    }

    private enum ListItemKind
    {
        Empty,
        SectionHeader,
        Row,
        Confirm,
        SettingsToggle,
        About,
        Actions
    }

    /// <summary>One item of the scrolled list. <see cref="Target"/> tells Settings items apart.</summary>
    private readonly record struct ListItem(ListItemKind Kind, double Y, double Height, PanelSection Section, AppHostMenuItem? Host,
        PanelTargetKind Target);

    /// <summary>A pending inline confirmation. <see cref="Host"/> is null for Clear recent.</summary>
    private sealed record PanelConfirm(PanelConfirmKind Kind, PanelSection Section, AppHostMenuItem? Host);
}
