// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Aspire.Tray;

/// <summary>
/// Exposes the custom-drawn flyout to VoiceOver. The panel view is an AXGroup whose children are
/// NSAccessibilityElement instances built from <see cref="TrayPanelScene.Accessibility"/>: buttons,
/// switches (AXCheckBox with the AXSwitch subrole) and static text. Elements are kept per
/// <see cref="PanelAccessibleKey"/> across redraws, so VoiceOver's cursor stays on the same element
/// while the panel repaints for hover or a state update. Presses and focus changes go back
/// through <see cref="TrayPanel"/>, so they behave exactly like a click or a key press.
/// https://developer.apple.com/documentation/appkit/nsaccessibilityelement
/// </summary>
internal sealed partial class MacTrayApplication
{
    private readonly Dictionary<PanelAccessibleKey, PanelAccessibleNode> _panelNodes = [];
    private readonly Dictionary<nint, PanelAccessibleKey> _panelNodeKeys = [];
    private List<nint> _panelNodeOrder = [];
    private TrayPanelScene? _accessibleScene;
    private PanelTargetKey? _accessibleFocus;

    private static unsafe void RegisterPanelAccessibilityClasses(nint viewClass)
    {
        var elementClass = AppKit.AllocateClass(AppKit.Class("NSAccessibilityElement"), "AspireTrayFlyoutElement", 0);
        if (elementClass == 0)
        {
            throw new InvalidOperationException("Could not allocate the flyout accessibility element class.");
        }
        // NSAccessibility protocol overrides. Everything static (role, label, value, frame) is set
        // through the element's setters; only behavior that depends on the live panel is overridden.
        // https://developer.apple.com/documentation/appkit/nsaccessibilityprotocol
        AddMethod(elementClass, "accessibilityPerformPress", (nint)(delegate* unmanaged[Cdecl]<nint, nint, byte>)&OnPanelElementPress, "c@:");
        AddMethod(elementClass, "isAccessibilityFocused", (nint)(delegate* unmanaged[Cdecl]<nint, nint, byte>)&OnPanelElementIsFocused, "c@:");
        AddMethod(elementClass, "setAccessibilityFocused:", (nint)(delegate* unmanaged[Cdecl]<nint, nint, byte, void>)&OnPanelElementSetFocused, "v@:c");
        AppKit.RegisterClass(elementClass);

        // The view answers which child has keyboard focus (VoiceOver asks the application, which
        // asks the key window's first responder) and which child is under the pointer.
        AddMethod(viewClass, "accessibilityFocusedUIElement", (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint>)&OnPanelFocusedElement, "@@:");
        AddMethod(viewClass, "accessibilityHitTest:", (nint)(delegate* unmanaged[Cdecl]<nint, nint, AppKit.NativePoint, nint>)&OnPanelHitTest,
            "@@:{CGPoint=dd}");

        static void AddMethod(nint cls, string selector, nint implementation, string types)
        {
            if (AppKit.AddMethod(cls, AppKit.Selector(selector), implementation, types) == 0)
            {
                throw new InvalidOperationException("Could not register a flyout accessibility method.");
            }
        }
    }

    private void ConfigurePanelViewAccessibility()
    {
        AppKit.SendBool(_panelView, AppKit.Selector("setAccessibilityElement:"), 1);
        AppKit.Set(_panelView, "setAccessibilityRole:", AppKit.Constant("NSAccessibilityGroupRole"));
        AppKit.Set(_panelView, "setAccessibilityLabel:", AppKit.String("Aspire"));
    }

    /// <summary>
    /// Brings the native elements in line with the current scene and posts the notifications
    /// VoiceOver needs: layout changed when elements come or go (a confirmation strip, the
    /// Settings view), value changed for a flipped switch, and focus changed for keyboard focus.
    /// </summary>
    private void SyncPanelAccessibility()
    {
        if (_panel is null || _panelView == 0)
        {
            return;
        }
        var scene = _panel.Scene;
        if (ReferenceEquals(scene, _accessibleScene))
        {
            return;
        }
        _accessibleScene = scene;

        // The children array must also be rebuilt when the same elements come in a new order
        // (a remembered AppHost moving to the top of the recent list), or VoiceOver keeps
        // reading them in the old one.
        var structureChanged = scene.Accessibility.Count != _panelNodes.Count;
        var seen = new HashSet<PanelAccessibleKey>();
        var order = new List<nint>(scene.Accessibility.Count);
        var valueChanged = new List<nint>();
        foreach (var element in scene.Accessibility)
        {
            seen.Add(element.Key);
            if (!_panelNodes.TryGetValue(element.Key, out var node))
            {
                // Text uses the plain class: the subclass's press override would advertise AXPress.
                var nativeClass = element.Role == PanelAccessibleRole.Text ? "NSAccessibilityElement" : "AspireTrayFlyoutElement";
                node = new(AppKit.Get(AppKit.Get(AppKit.Class(nativeClass), "alloc"), "init"));
                AppKit.Set(node.Handle, "setAccessibilityParent:", _panelView);
                AppKit.SendBool(node.Handle, AppKit.Selector("setAccessibilityElement:"), 1);
                _panelNodes.Add(element.Key, node);
                _panelNodeKeys.Add(node.Handle, element.Key);
                structureChanged = true;
            }
            if (node.Element is { } previous && previous.Role == element.Role && previous.Checked != element.Checked)
            {
                valueChanged.Add(node.Handle);
            }
            structureChanged |= order.Count >= _panelNodeOrder.Count || _panelNodeOrder[order.Count] != node.Handle;
            order.Add(node.Handle);
            UpdatePanelNode(node, element, scene.Height);
        }
        _panelNodeOrder = order;
        foreach (var (key, node) in _panelNodes.Where(pair => !seen.Contains(pair.Key)).ToList())
        {
            _panelNodes.Remove(key);
            _panelNodeKeys.Remove(node.Handle);
            AppKit.Release(node.Handle);
            structureChanged = true;
        }

        if (structureChanged)
        {
            unsafe
            {
                var handles = order.ToArray();
                fixed (nint* items = handles)
                {
                    AppKit.Set(_panelView, "setAccessibilityChildren:",
                        AppKit.CreateArray(AppKit.Class("NSArray"), AppKit.Selector("arrayWithObjects:count:"), items, (nuint)handles.Length));
                }
            }
            PostAccessibilityNotification(_panelView, "NSAccessibilityLayoutChangedNotification");
        }
        foreach (var handle in valueChanged)
        {
            PostAccessibilityNotification(handle, "NSAccessibilityValueChangedNotification");
        }
        if (_panel.FocusedTarget != _accessibleFocus)
        {
            _accessibleFocus = _panel.FocusedTarget;
            PostAccessibilityNotification(GetFocusedPanelElement(), "NSAccessibilityFocusedUIElementChangedNotification");
        }
    }

    private static void UpdatePanelNode(PanelAccessibleNode node, PanelAccessibleElement element, double panelHeight)
    {
        var previous = node.Element;
        var previousHeight = node.PanelHeight;
        node.Element = element;
        node.PanelHeight = panelHeight;
        var handle = node.Handle;
        if (previous?.Role != element.Role)
        {
            var (role, subrole) = element.Role switch
            {
                PanelAccessibleRole.Switch => ("NSAccessibilityCheckBoxRole", "NSAccessibilitySwitchSubrole"),
                PanelAccessibleRole.Text => ("NSAccessibilityStaticTextRole", null),
                _ => ("NSAccessibilityButtonRole", (string?)null)
            };
            AppKit.Set(handle, "setAccessibilityRole:", AppKit.Constant(role));
            AppKit.Set(handle, "setAccessibilitySubrole:", subrole is null ? 0 : AppKit.Constant(subrole));
        }
        if (previous?.Name != element.Name || previous.Role != element.Role)
        {
            // Static text is read from its value; controls are read from their label.
            var text = AppKit.String(element.Name);
            AppKit.Set(handle, "setAccessibilityLabel:", element.Role == PanelAccessibleRole.Text ? 0 : text);
            if (element.Role == PanelAccessibleRole.Text)
            {
                AppKit.Set(handle, "setAccessibilityValue:", text);
            }
        }
        if (element.Role == PanelAccessibleRole.Switch && previous?.Checked != element.Checked)
        {
            // AXCheckBox values are 0 (off) and 1 (on).
            AppKit.Set(handle, "setAccessibilityValue:",
                AppKit.SendInt64(AppKit.Class("NSNumber"), AppKit.Selector("numberWithInteger:"), element.Checked == true ? 1 : 0));
        }
        if (previous?.Description != element.Description)
        {
            AppKit.Set(handle, "setAccessibilityHelp:", element.Description is null ? 0 : AppKit.String(element.Description));
        }
        if (previous?.Enabled != element.Enabled)
        {
            AppKit.SendBool(handle, AppKit.Selector("setAccessibilityEnabled:"), element.Enabled ? (byte)1 : (byte)0);
        }
        if (previous?.Bounds != element.Bounds || previousHeight != panelHeight)
        {
            // NSAccessibilityElement converts this frame to screen coordinates through its parent,
            // so moving the window needs no update. It treats the frame as bottom-left based even
            // though the view is flipped, so flip it against the panel height here.
            // An element scrolled out of the list keeps its real position, outside the visible
            // panel; setting focus on it scrolls it into view.
            var bounds = element.Bounds;
            AppKit.SetRect(handle, AppKit.Selector("setAccessibilityFrameInParentSpace:"),
                new(new(bounds.X, panelHeight - bounds.Bottom), new(bounds.Width, bounds.Height)));
        }
    }

    private nint GetFocusedPanelElement()
        => _panel?.FocusedTarget is { } focus && _panelNodes.TryGetValue(PanelAccessibleKey.For(focus), out var node)
            ? node.Handle
            : _panelView;

    private void OnPanelElementPressed(PanelAccessibleKey key)
    {
        if (_panel is null || !_panelVisible || key.Target is not { } target)
        {
            return;
        }
        ApplyPanelResponse(_panel.Invoke(target));
    }

    private void OnPanelElementFocused(PanelAccessibleKey key)
    {
        if (_panel is null || !_panelVisible || key.Target is not { } target)
        {
            return;
        }
        ApplyPanelResponse(_panel.Focus(target));
    }

    private nint HitTestPanelAccessibility(AppKit.NativePoint screenPoint)
    {
        // The point is in screen coordinates with a bottom-left origin.
        var windowPoint = AppKit.ConvertPointFromScreen(_panelWindow, AppKit.Selector("convertPointFromScreen:"), screenPoint);
        var point = AppKit.ConvertPoint(_panelView, AppKit.Selector("convertPoint:fromView:"), windowPoint, 0);
        var scene = _panel!.Scene;
        // Later elements sit on top (row buttons follow their row), matching TrayPanel.HitTest.
        for (var i = scene.Accessibility.Count - 1; i >= 0; i--)
        {
            var element = scene.Accessibility[i];
            if (element.VisibleBounds.Contains(point.X, point.Y) && _panelNodes.TryGetValue(element.Key, out var node))
            {
                return node.Handle;
            }
        }
        return _panelView;
    }

    private void DisposePanelAccessibility()
    {
        foreach (var node in _panelNodes.Values)
        {
            AppKit.Release(node.Handle);
        }
        _panelNodes.Clear();
        _panelNodeKeys.Clear();
        _panelNodeOrder = [];
        _accessibleScene = null;
        _accessibleFocus = null;
    }

    /// <summary>
    /// Reads the panel's accessibility tree back through the same NSAccessibility getters VoiceOver
    /// uses and checks it against the scene: order, roles, names, switch values, screen frames and
    /// the focused element. <paramref name="stage"/> names the view being checked in failures.
    /// </summary>
    private void VerifyPanelAccessibilityForSmoke(string stage)
    {
        var scene = _panel!.Scene;
        Require(AppKit.Text(AppKit.Get(_panelView, "accessibilityRole")) == "AXGroup", "The panel view must be an AXGroup.");
        var children = AppKit.Get(_panelView, "accessibilityChildren");
        var count = children == 0 ? 0 : AppKit.GetInt64(children, AppKit.Selector("count"));
        Require(count == scene.Accessibility.Count, $"The panel exposes {count} accessibility children; the scene has {scene.Accessibility.Count}.");
        var window = AppKit.GetRect(_panelWindow, "frame");
        for (var i = 0; i < scene.Accessibility.Count; i++)
        {
            var element = scene.Accessibility[i];
            var child = AppKit.SendInt64(children, AppKit.Selector("objectAtIndex:"), i);
            var role = AppKit.Text(AppKit.Get(child, "accessibilityRole"));
            var expectedRole = element.Role switch
            {
                PanelAccessibleRole.Switch => "AXCheckBox",
                PanelAccessibleRole.Text => "AXStaticText",
                _ => "AXButton"
            };
            Require(role == expectedRole, $"'{element.Name}' has role {role}, expected {expectedRole}.");
            var value = AppKit.Get(child, "accessibilityValue");
            if (element.Role == PanelAccessibleRole.Text)
            {
                Require(value != 0 && AppKit.Text(value) == element.Name, $"Text '{element.Name}' must be exposed as its value.");
            }
            else
            {
                var label = AppKit.Get(child, "accessibilityLabel");
                Require(label != 0 && AppKit.Text(label) == element.Name, $"'{element.Name}' must be exposed as its label.");
                Require((AppKit.GetBool(child, AppKit.Selector("isAccessibilityEnabled")) != 0) == element.Enabled,
                    $"'{element.Name}' must report enabled = {element.Enabled}.");
            }
            if (element.Role == PanelAccessibleRole.Switch)
            {
                Require(AppKit.Text(AppKit.Get(child, "accessibilitySubrole")) == "AXSwitch", $"'{element.Name}' must use the AXSwitch subrole.");
                Require(value != 0 && AppKit.GetInt64(value, AppKit.Selector("integerValue")) == (element.Checked == true ? 1 : 0),
                    $"Switch '{element.Name}' must report {(element.Checked == true ? "on" : "off")}.");
            }
            // Screen coordinates have a bottom-left origin; the scene is top-left within the window.
            var frame = AppKit.GetRect(child, "accessibilityFrame");
            var expectedX = window.Origin.X + element.Bounds.X;
            var expectedY = window.Origin.Y + window.Size.Height - element.Bounds.Bottom;
            Require(Math.Abs(frame.Origin.X - expectedX) < 1 && Math.Abs(frame.Origin.Y - expectedY) < 1
                && Math.Abs(frame.Size.Width - element.Bounds.Width) < 1 && Math.Abs(frame.Size.Height - element.Bounds.Height) < 1,
                $"'{element.Name}' frame {frame} does not match its bounds {element.Bounds} in the window at {window}.");
        }
        Require(AppKit.Get(_panelView, "accessibilityFocusedUIElement") == GetFocusedPanelElement(),
            $"The focused accessibility element must follow keyboard focus ({_panel.FocusedTarget?.Kind.ToString() ?? "none"}) in {stage}.");

        static void Require(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }
    }

    private static void PostAccessibilityNotification(nint element, string notification)
        => AppKit.PostAccessibilityNotification(element, AppKit.Constant(notification));

    /// <summary>
    /// Runs an accessibility callback for an element of the open panel. Getters must answer
    /// synchronously and must never throw back into AppKit, so failures return the fallback.
    /// </summary>
    private static T QueryPanelAccessibility<T>(nint self, bool isView, T fallback, Func<MacTrayApplication, PanelAccessibleKey, T> query)
    {
        var application = Volatile.Read(ref s_callbackRoot);
        if (application is null || application._disposed || application._panel is null)
        {
            return fallback;
        }
        PanelAccessibleKey key = default;
        if (isView ? self != application._panelView : !application._panelNodeKeys.TryGetValue(self, out key))
        {
            return fallback;
        }
        try
        {
            application.VerifyUIThread();
            return query(application, key);
        }
        catch (Exception ex)
        {
            LogFailure("Flyout accessibility query failed", ex);
            return fallback;
        }
    }

    private static void RoutePanelElement(nint self, Action<MacTrayApplication, PanelAccessibleKey> callback)
    {
        var application = Volatile.Read(ref s_callbackRoot);
        if (application is null || application._disposed || !application._panelNodeKeys.TryGetValue(self, out var key))
        {
            return;
        }
        Route(application._target, 0, (app, _) => callback(app, key));
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte OnPanelElementPress(nint self, nint selector)
    {
        var application = Volatile.Read(ref s_callbackRoot);
        var handled = QueryPanelAccessibility(self, isView: false, false,
            static (app, key) => app._panelVisible && app._panel!.Scene.Targets.Any(target => target.Key == key.Target && target.Enabled));
        if (handled && application is not null)
        {
            // Answer VoiceOver first and act on the next run loop turn, so an action that takes a
            // while (opening an application, writing saved state) never holds VoiceOver's AXPress
            // request. The element is retained until it runs.
            AppKit.PerformSelectorAfterDelay(application._target, AppKit.Selector("performSelector:withObject:afterDelay:"),
                AppKit.Selector("panelAccessibilityPress:"), self, 0);
        }
        return handled ? (byte)1 : (byte)0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnPanelAccessibilityPress(nint self, nint selector, nint element)
        => Route(self, element, static (app, handle) =>
        {
            if (app._panelNodeKeys.TryGetValue(handle, out var key))
            {
                app.OnPanelElementPressed(key);
            }
        });

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte OnPanelElementIsFocused(nint self, nint selector)
        => QueryPanelAccessibility(self, isView: false, false, static (app, key) => key.Target is { } target && app._panel!.FocusedTarget == target)
            ? (byte)1
            : (byte)0;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnPanelElementSetFocused(nint self, nint selector, byte focused)
    {
        if (focused != 0)
        {
            RoutePanelElement(self, static (app, key) => app.OnPanelElementFocused(key));
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static nint OnPanelFocusedElement(nint self, nint selector)
        => QueryPanelAccessibility(self, isView: true, self, static (app, _) => app.GetFocusedPanelElement());

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static nint OnPanelHitTest(nint self, nint selector, AppKit.NativePoint point)
        => QueryPanelAccessibility(self, isView: true, self, (app, _) => app.HitTestPanelAccessibility(point));

    /// <summary>A retained native element and the scene element it currently mirrors.</summary>
    private sealed class PanelAccessibleNode(nint handle)
    {
        public nint Handle { get; } = handle;
        public PanelAccessibleElement? Element { get; set; }
        public double PanelHeight { get; set; }
    }
}
