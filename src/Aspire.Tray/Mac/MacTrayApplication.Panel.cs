// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Aspire.Tray;

/// <summary>
/// The flyout panel, the tray's only UI, shown by any activation of the status item. Layout,
/// glyphs and interaction live in the shared <see cref="TrayPanel"/>; this part only hosts it in
/// a borderless NSPanel, forwards input, and draws the display list with NSBezierPath and
/// NSAttributedString. VoiceOver sees the panel through MacTrayApplication.PanelAccessibility.cs.
/// </summary>
internal sealed partial class MacTrayApplication
{
    // NSEvent types and modifier flags.
    // https://developer.apple.com/documentation/appkit/nsevent/eventtype
    private const long LeftMouseDownEvent = 1;
    private const long RightMouseDownEvent = 3;
    private const long ShiftKeyMask = 1 << 17;
    private const long CommandKeyMask = 1 << 20;
    private const double PanelScreenMargin = 8;

    private readonly Dictionary<PanelFontRole, nint> _panelFonts = [];
    private readonly List<nint> _panelTooltipOwners = [];
    private readonly Dictionary<string, nint> _appIcons = [];
    private List<(PanelRect Bounds, string Text)> _panelTooltips = [];
    private TrayPanel? _panel;
    private nint _panelWindow;
    private nint _panelView;
    private long _panelHiddenAt;
    private bool _panelVisible;

    private static unsafe void RegisterPanelClasses()
    {
        var windowClass = AppKit.AllocateClass(AppKit.Class("NSPanel"), "AspireTrayFlyoutPanel", 0);
        var viewClass = AppKit.AllocateClass(AppKit.Class("NSView"), "AspireTrayFlyoutView", 0);
        if (windowClass == 0 || viewClass == 0)
        {
            throw new InvalidOperationException("Could not allocate the flyout panel classes.");
        }
        // Borderless windows refuse key status by default; the panel needs it for keyboard input.
        // https://developer.apple.com/documentation/appkit/nswindow/canbecomekey
        AddMethod(windowClass, "canBecomeKeyWindow", (nint)(delegate* unmanaged[Cdecl]<nint, nint, byte>)&ReturnYes, "c@:");
        AddMethod(viewClass, "isFlipped", (nint)(delegate* unmanaged[Cdecl]<nint, nint, byte>)&ReturnYes, "c@:");
        AddMethod(viewClass, "acceptsFirstResponder", (nint)(delegate* unmanaged[Cdecl]<nint, nint, byte>)&ReturnYes, "c@:");
        AddMethod(viewClass, "acceptsFirstMouse:", (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, byte>)&ReturnYesForEvent, "c@:@");
        AddMethod(viewClass, "drawRect:", (nint)(delegate* unmanaged[Cdecl]<nint, nint, AppKit.NativeRect, void>)&OnPanelDraw,
            "v@:{CGRect={CGPoint=dd}{CGSize=dd}}");
        AddMethod(viewClass, "viewDidChangeEffectiveAppearance", (nint)(delegate* unmanaged[Cdecl]<nint, nint, void>)&OnPanelAppearance, "v@:");
        foreach (var (name, callback) in new (string, nint)[]
        {
            ("mouseMoved:", (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&OnPanelMouseMoved),
            ("mouseDragged:", (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&OnPanelMouseMoved),
            ("mouseExited:", (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&OnPanelMouseExited),
            ("mouseDown:", (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&OnPanelMouseDown),
            ("mouseUp:", (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&OnPanelMouseUp),
            ("scrollWheel:", (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&OnPanelScroll),
            ("keyDown:", (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&OnPanelKeyDown)
        })
        {
            AddMethod(viewClass, name, callback, "v@:@");
        }
        RegisterPanelAccessibilityClasses(viewClass);
        AppKit.RegisterClass(windowClass);
        AppKit.RegisterClass(viewClass);

        static void AddMethod(nint cls, string selector, nint implementation, string types)
        {
            if (AppKit.AddMethod(cls, AppKit.Selector(selector), implementation, types) == 0)
            {
                throw new InvalidOperationException("Could not register a flyout panel method.");
            }
        }
    }

    private void AttachStatusItemAction()
    {
        var button = AppKit.Get(_statusItem, "button");
        AppKit.Set(button, "setTarget:", _target);
        AppKit.Set(button, "setAction:", AppKit.Selector("statusItemClicked:"));
        // Act on mouse-down, like a menu, for both buttons: a right click or Control-click opens
        // the same panel as a left click.
        // https://developer.apple.com/documentation/appkit/nscontrol/sendaction(on:)
        AppKit.SendInt64(button, AppKit.Selector("sendActionOn:"), (1 << 1) | (1 << 3));
    }

    private void OnStatusItemActivated()
    {
        var currentEvent = AppKit.Get(_application, "currentEvent");
        ToggleFromStatusItem(currentEvent == 0 ? 0 : AppKit.GetInt64(currentEvent, AppKit.Selector("type")));
    }

    /// <summary>
    /// Toggles the panel for an activation of the status item. Every activation opens it: a left
    /// or right click, a Control-click, and keyboard or VoiceOver activation (AXPress), which
    /// performs the button's click without a mouse event. Those last ones also show the keyboard
    /// focus, so the user can see where Tab and the arrow keys start.
    /// </summary>
    private void ToggleFromStatusItem(long eventType)
    {
        if (_panelVisible || Environment.TickCount64 - _panelHiddenAt < 250)
        {
            // A click on the status item can first take key status away from the panel, which
            // already closed it; that click should not reopen it.
            HidePanel();
            return;
        }
        ShowPanel(keyboard: eventType is not (LeftMouseDownEvent or RightMouseDownEvent));
    }

    private void ShowPanel(bool keyboard = false)
    {
        if (_quitting)
        {
            return;
        }
        EnsurePanel();
        _controller.PruneMissingPinnedAppHosts();
        DiscoverFolderApplications();
        _panel!.OpenInApplications = _installedFolderApplications.Select(application => application.Title).ToArray();
        _panel.Update(_controller.State);
        _panel.Reset();
        _panel.Palette = GetPanelPalette();
        if (keyboard)
        {
            _panel.FocusFirst();
        }
        _panelVisible = true;
        LayoutPanel();
        AppKit.SendBool(AppKit.Get(_statusItem, "button"), AppKit.Selector("highlight:"), 1);
        AppKit.SendBool(_application, AppKit.Selector("activateIgnoringOtherApps:"), 1);
        AppKit.Set(_panelWindow, "makeKeyAndOrderFront:", 0);
        AppKit.SendReturningBool(_panelWindow, AppKit.Selector("makeFirstResponder:"), _panelView);
    }

    private void HidePanel()
    {
        if (!_panelVisible)
        {
            return;
        }
        _panelVisible = false;
        _panelHiddenAt = Environment.TickCount64;
        AppKit.Set(_panelWindow, "orderOut:", 0);
        // ShowPanel activated this agent app so the borderless panel could take key status.
        // Hiding the app hands activation back to the app the user was in, as a closing menu
        // does; otherwise their editor keeps no keyboard focus until they click it again.
        // https://developer.apple.com/documentation/appkit/nsapplication/hide(_:)
        AppKit.Set(_application, "hide:", 0);
        if (_statusItem != 0)
        {
            AppKit.SendBool(AppKit.Get(_statusItem, "button"), AppKit.Selector("highlight:"), 0);
        }
    }

    private void EnsurePanel()
    {
        if (_panelWindow != 0)
        {
            return;
        }
        _panel = new TrayPanel(_controller.State, GetPanelPalette()) { FileManagerName = "Finder", QuitVerb = "Quit" };
        var frame = new AppKit.NativeRect(new(0, 0), new(TrayPanel.Width, 200));
        // Borderless (0), buffered backing (2).
        _panelWindow = AppKit.CreateWindow(AppKit.Get(AppKit.Class("AspireTrayFlyoutPanel"), "alloc"),
            AppKit.Selector("initWithContentRect:styleMask:backing:defer:"), frame, 0, 2, 0);
        if (_panelWindow == 0)
        {
            throw new InvalidOperationException("Could not create the flyout panel.");
        }
        AppKit.SendBool(_panelWindow, AppKit.Selector("setReleasedWhenClosed:"), 0);
        AppKit.SendBool(_panelWindow, AppKit.Selector("setOpaque:"), 0);
        AppKit.Set(_panelWindow, "setBackgroundColor:", AppKit.Get(AppKit.Class("NSColor"), "clearColor"));
        AppKit.SendBool(_panelWindow, AppKit.Selector("setHasShadow:"), 1);
        AppKit.SendBool(_panelWindow, AppKit.Selector("setHidesOnDeactivate:"), 0);
        // NSPopUpMenuWindowLevel, the level menus use, so the panel sits above normal windows.
        AppKit.SendInt64(_panelWindow, AppKit.Selector("setLevel:"), 101);
        // Join the active Space and float over full-screen apps, like a menu.
        // https://developer.apple.com/documentation/appkit/nswindow/collectionbehavior-swift.struct
        AppKit.SendInt64(_panelWindow, AppKit.Selector("setCollectionBehavior:"), (1 << 1) | (1 << 3) | (1 << 8));
        AppKit.Set(_panelWindow, "setDelegate:", _target);
        AppKit.Set(_panelWindow, "setAccessibilityLabel:", AppKit.String("Aspire"));

        _panelView = AppKit.SendRect(AppKit.Get(AppKit.Class("AspireTrayFlyoutView"), "alloc"), AppKit.Selector("initWithFrame:"), frame);
        // MouseEnteredAndExited | MouseMoved | ActiveAlways | InVisibleRect: the area follows
        // the view's size, so it never needs updating when the panel height changes.
        // https://developer.apple.com/documentation/appkit/nstrackingarea/options
        var tracking = AppKit.CreateTrackingArea(AppKit.Get(AppKit.Class("NSTrackingArea"), "alloc"),
            AppKit.Selector("initWithRect:options:owner:userInfo:"), default, 0x01 | 0x02 | 0x80 | 0x200, _panelView, 0);
        AppKit.Set(_panelView, "addTrackingArea:", tracking);
        AppKit.Release(tracking);
        AppKit.Set(_panelWindow, "setContentView:", _panelView);
        ConfigurePanelViewAccessibility();
    }

    private void LayoutPanel()
    {
        var button = AppKit.Get(_statusItem, "button");
        var buttonWindow = AppKit.Get(button, "window");
        var anchor = buttonWindow == 0 ? default : AppKit.GetRect(buttonWindow, "frame");
        var screen = buttonWindow == 0 ? 0 : AppKit.Get(buttonWindow, "screen");
        if (screen == 0)
        {
            screen = AppKit.Get(AppKit.Class("NSScreen"), "mainScreen");
        }
        var visible = AppKit.GetRect(screen, "visibleFrame");
        var visibleTop = visible.Origin.Y + visible.Size.Height;
        if (buttonWindow == 0 || anchor.Origin.Y + anchor.Size.Height < visibleTop)
        {
            // A status item hidden behind the camera housing, or pushed off a crowded menu bar,
            // keeps an offscreen window (observed at y = -37). Hang the panel from the top-right
            // corner of the screen instead of shrinking it to fit below that window.
            anchor = new(new(visible.Origin.X + visible.Size.Width - TrayPanel.Width, visibleTop), new(0, 0));
        }
        // Screen coordinates have a bottom-left origin. The panel hangs below the menu bar,
        // centered on the status item and kept inside the visible frame.
        var top = Math.Min(anchor.Origin.Y - 4, visible.Origin.Y + visible.Size.Height);
        _panel!.MaxHeight = Math.Min(TrayPanel.DefaultMaxHeight, top - visible.Origin.Y - PanelScreenMargin);
        var height = _panel.Scene.Height;
        var x = anchor.Origin.X + anchor.Size.Width / 2 - TrayPanel.Width / 2;
        x = Math.Clamp(x, visible.Origin.X + PanelScreenMargin,
            Math.Max(visible.Origin.X + PanelScreenMargin, visible.Origin.X + visible.Size.Width - PanelScreenMargin - TrayPanel.Width));
        AppKit.SetFrame(_panelWindow, AppKit.Selector("setFrame:display:"),
            new(new(Math.Round(x), Math.Round(top - height)), new(TrayPanel.Width, height)), 1);
        RedrawPanel();
    }

    private void UpdatePanel(TrayViewState state)
    {
        if (!_panelVisible || _panel is null)
        {
            return;
        }
        if (_panel.Update(state))
        {
            ApplyPanelResponse(PanelResponse.Changed);
        }
    }

    private void RedrawPanel()
    {
        AppKit.SendBool(_panelView, AppKit.Selector("setNeedsDisplay:"), 1);
        // The window shadow follows the drawn alpha (the rounded corners), so recompute it
        // after the content changes shape.
        AppKit.SendVoid(_panelView, AppKit.Selector("displayIfNeeded"));
        AppKit.SendVoid(_panelWindow, AppKit.Selector("invalidateShadow"));
        SyncPanelAccessibility();
        SyncPanelTooltips();
    }

    /// <summary>
    /// Registers a native tooltip over each visible icon button. Rebuilt only when a tooltip or
    /// its rectangle changes (scrolling, a new view), so hovering doesn't restart AppKit's delay.
    /// </summary>
    private void SyncPanelTooltips()
    {
        var tooltips = _panel!.Scene.Targets.Where(target => target.Tooltip is not null && !target.HitBounds.IsEmpty)
            .Select(target => (target.HitBounds, target.Tooltip!)).ToList();
        if (tooltips.SequenceEqual(_panelTooltips))
        {
            return;
        }
        _panelTooltips = tooltips;
        AppKit.SendVoid(_panelView, AppKit.Selector("removeAllToolTips"));
        ReleasePanelTooltipOwners();
        foreach (var (bounds, text) in tooltips)
        {
            // A tooltip rectangle asks its owner for the text, and an owner without
            // view:stringForToolTip:point:userData: answers with its description, which for an
            // NSString is the string itself. AppKit doesn't retain the owner, so we do.
            // https://developer.apple.com/documentation/appkit/nsview/addtooltip(_:owner:userdata:)
            var owner = AppKit.Get(AppKit.String(text), "retain");
            _panelTooltipOwners.Add(owner);
            // The view is flipped, so panel coordinates are view coordinates.
            AppKit.SendRectAndTwoPointers(_panelView, AppKit.Selector("addToolTipRect:owner:userData:"), ToNative(bounds), owner, 0);
        }
    }

    private void ReleasePanelTooltipOwners()
    {
        foreach (var owner in _panelTooltipOwners)
        {
            AppKit.Release(owner);
        }
        _panelTooltipOwners.Clear();
    }

    private void ApplyPanelResponse(PanelResponse response)
    {
        if (response.Redraw)
        {
            var frame = AppKit.GetRect(_panelWindow, "frame");
            if (Math.Abs(frame.Size.Height - _panel!.Scene.Height) > 0.5)
            {
                LayoutPanel();
            }
            else
            {
                RedrawPanel();
            }
        }
        if (response.Action is { } action)
        {
            RunPanelAction(action);
        }
    }

    private void RunPanelAction(PanelAction action)
    {
        switch (action.Kind)
        {
            case PanelActionKind.OpenDashboard:
                HidePanel();
                OpenDashboard(action.Host!.Id);
                break;
            case PanelActionKind.Stop:
                StopAppHost(action.Host!.Id);
                break;
            case PanelActionKind.Start:
                RunAction(() => StartAppHost(action.Host!.Id.AppHostPath));
                break;
            case PanelActionKind.OpenIn:
                RunAction(() => OpenIn(action.Host!.Id.AppHostPath, action.Application
                    ?? throw new InvalidOperationException("The selected application is no longer available.")));
                break;
            case PanelActionKind.ShowInFolder:
                RunAction(() => ShowInFinder(action.Host!.Id.AppHostPath));
                break;
            case PanelActionKind.CopyPath:
                // Copy the containing folder, as a terminal or editor wants it. This works even
                // when the AppHost file itself was moved or deleted.
                RunAction(() => _copyPath(Path.GetDirectoryName(action.Host!.Id.AppHostPath)!));
                break;
            case PanelActionKind.TogglePin:
                RunAction(() => _controller.SetPinned(action.Host!.Id.AppHostPath, !action.Host.IsPinned));
                break;
            case PanelActionKind.ClearRecent:
                RunAction(_controller.ClearRecent);
                break;
            case PanelActionKind.RemoveRecent:
                RunAction(() => _controller.RemoveRecent(action.Host!.Id.AppHostPath));
                break;
            case PanelActionKind.Documentation:
                HidePanel();
                RunAction(() => _openDashboard(new Uri("https://aspire.dev")));
                break;
            case PanelActionKind.Settings:
                ApplyPanelResponse(_panel!.ShowSettings(PanelSettings.Read(_startupSettings, _interactiveSmoke, LogStartupReadFailure)));
                break;
            case PanelActionKind.SetLaunchAtSignIn:
                ApplyPanelResponse(_panel!.UpdateSettings(PanelSettings.SetLaunchAtSignIn(_startupSettings, action.Enabled, _interactiveSmoke,
                    static ex => LogFailure("Unable to change launch-at-sign-in settings", ex))));
                break;
            case PanelActionKind.Quit:
                HidePanel();
                Quit();
                break;
            case PanelActionKind.Close:
                HidePanel();
                break;
        }
    }

    private static void LogStartupReadFailure(Exception exception) => LogFailure("Unable to read launch-at-sign-in settings", exception);

    private TrayPanelPalette GetPanelPalette()
    {
        var appearance = AppKit.Get(_panelView != 0 ? _panelView : _application, "effectiveAppearance");
        var dark = false;
        if (appearance != 0)
        {
            unsafe
            {
                nint* names = stackalloc nint[2] { AppKit.Constant("NSAppearanceNameAqua"), AppKit.Constant("NSAppearanceNameDarkAqua") };
                var array = AppKit.CreateArray(AppKit.Class("NSArray"), AppKit.Selector("arrayWithObjects:count:"), names, 2);
                var match = AppKit.Get(appearance, "bestMatchFromAppearancesWithNames:", array);
                dark = match != 0 && AppKit.Text(match) == AppKit.Text(names[1]);
            }
        }
        var palette = dark ? TrayPanelPalette.Dark : TrayPanelPalette.Light;
        var workspace = AppKit.Get(AppKit.Class("NSWorkspace"), "sharedWorkspace");
        return AppKit.GetBool(workspace, AppKit.Selector("accessibilityDisplayShouldIncreaseContrast")) != 0
            ? palette.WithIncreasedContrast()
            : palette;
    }

    private void DrawPanel()
    {
        if (_panel is null)
        {
            return;
        }
        foreach (var command in _panel.Scene.Commands)
        {
            DrawPanelCommand(command);
        }
    }

    /// <summary>
    /// The selectors, classes and attribute names the draw path uses, looked up once. A scene has
    /// a few hundred commands and is redrawn on every hover change, so per-command
    /// sel_registerName and objc_getClass calls (each marshalling a string) would dominate a
    /// repaint. The explicit constructor keeps the lookups on first use, after AppKit is loaded.
    /// </summary>
    private static class Draw
    {
        static Draw()
        {
        }

        public static readonly nint GraphicsContextClass = AppKit.Class("NSGraphicsContext");
        public static readonly nint BezierPathClass = AppKit.Class("NSBezierPath");
        public static readonly nint ColorClass = AppKit.Class("NSColor");
        public static readonly nint ParagraphStyleClass = AppKit.Class("NSMutableParagraphStyle");
        public static readonly nint DictionaryClass = AppKit.Class("NSDictionary");
        public static readonly nint AttributedStringClass = AppKit.Class("NSAttributedString");
        public static readonly nint Fill = AppKit.Selector("fill");
        public static readonly nint Stroke = AppKit.Selector("stroke");
        public static readonly nint SetLineWidth = AppKit.Selector("setLineWidth:");
        public static readonly nint SetLineCapStyle = AppKit.Selector("setLineCapStyle:");
        public static readonly nint SetLineJoinStyle = AppKit.Selector("setLineJoinStyle:");
        public static readonly nint SaveGraphicsState = AppKit.Selector("saveGraphicsState");
        public static readonly nint RestoreGraphicsState = AppKit.Selector("restoreGraphicsState");
        public static readonly nint ClipRect = AppKit.Selector("clipRect:");
        public static readonly nint DrawInRect = AppKit.Selector("drawInRect:fromRect:operation:fraction:respectFlipped:hints:");
        public static readonly nint BezierPath = AppKit.Selector("bezierPath");
        public static readonly nint MoveToPoint = AppKit.Selector("moveToPoint:");
        public static readonly nint LineToPoint = AppKit.Selector("lineToPoint:");
        public static readonly nint CurveToPoint = AppKit.Selector("curveToPoint:controlPoint1:controlPoint2:");
        public static readonly nint ClosePath = AppKit.Selector("closePath");
        public static readonly nint Set = AppKit.Selector("set");
        public static readonly nint ColorWithSrgb = AppKit.Selector("colorWithSRGBRed:green:blue:alpha:");
        public static readonly nint New = AppKit.Selector("new");
        public static readonly nint Alloc = AppKit.Selector("alloc");
        public static readonly nint SetLineBreakMode = AppKit.Selector("setLineBreakMode:");
        public static readonly nint SetAlignment = AppKit.Selector("setAlignment:");
        public static readonly nint DictionaryWithObjects = AppKit.Selector("dictionaryWithObjects:forKeys:count:");
        public static readonly nint InitWithStringAttributes = AppKit.Selector("initWithString:attributes:");
        public static readonly nint Ascender = AppKit.Selector("ascender");
        public static readonly nint Descender = AppKit.Selector("descender");
        public static readonly nint Leading = AppKit.Selector("leading");
        public static readonly nint DrawWithRect = AppKit.Selector("drawWithRect:options:context:");
        public static readonly nint Release = AppKit.Selector("release");
        public static readonly nint FontAttributeName = AppKit.Constant("NSFontAttributeName");
        public static readonly nint ForegroundColorAttributeName = AppKit.Constant("NSForegroundColorAttributeName");
        public static readonly nint ParagraphStyleAttributeName = AppKit.Constant("NSParagraphStyleAttributeName");
    }

    private void DrawPanelCommand(PanelCommand command)
    {
        switch (command)
        {
            case FillPathCommand fill:
                SetColor(fill.Color);
                AppKit.SendVoid(CreateBezierPath(fill.Path), Draw.Fill);
                break;
            case StrokePathCommand stroke:
                SetColor(stroke.Color);
                var path = CreateBezierPath(stroke.Path);
                AppKit.SetDouble(path, Draw.SetLineWidth, stroke.Width);
                AppKit.SendInt64(path, Draw.SetLineCapStyle, 1);
                AppKit.SendInt64(path, Draw.SetLineJoinStyle, 1);
                AppKit.SendVoid(path, Draw.Stroke);
                break;
            case TextCommand text:
                DrawPanelText(text);
                break;
            case LogoCommand logo:
                // NSCompositingOperationSourceOver (2), drawn upright in the flipped view.
                AppKit.DrawImageRespectingFlipped(_brandImage, Draw.DrawInRect, ToNative(logo.Bounds), default, 2, 1, 1, 0);
                break;
            case AppIconCommand appIcon:
                if (GetAppIcon(appIcon.Application) is not 0 and var icon)
                {
                    AppKit.DrawImageRespectingFlipped(icon, Draw.DrawInRect, ToNative(appIcon.Bounds), default, 2, 1, 1, 0);
                }
                else
                {
                    foreach (var shape in appIcon.Fallback)
                    {
                        DrawPanelCommand(shape);
                    }
                }
                break;
            case PushClipCommand clip:
                AppKit.SendVoid(Draw.GraphicsContextClass, Draw.SaveGraphicsState);
                AppKit.SetRect(Draw.BezierPathClass, Draw.ClipRect, ToNative(clip.Bounds));
                break;
            case PopClipCommand:
                AppKit.SendVoid(Draw.GraphicsContextClass, Draw.RestoreGraphicsState);
                break;
        }
    }

    /// <summary>
    /// Returns the Finder icon of an installed "Open in" application, or 0 when it is gone. NSImage
    /// holds every representation, so one cached image draws sharply at any scale.
    /// https://developer.apple.com/documentation/appkit/nsworkspace/icon(forfile:)
    /// </summary>
    private nint GetAppIcon(string title)
    {
        if (!_appIcons.TryGetValue(title, out var icon))
        {
            var path = _installedFolderApplications.FirstOrDefault(application => application.Title == title).Path;
            var workspace = AppKit.Get(AppKit.Class("NSWorkspace"), "sharedWorkspace");
            icon = path is null ? 0 : AppKit.Get(AppKit.Get(workspace, "iconForFile:", AppKit.String(path)), "retain");
            _appIcons.Add(title, icon);
        }
        return icon;
    }

    private static void SetColor(PanelColor color)
        => AppKit.SendVoid(CreateColor(color), Draw.Set);

    private static nint CreateColor(PanelColor color)
        => AppKit.SendFourDoubles(Draw.ColorClass, Draw.ColorWithSrgb, color.R / 255.0, color.G / 255.0, color.B / 255.0, color.Alpha);

    private static nint CreateBezierPath(PanelPath source)
    {
        var path = AppKit.Send(Draw.BezierPathClass, Draw.BezierPath);
        foreach (var segment in source.Segments)
        {
            switch (segment.Verb)
            {
                case PanelPathVerb.MoveTo:
                    AppKit.SendPoint(path, Draw.MoveToPoint, new(segment.X3, segment.Y3));
                    break;
                case PanelPathVerb.LineTo:
                    AppKit.SendPoint(path, Draw.LineToPoint, new(segment.X3, segment.Y3));
                    break;
                case PanelPathVerb.CubicTo:
                    AppKit.SendThreePoints(path, Draw.CurveToPoint, new(segment.X3, segment.Y3), new(segment.X1, segment.Y1), new(segment.X2, segment.Y2));
                    break;
                default:
                    AppKit.SendVoid(path, Draw.ClosePath);
                    break;
            }
        }
        return path;
    }

    private unsafe void DrawPanelText(TextCommand text)
    {
        var font = GetPanelFont(text.Font);
        var style = AppKit.Send(Draw.ParagraphStyleClass, Draw.New);
        try
        {
            // NSLineBreakMode: byWordWrapping (0), byTruncatingTail (4), byTruncatingMiddle (5).
            AppKit.SendInt64(style, Draw.SetLineBreakMode, text.MaxLines > 1 ? 0 : text.Truncation == PanelTruncation.Middle ? 5 : 4);
            AppKit.SendInt64(style, Draw.SetAlignment, GetTextAlignment(text.Alignment));
            nint* keys = stackalloc nint[3] { Draw.FontAttributeName, Draw.ForegroundColorAttributeName, Draw.ParagraphStyleAttributeName };
            nint* values = stackalloc nint[3] { font, CreateColor(text.Color), style };
            var attributes = AppKit.CreateDictionary(Draw.DictionaryClass, Draw.DictionaryWithObjects, values, keys, 3);
            var value = AppKit.SendTwoPointers(AppKit.Send(Draw.AttributedStringClass, Draw.Alloc), Draw.InitWithStringAttributes,
                AppKit.String(text.Text), attributes);
            try
            {
                // Size the box from the font's line metrics. A single line is placed so its baseline
                // lands on the scene's baseline, snapped to a device pixel: with the line fragment
                // origin option, AppKit draws the first baseline one ascender below the box's top.
                // Centering the line box instead put SF text about 1 DIP low (its descender is
                // shallower than its ascender). Multi-line text starts at the top of its bounds.
                var ascender = AppKit.GetDouble(font, Draw.Ascender);
                var lineHeight = ascender - AppKit.GetDouble(font, Draw.Descender) + AppKit.GetDouble(font, Draw.Leading);
                var scale = _panelWindow == 0 ? 1 : AppKit.GetDouble(_panelWindow, AppKit.Selector("backingScaleFactor"));
                var baseline = Math.Round(text.Baseline * scale) / scale;
                var bounds = text.MaxLines > 1
                    ? new AppKit.NativeRect(new(text.Bounds.X, text.Bounds.Y), new(text.Bounds.Width, Math.Min(text.Bounds.Height, lineHeight * text.MaxLines)))
                    : new AppKit.NativeRect(new(text.Bounds.X, baseline - ascender), new(text.Bounds.Width, lineHeight));
                // NSStringDrawingUsesLineFragmentOrigin (1) | NSStringDrawingTruncatesLastVisibleLine (32).
                // https://developer.apple.com/documentation/foundation/nsstring/drawingoptions
                AppKit.DrawWithRect(value, Draw.DrawWithRect, bounds, 1 | 32, 0);
            }
            finally
            {
                AppKit.SendVoid(value, Draw.Release);
            }
        }
        finally
        {
            AppKit.SendVoid(style, Draw.Release);
        }
    }

    private static long GetTextAlignment(PanelTextAlignment alignment)
    {
        // NSTextAlignment uses the iOS values (center 1, right 2) on Apple silicon and the
        // historical macOS values (right 1, center 2) on Intel.
        // https://developer.apple.com/documentation/appkit/nstextalignment
        var intel = RuntimeInformation.ProcessArchitecture == Architecture.X64;
        return alignment switch
        {
            PanelTextAlignment.Center => intel ? 2 : 1,
            PanelTextAlignment.Trailing => intel ? 1 : 2,
            _ => 0
        };
    }

    private nint GetPanelFont(PanelFontRole role)
    {
        if (!_panelFonts.TryGetValue(role, out var font))
        {
            var weight = PanelFonts.IsSemibold(role)
                ? AppKit.DoubleConstant("NSFontWeightSemibold")
                : AppKit.DoubleConstant("NSFontWeightRegular");
            font = AppKit.Get(AppKit.SendTwoDoubles(AppKit.Class("NSFont"), AppKit.Selector("systemFontOfSize:weight:"),
                PanelFonts.Size(role), weight), "retain");
            _panelFonts.Add(role, font);
        }
        return font;
    }

    private static AppKit.NativeRect ToNative(PanelRect rect) => new(new(rect.X, rect.Y), new(rect.Width, rect.Height));

    private (double X, double Y) GetPanelPoint(nint nativeEvent)
    {
        var point = AppKit.ConvertPoint(_panelView, AppKit.Selector("convertPoint:fromView:"),
            AppKit.GetPoint(nativeEvent, AppKit.Selector("locationInWindow")), 0);
        return (point.X, point.Y);
    }

    private void HandlePanelEvent(string kind, nint nativeEvent)
    {
        if (_panel is null || !_panelVisible)
        {
            return;
        }
        var response = PanelResponse.None;
        switch (kind)
        {
            case "moved":
                var (mx, my) = GetPanelPoint(nativeEvent);
                response = _panel.PointerMoved(mx, my);
                break;
            case "exited":
                response = _panel.PointerLeft();
                break;
            case "down":
                var (dx, dy) = GetPanelPoint(nativeEvent);
                response = _panel.PointerDown(dx, dy);
                break;
            case "up":
                var (ux, uy) = GetPanelPoint(nativeEvent);
                response = _panel.PointerUp(ux, uy);
                break;
            case "scroll":
                var delta = AppKit.GetDouble(nativeEvent, AppKit.Selector("scrollingDeltaY"));
                // Mouse wheels report lines rather than points; scale a line to a short step.
                if (AppKit.GetBool(nativeEvent, AppKit.Selector("hasPreciseScrollingDeltas")) == 0)
                {
                    delta *= 16;
                }
                response = _panel.Scroll(delta);
                break;
            case "key":
                response = HandlePanelKey(nativeEvent);
                break;
        }
        ApplyPanelResponse(response);
    }

    private PanelResponse HandlePanelKey(nint nativeEvent)
    {
        // keyCode is an unsigned short; ignore any upper register bits.
        var keyCode = AppKit.GetInt64(nativeEvent, AppKit.Selector("keyCode")) & 0xFFFF;
        var modifiers = AppKit.GetInt64(nativeEvent, AppKit.Selector("modifierFlags"));
        if ((modifiers & CommandKeyMask) != 0)
        {
            // Command-Q quits and Command-W closes, as in any other window, and Command-comma
            // opens Settings, as in any other app.
            return keyCode switch
            {
                12 => new(false, new PanelAction(PanelActionKind.Quit)),
                13 => new(false, new PanelAction(PanelActionKind.Close)),
                43 when !_panel!.IsSettingsOpen => new(false, new PanelAction(PanelActionKind.Settings)),
                _ => PanelResponse.None
            };
        }
        // Virtual key codes from HIToolbox Events.h (kVK_*).
        PanelKey? key = keyCode switch
        {
            126 => PanelKey.Up,
            125 => PanelKey.Down,
            48 => (modifiers & ShiftKeyMask) != 0 ? PanelKey.ShiftTab : PanelKey.Tab,
            36 or 76 => PanelKey.Enter,
            49 => PanelKey.Space,
            53 => PanelKey.Escape,
            115 => PanelKey.Home,
            119 => PanelKey.End,
            _ => null
        };
        return key is { } value ? _panel!.Key(value) : PanelResponse.None;
    }

    /// <summary>
    /// Opens the flyout through the production path and checks the native window against the
    /// shared scene. When <paramref name="captureDirectory"/> is set, also writes PNGs rendered by
    /// AppKit itself (light, dark, and dark with hover and keyboard focus) for visual review;
    /// caching the view's display needs no screen-recording permission.
    /// </summary>
    internal string VerifyPanelForSmoke(string? captureDirectory)
    {
        VerifyUIThread();
        var button = AppKit.Get(_statusItem, "button");
        Require(AppKit.Get(_statusItem, "menu") == 0, "The status item must not keep a menu attached; every activation opens the panel.");
        Require(AppKit.Get(button, "action") == AppKit.Selector("statusItemClicked:") && AppKit.Get(button, "target") == _target,
            "The status button must route clicks to the panel selector.");
        ShowPanel();
        try
        {
            Require(_panelVisible && AppKit.GetBool(_panelWindow, AppKit.Selector("isVisible")) != 0, "The flyout panel did not open.");
            var frame = AppKit.GetRect(_panelWindow, "frame");
            var scene = _panel!.Scene;
            Require(frame.Size.Width == TrayPanel.Width && Math.Abs(frame.Size.Height - scene.Height) < 0.5,
                $"The panel window ({frame.Size.Width}x{frame.Size.Height}) does not match the scene ({scene.Width}x{scene.Height}).");
            Require(AppKit.GetBool(_panelView, AppKit.Selector("isFlipped")) != 0, "The panel view must use top-left coordinates.");
            Require(AppKit.GetBool(_panelWindow, AppKit.Selector("canBecomeKeyWindow")) != 0, "The borderless panel must accept key status.");
            Require(scene.Targets.Count(target => target.Kind == PanelTargetKind.Row) == _controller.State.AppHosts.Count
                + (_panel.IsRecentExpanded ? _controller.State.RecentAppHosts.Count : 0), "The panel must list every AppHost.");
            VerifyPanelAccessibilityForSmoke("the list");
            Require(scene.Accessibility.Single(element => element.Key.Label == "header.title").Bounds.CenterY == TrayPanel.HeaderHeight / 2
                && scene.Accessibility.Single(element => element.Key.Label == "header.status").Bounds.CenterY == TrayPanel.HeaderHeight / 2
                && TrayPanel.HeaderHeight == TrayPanel.FooterHeight,
                "The header must be one line, as tall as the footer.");
            Require(_panelTooltips.Select(tooltip => tooltip.Text).SequenceEqual(
                    scene.Targets.Where(target => target.Tooltip is not null && !target.HitBounds.IsEmpty).Select(target => target.Tooltip!))
                && _panelTooltipOwners.Count == _panelTooltips.Count && _panelTooltips.Any(tooltip => tooltip.Text == "Documentation")
                && scene.Accessibility.Single(element => element.Target?.Kind == PanelTargetKind.Quit) is
                    { Name: "Quit", Description: "Quit the tray. AppHosts keep running.", Target.Tooltip: "Quit the tray. AppHosts keep running." },
                "Every visible icon button must have exactly one native tooltip.");
            var captures = new List<string>();
            if (captureDirectory is not null)
            {
                Directory.CreateDirectory(captureDirectory);
                foreach (var (name, appearance) in new[] { ("light", "NSAppearanceNameAqua"), ("dark", "NSAppearanceNameDarkAqua") })
                {
                    AppKit.Set(_panelView, "setAppearance:", AppKit.Get(AppKit.Class("NSAppearance"), "appearanceNamed:", AppKit.Constant(appearance)));
                    _panel.Palette = GetPanelPalette();
                    captures.Add(CapturePanel(Path.Combine(captureDirectory, $"panel-{name}.png")));
                }
                var row = _panel.Scene.Targets.Where(target => target.Kind == PanelTargetKind.Row).Skip(1).FirstOrDefault();
                if (row is not null)
                {
                    _panel.Key(PanelKey.Down);
                    _panel.PointerMoved(row.HitBounds.X + 40, row.HitBounds.CenterY);
                    captures.Add(CapturePanel(Path.Combine(captureDirectory, "panel-dark-interactive.png")));
                    _panel.Reset();
                }
                var more = _panel.Scene.Targets.FirstOrDefault(target => target.Kind == PanelTargetKind.More);
                if (more is not null)
                {
                    _panel.Invoke(more.Key);
                    foreach (var (name, appearance) in new[] { ("light", "NSAppearanceNameAqua"), ("dark", "NSAppearanceNameDarkAqua") })
                    {
                        AppKit.Set(_panelView, "setAppearance:", AppKit.Get(AppKit.Class("NSAppearance"), "appearanceNamed:", AppKit.Constant(appearance)));
                        _panel.Palette = GetPanelPalette();
                        captures.Add(CapturePanel(Path.Combine(captureDirectory, $"panel-actions-{name}.png")));
                    }
                    if (_panel.Scene.Targets.FirstOrDefault(target => target.Kind == PanelTargetKind.OpenInMenu) is { } openIn)
                    {
                        _panel.Invoke(openIn.Key);
                        captures.Add(CapturePanel(Path.Combine(captureDirectory, "panel-actions-open-in-dark.png")));
                        _panel.Key(PanelKey.Escape);
                    }
                    _panel.Key(PanelKey.Escape);
                }
                AppKit.Set(_panelView, "setAppearance:", 0);
                _panel.Palette = GetPanelPalette();
            }

            // The gear opens Settings inside the panel; its toggle round-trips through the
            // startup settings (an isolated in-memory store during smoke).
            ClickPanelForSmoke(_panel.Scene.Targets.Single(target => target.Kind == PanelTargetKind.Settings));
            Require(_panel.IsSettingsOpen && _panelVisible, "The gear must open the Settings view inside the panel.");
            RequireWindowMatchesScene();
            VerifyPanelAccessibilityForSmoke("the Settings view");
            var launch = _panel.Scene.Targets.Single(target => target.Kind == PanelTargetKind.LaunchAtSignIn);
            if (launch.Enabled)
            {
                var before = _startupSettings.Read().Enabled;
                // Press the switch the way VoiceOver does (AXPress), then check its new value.
                OnPanelElementPressed(PanelAccessibleKey.For(launch.Key));
                Require(_startupSettings.Read().Enabled != before, "Pressing the Settings switch must change launch at sign-in.");
                VerifyPanelAccessibilityForSmoke("the Settings view after a press");
                ClickPanelForSmoke(_panel.Scene.Targets.Single(target => target.Kind == PanelTargetKind.LaunchAtSignIn));
                Require(_startupSettings.Read().Enabled == before, "The Settings view toggle must change launch at sign-in back.");
            }
            if (captureDirectory is not null)
            {
                foreach (var (name, appearance) in new[] { ("light", "NSAppearanceNameAqua"), ("dark", "NSAppearanceNameDarkAqua") })
                {
                    AppKit.Set(_panelView, "setAppearance:", AppKit.Get(AppKit.Class("NSAppearance"), "appearanceNamed:", AppKit.Constant(appearance)));
                    _panel.Palette = GetPanelPalette();
                    captures.Add(CapturePanel(Path.Combine(captureDirectory, $"panel-settings-{name}.png")));
                }
                AppKit.Set(_panelView, "setAppearance:", 0);
                _panel.Palette = GetPanelPalette();
            }
            ApplyPanelResponse(_panel.Key(PanelKey.Escape));
            Require(!_panel.IsSettingsOpen && _panelVisible, "Escape must leave the Settings view and keep the panel open.");
            RequireWindowMatchesScene();

            var escape = _panel.Key(PanelKey.Escape);
            ApplyPanelResponse(escape);
            Require(!_panelVisible && AppKit.GetBool(_panelWindow, AppKit.Selector("isVisible")) == 0, "Escape must close the panel.");
            return captures.Count == 0 ? "panel verified" : $"panel verified; captures: {string.Join(", ", captures)}";
        }
        finally
        {
            HidePanel();
        }

        void RequireWindowMatchesScene()
        {
            var height = AppKit.GetRect(_panelWindow, "frame").Size.Height;
            Require(Math.Abs(height - _panel!.Scene.Height) < 0.5, $"The panel window ({height}) must resize to the scene ({_panel.Scene.Height}).");
        }

        static void Require(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }
    }

    private void ClickPanelForSmoke(PanelHitTarget target)
    {
        var (x, y) = (target.HitBounds.CenterX, target.HitBounds.CenterY);
        ApplyPanelResponse(_panel!.PointerMoved(x, y));
        ApplyPanelResponse(_panel.PointerDown(x, y));
        ApplyPanelResponse(_panel.PointerUp(x, y));
    }

    private string CapturePanel(string path)
    {
        LayoutPanel();
        CaptureView(_panelView, path);
        return path;
    }

    /// <summary>
    /// Forgets the cached application icons, so a rediscovery (every time the panel opens) picks
    /// up an application installed, moved or updated since; a miss is otherwise cached for good.
    /// </summary>
    private void ReleaseAppIcons()
    {
        foreach (var icon in _appIcons.Values)
        {
            if (icon != 0)
            {
                AppKit.Release(icon);
            }
        }
        _appIcons.Clear();
    }

    private void DisposePanel()
    {
        if (_panelWindow != 0)
        {
            AppKit.Set(_panelWindow, "setDelegate:", 0);
            AppKit.Set(_panelWindow, "orderOut:", 0);
            DisposePanelAccessibility();
            AppKit.SendVoid(_panelView, AppKit.Selector("removeAllToolTips"));
            ReleasePanelTooltipOwners();
            _panelTooltips = [];
            AppKit.Release(_panelView);
            AppKit.Release(_panelWindow);
            _panelWindow = 0;
            _panelView = 0;
        }
        foreach (var font in _panelFonts.Values)
        {
            AppKit.Release(font);
        }
        _panelFonts.Clear();
        ReleaseAppIcons();
        _panelVisible = false;
    }

    private static void RouteView(nint view, Action<MacTrayApplication> callback)
    {
        var application = Volatile.Read(ref s_callbackRoot);
        if (application is null || application._disposed || view != application._panelView)
        {
            return;
        }
        Route(application._target, 0, (app, _) => callback(app));
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte ReturnYes(nint self, nint selector) => 1;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte ReturnYesForEvent(nint self, nint selector, nint nativeEvent) => 1;

    private static unsafe void AddPanelCallbacks(nint callbackClass)
    {
        AddCallback(callbackClass, "statusItemClicked:", &OnStatusItemClicked);
        AddCallback(callbackClass, "windowDidResignKey:", &OnPanelResignKey);
        AddCallback(callbackClass, "panelAccessibilityPress:", &OnPanelAccessibilityPress);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnStatusItemClicked(nint self, nint selector, nint sender)
        => Route(self, sender, static (app, _) => app.OnStatusItemActivated());

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnPanelResignKey(nint self, nint selector, nint notification)
        => Route(self, notification, static (app, _) => app.HidePanel());

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnPanelDraw(nint self, nint selector, AppKit.NativeRect dirty)
        => RouteView(self, static app => app.DrawPanel());

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnPanelAppearance(nint self, nint selector)
        => RouteView(self, static app =>
        {
            app._panel!.Palette = app.GetPanelPalette();
            app.RedrawPanel();
        });

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnPanelMouseMoved(nint self, nint selector, nint nativeEvent)
        => RouteView(self, app => app.HandlePanelEvent("moved", nativeEvent));

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnPanelMouseExited(nint self, nint selector, nint nativeEvent)
        => RouteView(self, app => app.HandlePanelEvent("exited", nativeEvent));

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnPanelMouseDown(nint self, nint selector, nint nativeEvent)
        => RouteView(self, app => app.HandlePanelEvent("down", nativeEvent));

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnPanelMouseUp(nint self, nint selector, nint nativeEvent)
        => RouteView(self, app => app.HandlePanelEvent("up", nativeEvent));

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnPanelScroll(nint self, nint selector, nint nativeEvent)
        => RouteView(self, app => app.HandlePanelEvent("scroll", nativeEvent));

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnPanelKeyDown(nint self, nint selector, nint nativeEvent)
        => RouteView(self, app => app.HandlePanelEvent("key", nativeEvent));
}
