// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Aspire.Tray;

/// <summary>
/// Exposes the custom-drawn flyout to UI Automation (Narrator, NVDA, Windows Voice Access and
/// automation clients). The panel window answers WM_GETOBJECT with a fragment root whose children
/// mirror <see cref="TrayPanelScene.Accessibility"/>: Button elements with the Invoke pattern,
/// CheckBox elements with the Toggle pattern, and Text elements. Elements are kept per
/// <see cref="PanelAccessibleKey"/> across redraws so their runtime ids stay stable, and every
/// action goes through <see cref="TrayPanel"/>, exactly like a click or a key press.
/// UIA calls providers returned from WM_GETOBJECT on the window's own thread, so no locking is needed.
/// https://learn.microsoft.com/windows/win32/winauto/uiauto-serversideprovider
/// </summary>
internal sealed unsafe partial class TrayApplication
{
    // A private message the panel posts to itself to run an Invoke after UIA's call returns.
    private const uint WmPanelAutomationInvoke = 0x8000 + 0xA1; // WM_APP + 0xA1

    private readonly Dictionary<PanelAccessibleKey, PanelAutomationNode> _automationNodes = [];
    private readonly Dictionary<int, PanelAutomationNode> _automationNodesById = [];
    private List<PanelAutomationNode> _automationOrder = [];
    private PanelAutomationRoot? _automationRoot;
    private TrayPanelScene? _automationScene;
    private PanelTargetKey? _automationFocus;
    private int _automationNextId;

    /// <summary>Answers WM_GETOBJECT for the UIA root object; other object ids go to DefWindowProc (MSAA).</summary>
    private nint HandlePanelGetObject(nint window, nuint wParam, nint lParam)
    {
        _automationRoot ??= new(this);
        SyncPanelAutomation();
        var provider = GetSimpleProvider(_automationRoot);
        try
        {
            return NativeMethods.UiaReturnRawElementProvider(window, wParam, lParam, provider);
        }
        finally
        {
            Marshal.Release(provider);
        }
    }

    /// <summary>
    /// Brings the provider elements in line with the current scene and raises the events screen
    /// readers rely on: structure changed when elements come or go (a confirmation strip, the
    /// Settings view), property changed for a flipped toggle or a renamed button, and focus changed
    /// for keyboard focus. Nothing is raised until a client has asked for the tree.
    /// </summary>
    private void SyncPanelAutomation()
    {
        if (_panel is null || _automationRoot is null)
        {
            return;
        }
        var scene = _panel.Scene;
        if (ReferenceEquals(scene, _automationScene))
        {
            return;
        }
        _automationScene = scene;
        var listening = NativeMethods.UiaClientsAreListening();

        var structureChanged = scene.Accessibility.Count != _automationOrder.Count;
        var order = new List<PanelAutomationNode>(scene.Accessibility.Count);
        var changes = new List<(PanelAutomationNode Node, PanelAccessibleElement? Previous)>();
        foreach (var element in scene.Accessibility)
        {
            if (!_automationNodes.TryGetValue(element.Key, out var node))
            {
                node = new(this, ++_automationNextId) { Element = element };
                _automationNodes.Add(element.Key, node);
                _automationNodesById.Add(node.Id, node);
                structureChanged = true;
            }
            else
            {
                changes.Add((node, node.Element));
                node.Element = element;
            }
            structureChanged |= order.Count >= _automationOrder.Count || _automationOrder[order.Count] != node;
            order.Add(node);
        }
        var current = order.ToHashSet();
        foreach (var (key, node) in _automationNodes.Where(pair => !current.Contains(pair.Value)).ToList())
        {
            _automationNodes.Remove(key);
            _automationNodesById.Remove(node.Id);
            node.Removed = true;
            if (listening)
            {
                // Releases the references clients hold, so a removed row is not kept alive by them.
                WithSimpleProvider(node, NativeMethods.UiaDisconnectProvider);
            }
            structureChanged = true;
        }
        _automationOrder = order;
        if (!listening)
        {
            _automationFocus = _panel.FocusedTarget;
            return;
        }

        foreach (var (node, previous) in changes)
        {
            var element = node.Element;
            if (previous?.Checked != element.Checked && element.Role == PanelAccessibleRole.Switch)
            {
                RaisePropertyChanged(node, NativeMethods.UiaToggleToggleStatePropertyId,
                    ComVariant.Create((int)ToToggleState(previous?.Checked)), ComVariant.Create((int)ToToggleState(element.Checked)));
            }
            if (previous is not null && previous.Name != element.Name)
            {
                RaisePropertyChanged(node, NativeMethods.UiaNamePropertyId, ComVariant.Create(previous.Name), ComVariant.Create(element.Name));
            }
        }
        if (structureChanged)
        {
            // The root's runtime id is the one the HWND host provider reports: UiaHwndRuntimeIdBase (42)
            // followed by the window handle.
            var rootId = stackalloc int[2] { 42, (int)_panelWindow };
            var root = GetSimpleProvider(_automationRoot);
            try
            {
                var result = NativeMethods.UiaRaiseStructureChangedEvent(root, NativeMethods.StructureChangeType.ChildrenInvalidated, rootId, 2);
                if (result < 0)
                {
                    Program.Log($"UI Automation structure notification failed (0x{result:X8}).");
                }
            }
            finally
            {
                Marshal.Release(root);
            }
        }
        if (_panel.FocusedTarget != _automationFocus)
        {
            _automationFocus = _panel.FocusedTarget;
            if (GetFocusedAutomationNode() is { } focused)
            {
                WithSimpleProvider(focused, provider => NativeMethods.UiaRaiseAutomationEvent(provider, NativeMethods.UiaAutomationFocusChangedEventId));
            }
        }

        void RaisePropertyChanged(PanelAutomationNode node, int property, ComVariant oldValue, ComVariant newValue)
        {
            try
            {
                WithSimpleProvider(node, provider => NativeMethods.UiaRaiseAutomationPropertyChangedEvent(provider, property,
                    NativeMethods.NativeVariant.From(oldValue), NativeMethods.NativeVariant.From(newValue)));
            }
            finally
            {
                oldValue.Dispose();
                newValue.Dispose();
            }
        }
    }

    private PanelAutomationNode? GetFocusedAutomationNode()
        => _panel?.FocusedTarget is { } focus && _automationNodes.TryGetValue(PanelAccessibleKey.For(focus), out var node) ? node : null;

    private PanelAutomationNode? HitTestAutomation(double screenX, double screenY)
    {
        if (_panel is null || !_panelVisible)
        {
            return null;
        }
        NativeMethods.GetWindowRect(_panelWindow, out var window);
        var x = (screenX - window.Left) / PanelScale;
        var y = (screenY - window.Top) / PanelScale;
        // Later elements sit on top (row buttons follow their row), matching TrayPanel.HitTest.
        for (var i = _automationOrder.Count - 1; i >= 0; i--)
        {
            if (_automationOrder[i].Element.VisibleBounds.Contains(x, y))
            {
                return _automationOrder[i];
            }
        }
        return null;
    }

    private NativeMethods.UiaRect ToScreenRect(PanelRect bounds)
    {
        NativeMethods.GetWindowRect(_panelWindow, out var window);
        var scale = PanelScale;
        return new()
        {
            Left = window.Left + bounds.X * scale,
            Top = window.Top + bounds.Y * scale,
            Width = bounds.Width * scale,
            Height = bounds.Height * scale
        };
    }

    private void InvokeFromAutomation(int id)
    {
        if (_panel is not null && _panelVisible && _automationNodesById.TryGetValue(id, out var node) && node.Element.Target is { } target)
        {
            ApplyPanelResponse(_panel.Invoke(target.Key));
        }
    }

    private void DisposePanelAutomation()
    {
        if (_automationRoot is null)
        {
            return;
        }
        // Tell UIA the window no longer serves providers, then drop every client reference.
        // https://learn.microsoft.com/windows/win32/api/uiautomationcoreapi/nf-uiautomationcoreapi-uiareturnrawelementprovider
        NativeMethods.UiaReturnRawElementProvider(_panelWindow, 0, 0, 0);
        foreach (var node in _automationNodes.Values)
        {
            node.Removed = true;
            WithSimpleProvider(node, NativeMethods.UiaDisconnectProvider);
        }
        WithSimpleProvider(_automationRoot, NativeMethods.UiaDisconnectProvider);
        _automationNodes.Clear();
        _automationNodesById.Clear();
        _automationOrder = [];
        _automationRoot = null;
        _automationScene = null;
    }

    private static NativeMethods.ToggleState ToToggleState(bool? value) => value == true ? NativeMethods.ToggleState.On : NativeMethods.ToggleState.Off;

    /// <summary>
    /// Returns an AddRef'd IRawElementProviderSimple for a provider object. This goes through the
    /// same marshaller (and so the same ComWrappers instance) as the generated interface stubs, so
    /// an element keeps one COM identity whether UIA got it from a stub or from this pointer.
    /// </summary>
    private static nint GetSimpleProvider(IRawElementProviderSimple provider)
        => (nint)ComInterfaceMarshaller<IRawElementProviderSimple>.ConvertToUnmanaged(provider);

    private static void WithSimpleProvider(IRawElementProviderSimple provider, Func<nint, int> call)
    {
        var simple = GetSimpleProvider(provider);
        try
        {
            var result = call(simple);
            if (result < 0)
            {
                Program.Log($"UI Automation notification failed (0x{result:X8}).");
            }
        }
        finally
        {
            Marshal.Release(simple);
        }
    }

    private static ComVariant Variant(bool value) => ComVariant.Create(value);

    private static ComVariant Variant(int value) => ComVariant.Create(value);

    private static ComVariant Variant(string? value) => value is null ? default : ComVariant.Create(value);

    /// <summary>
    /// The fragment root. The HWND host provider supplies the window's own properties (bounds,
    /// runtime id, process); this adds the name, control type and the panel's children.
    /// </summary>
    [GeneratedComClass]
    private sealed partial class PanelAutomationRoot(TrayApplication owner)
        : IRawElementProviderSimple, IRawElementProviderFragment, IRawElementProviderFragmentRoot
    {
        public NativeMethods.ProviderOptions get_ProviderOptions() => NativeMethods.ProviderOptions.ServerSideProvider;

        public nint GetPatternProvider(int patternId) => 0;

        public int GetPropertyValue(int propertyId, ComVariant* value)
        {
            *value = propertyId switch
            {
                NativeMethods.UiaControlTypePropertyId => Variant(NativeMethods.UiaPaneControlTypeId),
                NativeMethods.UiaNamePropertyId => Variant("Aspire"),
                NativeMethods.UiaFrameworkIdPropertyId => Variant("Aspire"),
                _ => default
            };
            return 0;
        }

        public nint get_HostRawElementProvider()
        {
            Marshal.ThrowExceptionForHR(NativeMethods.UiaHostProviderFromHwnd(owner._panelWindow, out var host));
            return host;
        }

        public IRawElementProviderFragment? Navigate(NativeMethods.NavigateDirection direction) => direction switch
        {
            NativeMethods.NavigateDirection.FirstChild => owner._automationOrder.FirstOrDefault(),
            NativeMethods.NavigateDirection.LastChild => owner._automationOrder.LastOrDefault(),
            _ => null
        };

        public nint GetRuntimeId() => 0;

        public NativeMethods.UiaRect get_BoundingRectangle() => default;

        public nint GetEmbeddedFragmentRoots() => 0;

        public void SetFocus()
        {
        }

        public IRawElementProviderFragmentRoot get_FragmentRoot() => this;

        public IRawElementProviderFragment? ElementProviderFromPoint(double x, double y) => owner.HitTestAutomation(x, y);

        public IRawElementProviderFragment? GetFocus() => owner.GetFocusedAutomationNode();
    }

    /// <summary>One element of the panel: a button, a toggle or text.</summary>
    [GeneratedComClass]
    private sealed partial class PanelAutomationNode(TrayApplication owner, int id)
        : IRawElementProviderSimple, IRawElementProviderFragment, IInvokeProvider, IToggleProvider
    {
        public int Id => id;
        public required PanelAccessibleElement Element { get; set; }

        /// <summary>Set once the element left the scene; clients may still hold it.</summary>
        public bool Removed { get; set; }

        private bool IsFocused => Element.Target is { } target && owner._panel?.FocusedTarget == target.Key;

        public NativeMethods.ProviderOptions get_ProviderOptions() => NativeMethods.ProviderOptions.ServerSideProvider;

        public nint GetPatternProvider(int patternId)
        {
            RequireAvailable();
            // An AddRef'd pattern interface pointer doubles as the IUnknown UIA asks for.
            return (patternId, Element.Role) switch
            {
                (NativeMethods.UiaInvokePatternId, PanelAccessibleRole.Button) => (nint)ComInterfaceMarshaller<IInvokeProvider>.ConvertToUnmanaged(this),
                (NativeMethods.UiaTogglePatternId, PanelAccessibleRole.Switch) => (nint)ComInterfaceMarshaller<IToggleProvider>.ConvertToUnmanaged(this),
                _ => 0
            };
        }

        public int GetPropertyValue(int propertyId, ComVariant* value)
        {
            *value = default;
            if (Removed || owner._panel is null)
            {
                return NativeMethods.UiaElementNotAvailable;
            }
            var element = Element;
            *value = propertyId switch
            {
                NativeMethods.UiaControlTypePropertyId => Variant(element.Role switch
                {
                    PanelAccessibleRole.Switch => NativeMethods.UiaCheckBoxControlTypeId,
                    PanelAccessibleRole.Text => NativeMethods.UiaTextControlTypeId,
                    _ => NativeMethods.UiaButtonControlTypeId
                }),
                NativeMethods.UiaNamePropertyId => Variant(element.Name),
                NativeMethods.UiaAutomationIdPropertyId => Variant(element.Target?.Kind.ToString() ?? element.Key.Label),
                NativeMethods.UiaHelpTextPropertyId => Variant(element.Description),
                NativeMethods.UiaIsEnabledPropertyId => Variant(element.Enabled),
                NativeMethods.UiaIsKeyboardFocusablePropertyId => Variant(element.Target is { Enabled: true }),
                NativeMethods.UiaHasKeyboardFocusPropertyId => Variant(IsFocused),
                NativeMethods.UiaIsOffscreenPropertyId => Variant(element.IsOffscreen),
                NativeMethods.UiaFrameworkIdPropertyId => Variant("Aspire"),
                _ => default
            };
            return 0;
        }

        public nint get_HostRawElementProvider() => 0;

        public IRawElementProviderFragment? Navigate(NativeMethods.NavigateDirection direction)
        {
            RequireAvailable();
            var siblings = owner._automationOrder;
            var index = siblings.IndexOf(this);
            return direction switch
            {
                NativeMethods.NavigateDirection.Parent => owner._automationRoot,
                NativeMethods.NavigateDirection.NextSibling when index >= 0 && index + 1 < siblings.Count => siblings[index + 1],
                NativeMethods.NavigateDirection.PreviousSibling when index > 0 => siblings[index - 1],
                _ => null
            };
        }

        public nint GetRuntimeId()
        {
            // UiaAppendRuntimeId tells UIA to prefix the id with the host window's runtime id.
            // https://learn.microsoft.com/windows/win32/api/uiautomationcore/nf-uiautomationcore-irawelementproviderfragment-getruntimeid
            var array = NativeMethods.SafeArrayCreateVector(3, 0, 2); // VT_I4
            if (array == 0)
            {
                Marshal.ThrowExceptionForHR(unchecked((int)0x8007000E)); // E_OUTOFMEMORY
            }
            Marshal.ThrowExceptionForHR(NativeMethods.SafeArrayAccessData(array, out var data));
            ((int*)data)[0] = NativeMethods.UiaAppendRuntimeId;
            ((int*)data)[1] = id;
            Marshal.ThrowExceptionForHR(NativeMethods.SafeArrayUnaccessData(array));
            return array;
        }

        public NativeMethods.UiaRect get_BoundingRectangle()
        {
            RequireAvailable();
            // Report the part visible through the list clip, so the clickable point is on screen;
            // an element scrolled out of view keeps its real position and reports IsOffscreen.
            var element = Element;
            return owner.ToScreenRect(element.IsOffscreen ? element.Bounds : element.VisibleBounds);
        }

        public nint GetEmbeddedFragmentRoots() => 0;

        public void SetFocus()
        {
            RequireAvailable();
            if (Element.Target is { Enabled: true } target && owner._panel is { } panel && owner._panelVisible)
            {
                owner.ApplyPanelResponse(panel.Focus(target.Key));
            }
        }

        public IRawElementProviderFragmentRoot get_FragmentRoot() => owner._automationRoot!;

        public void Invoke()
        {
            RequireActionable();
            // Invoke must not block the client. Run the action after this call returns: the row's
            // "…" menu, for one, tracks in a nested message loop until it closes.
            // https://learn.microsoft.com/windows/win32/api/uiautomationcore/nf-uiautomationcore-iinvokeprovider-invoke
            if (NativeMethods.PostMessage(owner._panelWindow, WmPanelAutomationInvoke, (nuint)id, 0) == 0)
            {
                Marshal.ThrowExceptionForHR(Marshal.GetHRForLastWin32Error());
            }
        }

        public void Toggle()
        {
            RequireActionable();
            // Toggles never open nested UI, so they run now and the next ToggleState read is current.
            owner.ApplyPanelResponse(owner._panel!.Invoke(Element.Target!.Key));
        }

        public NativeMethods.ToggleState get_ToggleState()
        {
            RequireAvailable();
            return ToToggleState(Element.Checked);
        }

        private void RequireAvailable()
        {
            if (Removed || owner._panel is null)
            {
                Marshal.ThrowExceptionForHR(NativeMethods.UiaElementNotAvailable);
            }
        }

        private void RequireActionable()
        {
            RequireAvailable();
            if (!owner._panelVisible)
            {
                // A closed flyout is no longer on screen for the client to act on.
                Marshal.ThrowExceptionForHR(NativeMethods.UiaElementNotAvailable);
            }
            if (Element.Target is not { Enabled: true })
            {
                Marshal.ThrowExceptionForHR(NativeMethods.UiaElementNotEnabled);
            }
        }
    }
}
