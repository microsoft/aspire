// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Aspire.Tray;

/// <summary>
/// UI Automation provider interfaces and functions used by the flyout panel. The interfaces use
/// source-generated COM, which works under NativeAOT without built-in COM interop. Method order
/// must match the vtables in UIAutomationCore.idl; property getters are declared as the get_
/// methods they are in COM. Interface pointers that come from or go straight to native code
/// (the HWND host provider, a pattern's IUnknown) stay as raw pointers.
/// https://learn.microsoft.com/windows/win32/winauto/uiauto-providersoverview
/// </summary>
internal static unsafe partial class NativeMethods
{
    internal const uint WmGetObject = 0x003D;
    internal const int UiaRootObjectId = -25;
    internal const int UiaAppendRuntimeId = 3;

    // https://learn.microsoft.com/windows/win32/winauto/uiauto-control-pattern-ids
    internal const int UiaInvokePatternId = 10000;
    internal const int UiaTogglePatternId = 10015;

    // https://learn.microsoft.com/windows/win32/winauto/uiauto-automation-element-propids
    internal const int UiaControlTypePropertyId = 30003;
    internal const int UiaNamePropertyId = 30005;
    internal const int UiaHasKeyboardFocusPropertyId = 30008;
    internal const int UiaIsKeyboardFocusablePropertyId = 30009;
    internal const int UiaIsEnabledPropertyId = 30010;
    internal const int UiaAutomationIdPropertyId = 30011;
    internal const int UiaHelpTextPropertyId = 30013;
    internal const int UiaIsOffscreenPropertyId = 30022;
    internal const int UiaFrameworkIdPropertyId = 30024;
    internal const int UiaToggleToggleStatePropertyId = 30086;

    // https://learn.microsoft.com/windows/win32/winauto/uiauto-controltype-ids
    internal const int UiaButtonControlTypeId = 50000;
    internal const int UiaCheckBoxControlTypeId = 50002;
    internal const int UiaTextControlTypeId = 50020;
    internal const int UiaPaneControlTypeId = 50033;

    // https://learn.microsoft.com/windows/win32/winauto/uiauto-event-ids
    internal const int UiaAutomationFocusChangedEventId = 20005;

    // UIA_E_ELEMENTNOTENABLED and UIA_E_ELEMENTNOTAVAILABLE from UIAutomationCoreApi.h.
    internal const int UiaElementNotEnabled = unchecked((int)0x80040200);
    internal const int UiaElementNotAvailable = unchecked((int)0x80040201);

    [LibraryImport("uiautomationcore.dll")]
    internal static partial nint UiaReturnRawElementProvider(nint window, nuint wParam, nint lParam, nint provider);

    [LibraryImport("uiautomationcore.dll")]
    internal static partial int UiaHostProviderFromHwnd(nint window, out nint provider);

    [LibraryImport("uiautomationcore.dll")]
    internal static partial int UiaRaiseAutomationEvent(nint provider, int eventId);

    [LibraryImport("uiautomationcore.dll")]
    internal static partial int UiaRaiseStructureChangedEvent(nint provider, StructureChangeType changeType, int* runtimeId, int runtimeIdLength);

    [LibraryImport("uiautomationcore.dll")]
    internal static partial int UiaRaiseAutomationPropertyChangedEvent(nint provider, int propertyId, NativeVariant oldValue, NativeVariant newValue);

    [LibraryImport("uiautomationcore.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool UiaClientsAreListening();

    [LibraryImport("uiautomationcore.dll")]
    internal static partial int UiaDisconnectProvider(nint provider);

    [LibraryImport("oleaut32.dll")]
    internal static partial nint SafeArrayCreateVector(ushort variantType, int lowerBound, uint elementCount);

    [LibraryImport("oleaut32.dll")]
    internal static partial int SafeArrayAccessData(nint array, out nint data);

    [LibraryImport("oleaut32.dll")]
    internal static partial int SafeArrayUnaccessData(nint array);

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial int ScreenToClient(nint window, ref Point point);

    [Flags]
    internal enum ProviderOptions
    {
        ServerSideProvider = 0x2
    }

    internal enum NavigateDirection
    {
        Parent = 0,
        NextSibling = 1,
        PreviousSibling = 2,
        FirstChild = 3,
        LastChild = 4
    }

    internal enum ToggleState
    {
        Off = 0,
        On = 1
    }

    // StructureChangeType from uiautomationcore.h: ChildAdded 0, ChildRemoved 1,
    // ChildrenInvalidated 2, ChildrenBulkAdded 3, ChildrenBulkRemoved 4, ChildrenReordered 5.
    internal enum StructureChangeType
    {
        ChildrenInvalidated = 2
    }

    /// <summary>
    /// The VARIANT layout, for passing a <see cref="ComVariant"/> by value: <see cref="ComVariant"/>
    /// itself is not accepted by source-generated marshalling while runtime marshalling is enabled.
    /// 16 bytes on 32-bit Windows, 24 bytes on 64-bit Windows.
    /// https://learn.microsoft.com/windows/win32/api/oaidl/ns-oaidl-variant
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeVariant
    {
        public ushort Type;
        public ushort Reserved1;
        public ushort Reserved2;
        public ushort Reserved3;
        public nint Value;
        public nint Record;

        public static NativeVariant From(ComVariant value) => *(NativeVariant*)&value;
    }

    /// <summary>A screen rectangle in physical pixels: left, top, width, height.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct UiaRect
    {
        public double Left;
        public double Top;
        public double Width;
        public double Height;
    }
}

[GeneratedComInterface]
[Guid("d6dd68d1-86fd-4332-8666-9abedea2d24c")]
internal unsafe partial interface IRawElementProviderSimple
{
    NativeMethods.ProviderOptions get_ProviderOptions();

    /// <summary>Returns an AddRef'd IUnknown for the pattern, or null when it is not supported.</summary>
    nint GetPatternProvider(int patternId);

    /// <summary>
    /// Writes the property's VARIANT (VT_EMPTY when the provider has no value) and returns an HRESULT.
    /// The VARIANT is passed as a pointer because ComVariant is not blittable to the source generator.
    /// </summary>
    [PreserveSig]
    int GetPropertyValue(int propertyId, ComVariant* value);

    /// <summary>Returns the HWND host provider for a fragment root, or null for its descendants.</summary>
    nint get_HostRawElementProvider();
}

[GeneratedComInterface]
[Guid("f7063da8-8359-439c-9297-bbc5299a7d87")]
internal partial interface IRawElementProviderFragment
{
    IRawElementProviderFragment? Navigate(NativeMethods.NavigateDirection direction);

    /// <summary>Returns a SAFEARRAY of VT_I4, or null for the fragment root (the host supplies its id).</summary>
    nint GetRuntimeId();

    NativeMethods.UiaRect get_BoundingRectangle();

    nint GetEmbeddedFragmentRoots();

    void SetFocus();

    IRawElementProviderFragmentRoot get_FragmentRoot();
}

[GeneratedComInterface]
[Guid("620ce2a5-ab8f-40a9-86cb-de3c75599b58")]
internal partial interface IRawElementProviderFragmentRoot
{
    IRawElementProviderFragment? ElementProviderFromPoint(double x, double y);

    IRawElementProviderFragment? GetFocus();
}

[GeneratedComInterface]
[Guid("54fcb24b-e18e-47a2-b4d3-eccbe77599a2")]
internal partial interface IInvokeProvider
{
    void Invoke();
}

[GeneratedComInterface]
[Guid("56d00bd0-c4f4-433c-a836-1a52a57e0892")]
internal partial interface IToggleProvider
{
    void Toggle();

    NativeMethods.ToggleState get_ToggleState();
}
