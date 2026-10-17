// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Aspire.Tray;

internal readonly record struct MenuPalette(uint Background, uint Text, uint Disabled, uint Hover, uint SelectedText, uint Separator)
{
    [SupportedOSPlatform("windows")]
    internal static unsafe MenuPalette Load()
    {
        // Contrast themes take priority over the light/dark application preference.
        // https://learn.microsoft.com/windows/apps/design/accessibility/high-contrast-themes
        var contrast = new NativeMethods.HighContrast { Size = (uint)sizeof(NativeMethods.HighContrast) };
        NativeCallException.Require(NativeMethods.GetHighContrast(0x42, contrast.Size, ref contrast, 0) != 0, "SystemParametersInfoW(high contrast)");
        if ((contrast.Flags & 1) != 0)
        {
            return new(NativeMethods.GetSysColor(4), NativeMethods.GetSysColor(7), NativeMethods.GetSysColor(17),
                NativeMethods.GetSysColor(13), NativeMethods.GetSysColor(14), NativeMethods.GetSysColor(17));
        }

        // Windows stores "Choose your default app mode" as a DWORD: 0 = dark,
        // 1 = light. Missing preferences retain the default light appearance.
        using var preferences = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return preferences?.GetValue("AppsUseLightTheme") is int and 0 ? Dark : Light;
    }

    // GDI COLORREF values are 0x00BBGGRR.
    internal static MenuPalette Light => new(0xF9F9F9, 0x202020, 0x777777, 0xEAEAEA, 0x202020, 0xE0E0E0);

    // Match the Dashboard's dark popup, menu hover, foreground, muted foreground,
    // brand foreground, and stroke tokens in wwwroot/css/design.css. This gives
    // the tray the same purple-gray surfaces and lavender accents as the Dashboard.
    internal static MenuPalette Dark => new(0x413236, 0xFFFFFF, 0x94888C, 0x4E4044, 0xEEAAB9, 0x534549);
}
