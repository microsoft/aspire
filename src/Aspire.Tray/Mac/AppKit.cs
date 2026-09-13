// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.InteropServices;

namespace Aspire.Tray;

internal static partial class AppKit
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private static nint s_appKit;

    public static void Initialize()
    {
        s_appKit = NativeLibrary.Load("/System/Library/Frameworks/AppKit.framework/AppKit");
    }

    public static nint Class(string name)
    {
        var value = GetClass(name);
        if (value == 0)
        {
            throw new InvalidOperationException($"AppKit class {name} is unavailable.");
        }
        return value;
    }

    public static nint String(string value) => SendUtf8(Class("NSString"), Selector("stringWithUTF8String:"), value);
    public static nint Get(nint receiver, string selector) => Send(receiver, Selector(selector));
    public static nint Get(nint receiver, string selector, nint argument) => SendPointer(receiver, Selector(selector), argument);
    public static void Set(nint receiver, string selector, nint argument) => SendVoidPointer(receiver, Selector(selector), argument);
    public static void Release(nint value) => SendVoid(value, Selector("release"));
    public static nint Constant(string name) => Marshal.ReadIntPtr(NativeLibrary.GetExport(s_appKit, name));
    public static bool Supports(nint receiver, string selector)
        => SendReturningBool(receiver, Selector("respondsToSelector:"), Selector(selector)) != 0;
    public static string Text(nint value)
        => Marshal.PtrToStringUTF8(Get(value, "UTF8String"))
            ?? throw new InvalidOperationException("AppKit returned a missing string.");

    // objc_msgSend must be declared for each actual native signature, not as one variadic function.
    // CGFloat is double on both supported 64-bit macOS ABIs; NSInteger is pointer-sized.
    // See https://developer.apple.com/documentation/objectivec/objc_msgsend
    [LibraryImport(ObjC, EntryPoint = "objc_getClass", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint GetClass(string name);

    [LibraryImport(ObjC, EntryPoint = "sel_registerName", StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint Selector(string name);

    [LibraryImport(ObjC, EntryPoint = "objc_allocateClassPair", StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint AllocateClass(nint superclass, string name, nuint extraBytes);

    [LibraryImport(ObjC, EntryPoint = "class_addMethod", StringMarshalling = StringMarshalling.Utf8)]
    public static partial byte AddMethod(nint cls, nint selector, nint implementation, string types);

    [LibraryImport(ObjC, EntryPoint = "objc_registerClassPair")]
    public static partial void RegisterClass(nint cls);

    [LibraryImport(ObjC, EntryPoint = "objc_autoreleasePoolPush")]
    public static partial nint PushPool();

    [LibraryImport(ObjC, EntryPoint = "objc_autoreleasePoolPop")]
    public static partial void PopPool(nint pool);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    public static partial nint Send(nint receiver, nint selector);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    public static partial void SendVoid(nint receiver, nint selector);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    public static partial nint SendPointer(nint receiver, nint selector, nint argument);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    public static partial void SendVoidPointer(nint receiver, nint selector, nint argument);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint SendUtf8(nint receiver, nint selector, string argument);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    public static partial nint SendThreePointers(nint receiver, nint selector, nint first, nint second, nint third);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    public static partial nint SendTwoPointers(nint receiver, nint selector, nint first, nint second);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    public static partial void SendVoidTwoPointers(nint receiver, nint selector, nint first, nint second);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    public static partial byte PopUpMenu(nint receiver, nint selector, nint positioningItem, NativePoint location, nint view);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    public static partial nint SendInt64(nint receiver, nint selector, long argument);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    public static partial long GetInt64(nint receiver, nint selector);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    public static unsafe partial nint CreateArray(nint receiver, nint selector, nint* objects, nuint count);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    public static partial nint SendDouble(nint receiver, nint selector, double argument);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    public static partial nint SendTwoDoubles(nint receiver, nint selector, double first, double second);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    public static partial void SetDoubleForKey(nint receiver, nint selector, double value, nint key);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    public static partial void SendBool(nint receiver, nint selector, byte argument);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    public static partial byte SendReturningBool(nint receiver, nint selector, nint argument);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    public static partial byte GetBool(nint receiver, nint selector);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    public static partial void SendSize(nint receiver, nint selector, NativeSize size);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    public static partial nint CreateTimer(nint receiver, nint selector, double seconds, nint target, nint action, nint userInfo, byte repeats);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    public static unsafe partial nint CreateData(nint receiver, nint selector, byte* bytes, nuint length);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    public static partial nint CreateEvent(nint receiver, nint selector, nuint type, NativePoint location,
        nuint modifierFlags, double timestamp, nint windowNumber, nint context, short subtype, nint data1, nint data2);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    public static partial void PostEvent(nint receiver, nint selector, nint nativeEvent, byte atStart);

    [LibraryImport(CoreFoundation)]
    public static partial nint CFRunLoopGetMain();

    [LibraryImport(CoreFoundation)]
    public static partial nint CFRunLoopCopyCurrentMode(nint runLoop);

    [LibraryImport(CoreFoundation)]
    public static partial nint CFRetain(nint value);

    [LibraryImport(CoreFoundation)]
    public static partial void CFRelease(nint value);

    [LibraryImport(CoreFoundation)]
    public static partial nint CFRunLoopSourceCreate(nint allocator, nint order, ref RunLoopSourceContext context);

    [LibraryImport(CoreFoundation)]
    public static partial void CFRunLoopAddSource(nint runLoop, nint source, nint mode);

    [LibraryImport(CoreFoundation)]
    public static partial void CFRunLoopRemoveSource(nint runLoop, nint source, nint mode);

    [LibraryImport(CoreFoundation)]
    public static partial byte CFRunLoopContainsSource(nint runLoop, nint source, nint mode);

    [LibraryImport(CoreFoundation)]
    public static partial void CFRunLoopSourceSignal(nint source);

    [LibraryImport(CoreFoundation)]
    public static partial void CFRunLoopSourceInvalidate(nint source);

    [LibraryImport(CoreFoundation)]
    public static partial void CFRunLoopWakeUp(nint runLoop);

    // CFIndex is pointer-sized; all function pointers are null except the C perform callback.
    // https://developer.apple.com/documentation/corefoundation/cfrunloopsourcecontext
    [StructLayout(LayoutKind.Sequential)]
    internal struct RunLoopSourceContext
    {
        public nint Version;
        public nint Info;
        public nint Retain;
        public nint Release;
        public nint CopyDescription;
        public nint Equal;
        public nint Hash;
        public nint Schedule;
        public nint Cancel;
        public nint Perform;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal readonly record struct NativeSize(double Width, double Height);

    [StructLayout(LayoutKind.Sequential)]
    internal readonly record struct NativePoint(double X, double Y);
}
