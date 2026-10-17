// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Aspire.Tray;

internal sealed unsafe partial class TrayApplication
{
    private string? _messageDetail;
    private bool _messageConfirmation;

    private nint ShowModernMessage(string title, string detail, bool confirmation)
    {
        _messageDetail = detail;
        _messageConfirmation = confirmation;
        try
        {
            var template = CreateDialogTemplate(title, 310, 140, 10);
            fixed (byte* pointer = template)
            {
                var result = NativeMethods.DialogBoxIndirectParam(_module, pointer, _modalOwner, &MessageDialogProcedure, 0);
                NativeCallException.Require(result != -1, "DialogBoxIndirectParamW(message)");
                if (_callbackFailure is not null)
                {
                    throw _callbackFailure;
                }
                return result;
            }
        }
        finally
        {
            _messageDetail = null;
        }
    }

    private nint HandleMessageDialog(nint dialog, uint message, nuint wParam, nint lParam)
    {
        if (PaintDialog(dialog, message, wParam, lParam, out var painted))
        {
            return painted;
        }
        switch (message)
        {
            case NativeMethods.WmInitDialog:
                var units = new NativeMethods.Rect { Right = 278, Bottom = 100 };
                NativeMethods.MapDialogRect(dialog, ref units);
                var dc = NativeMethods.GetDC(dialog);
                int detailHeight;
                var previous = NativeMethods.SelectObject(dc, NativeMethods.SendMessage(dialog, 0x31, 0, 0));
                try
                {
                    var measured = new NativeMethods.Rect { Right = units.Right };
                    NativeMethods.DrawText(dc, _messageDetail!, _messageDetail!.Length, ref measured, 0x10 | 0x400 | 0x800);
                    detailHeight = Math.Max(44, (measured.Bottom * 100 + units.Bottom - 1) / units.Bottom + 4);
                }
                finally
                {
                    NativeMethods.SelectObject(dc, previous);
                    NativeMethods.ReleaseDC(dialog, dc);
                }
                NativeCallException.Require(NativeMethods.GetWindowRect(dialog, out var window) != 0, "GetWindowRect(message)");
                NativeCallException.Require(NativeMethods.GetClientRect(dialog, out var client) != 0, "GetClientRect(message)");
                var monitor = new NativeMethods.MonitorInfo { Size = (uint)sizeof(NativeMethods.MonitorInfo) };
                NativeCallException.Require(NativeMethods.GetMonitorInfo(NativeMethods.MonitorFromWindow(dialog, 2), ref monitor) != 0,
                    "GetMonitorInfoW(message)");
                var work = _messageWorkAreaForSmoke ?? monitor.Work;
                var frameHeight = window.Bottom - window.Top - client.Bottom;
                // Reserve the margins and buttons before allocating space to wrapped text.
                var availableHeight = (work.Bottom - work.Top - frameHeight) * 100 / units.Bottom;
                var visibleHeight = Math.Max(1, Math.Min(detailHeight, availableHeight - 72));
                // Read-only scrolling retains the full diagnostic when it cannot fit on screen.
                AddDialogControl(dialog, detailHeight > visibleHeight ? "EDIT" : "STATIC", _messageDetail!,
                    detailHeight > visibleHeight ? 0x200000u | 0x800u | 0x40u | 0x4u : 0x80u,
                    2201, 16, 16, 278, visibleHeight);
                var buttonTop = 16 + visibleHeight + 20;
                if (_messageConfirmation)
                {
                    AddDialogControl(dialog, "BUTTON", "&OK", 0x10000, 1, 166, buttonTop, 60, 24);
                }
                var cancel = AddDialogControl(dialog, "BUTTON", _messageConfirmation ? "Cancel" : "&Close",
                    0x10000 | 1, 2, 234, buttonTop, 60, 24);
                var target = new NativeMethods.Rect { Right = 310, Bottom = buttonTop + 24 + 12 };
                NativeMethods.MapDialogRect(dialog, ref target);
                var height = target.Bottom + frameHeight;
                var width = window.Right - window.Left;
                var x = Math.Clamp(window.Left, work.Left, Math.Max(work.Left, work.Right - width));
                var y = Math.Clamp(window.Top, work.Top, Math.Max(work.Top, work.Bottom - height));
                NativeCallException.Require(NativeMethods.SetWindowPos(dialog, 0, x, y, width, height, 0x4 | 0x10) != 0,
                    "SetWindowPos(message)");
                UpdateDialogAppearance(dialog);
                NativeMethods.SendMessage(dialog, 0x401, 2, 0);
                NativeMethods.SetFocus(cancel);
                return 0;
            case NativeMethods.WmCommand when (wParam & 0xFFFF) is 1 or 2:
                NativeMethods.EndDialog(dialog, (nint)(wParam & 0xFFFF));
                return 1;
            case NativeMethods.WmClose:
                NativeMethods.EndDialog(dialog, 2);
                return 1;
            case NativeMethods.WmSettingChange:
            case NativeMethods.WmSysColorChange:
            case NativeMethods.WmThemeChanged:
                UpdateDialogAppearance(dialog);
                return 0;
            case NativeMethods.WmNcDestroy:
                ReleaseDialogAppearance(dialog);
                return 0;
            default:
                return 0;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint MessageDialogProcedure(nint dialog, uint message, nuint wParam, nint lParam)
    {
        try
        {
            return s_current?.HandleMessageDialog(dialog, message, wParam, lParam) ?? 0;
        }
        catch (Exception ex)
        {
            if (s_current is { } application)
            {
                application._callbackFailure ??= ex;
                Program.Log($"Message dialog failed: {ex.Message}");
            }
            NativeMethods.EndDialog(dialog, 2);
            return 1;
        }
    }
}
