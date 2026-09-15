// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Aspire.Tray;

internal sealed unsafe partial class TrayApplication
{
    private const int StopSuppressionId = 2101;
    private const int StopDetailId = 2102;
    private string? _stopDetail;
    private bool _suppressStopConfirmation;

    private bool ConfirmStop(AppHostInfo host)
    {
        _suppressStopConfirmation = false;
        if (!controller.ConfirmStop)
        {
            return true;
        }
        var previousOwner = _modalOwner;
        _modalOwner = _settingsWindow != 0 ? _settingsWindow : _window;
        _modalDepth++;
        _stopDetail = $"Stop {AppHostPresentation.GetTitle(host)} (PID {host.AppHostPid})?\r\n\r\n"
            + "This stops the AppHost and its running resources.";
        try
        {
            if (smokeSeconds is not null && !_interactiveSmoke)
            {
                var accept = ConfirmForSmoke?.Invoke("Stop AppHost", _stopDetail, NativeMethods.SafeConfirmation)
                    ?? throw new InvalidOperationException("Smoke confirmation handler is missing.");
                _smokeDialog = new(2, accept ? 1 : 2);
            }
            var template = CreateDialogTemplate("Stop AppHost", 310, 126, 9);
            nint result;
            fixed (byte* pointer = template)
            {
                result = NativeMethods.DialogBoxIndirectParam(_module, pointer, _modalOwner, &StopDialogProcedure, 0);
            }
            NativeCallException.Require(result != -1, "DialogBoxIndirectParamW(Stop)");
            if (_callbackFailure is not null)
            {
                throw _callbackFailure;
            }
            if (result != 1 || _quitRequested)
            {
                return false;
            }
            return true;
        }
        finally
        {
            _stopDetail = null;
            _smokeDialog = null;
            DialogReadyForSmoke = null;
            _modalDepth--;
            _modalOwner = previousOwner;
            RequestRefresh();
        }
    }

    private nint HandleStopDialog(nint dialog, uint message, nuint wParam)
    {
        switch (message)
        {
            case NativeMethods.WmInitDialog:
                AddDialogControl(dialog, "STATIC", _stopDetail!, 0, StopDetailId, 12, 12, 286, 48);
                AddDialogControl(dialog, "BUTTON", "&Don't ask again", 0x10000 | 0x3,
                    StopSuppressionId, 12, 68, 286, 16); // BS_AUTOCHECKBOX, initially unchecked.
                AddDialogControl(dialog, "BUTTON", "&Stop AppHost", 0x10000, 1, 150, 98, 80, 18);
                var cancel = AddDialogControl(dialog, "BUTTON", "Cancel", 0x10000 | 0x1, 2, 238, 98, 60, 18);
                NativeMethods.SendMessage(dialog, 0x401, 2, 0); // DM_SETDEFID.
                NativeMethods.SetFocus(cancel);
                return 0;
            case NativeMethods.WmCommand when (wParam & 0xFFFF) is 1 or 2:
                var result = (int)(wParam & 0xFFFF);
                _suppressStopConfirmation = result == 1
                    && NativeMethods.SendMessage(NativeMethods.GetDlgItem(dialog, StopSuppressionId),
                        NativeMethods.BmGetCheck, 0, 0) == 1;
                NativeCallException.Require(NativeMethods.EndDialog(dialog, result) != 0, "EndDialog(Stop)");
                return 1;
            case NativeMethods.WmClose:
                NativeCallException.Require(NativeMethods.EndDialog(dialog, 2) != 0, "EndDialog(Cancel)");
                return 1;
            default:
                return 0;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint StopDialogProcedure(nint dialog, uint message, nuint wParam, nint lParam)
    {
        try
        {
            return s_current?.HandleStopDialog(dialog, message, wParam) ?? 0;
        }
        catch (Exception ex)
        {
            // Never unwind a managed exception through the Win32 modal loop.
            if (s_current is { } application)
            {
                application._callbackFailure ??= ex;
                Program.Log($"Stop dialog failed: {ex.Message}");
            }
            NativeMethods.EndDialog(dialog, 2);
            return 1;
        }
    }
}
