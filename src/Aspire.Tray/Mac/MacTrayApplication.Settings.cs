// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Aspire.Tray;

internal sealed partial class MacTrayApplication
{
    private nint _applicationMenu;
    private nint _settingsWindow;
    private nint _startupCheckbox;
    private nint _startupStatus;
    private nint _settingsAbout;
    private nint _settingsDocumentation;
    private bool _settingsRequested;

    private nint AddSettingsItem(nint menu)
    {
        var item = AddItem(menu, "Settings\u2026", "showSettings:", enabled: true);
        AppKit.Set(item, "setKeyEquivalent:", AppKit.String(","));
        AppKit.Set(item, "setKeyEquivalentModifierMask:", 1 << 20);
        return item;
    }

    private void CreateApplicationMenu()
    {
        // The status menu's shortcuts work during tracking. A main menu also makes
        // Command-comma available while Settings has focus, without a global monitor.
        _applicationMenu = AppKit.Get(AppKit.Class("NSMenu"), "new");
        var applicationItem = AddItem(_applicationMenu, "Aspire", null, enabled: true);
        var submenu = AppKit.Get(AppKit.Class("NSMenu"), "new");
        try
        {
            AppKit.SendBool(submenu, AppKit.Selector("setAutoenablesItems:"), 0);
            AddSettingsItem(submenu);
            AddSeparator(submenu);
            var quit = AddItem(submenu, "Quit Aspire", "quit:", enabled: true);
            AppKit.Set(quit, "setKeyEquivalent:", AppKit.String("q"));
            AppKit.Set(quit, "setKeyEquivalentModifierMask:", 1 << 20);
            AppKit.Set(applicationItem, "setSubmenu:", submenu);
            AppKit.Set(_application, "setMainMenu:", _applicationMenu);
        }
        finally
        {
            AppKit.Release(submenu);
        }
    }

    private void ShowSettings()
    {
        VerifyUIThread();
        _settingsRequested = true;
        if (_openMenus.Count != 0 || _modalDepth != 0)
        {
            RequestRefresh();
            return;
        }
        ShowPendingSettings();
    }

    private void ShowPendingSettings()
    {
        if (!_settingsRequested || _openMenus.Count != 0 || _modalDepth != 0 || _quitting)
        {
            return;
        }
        _settingsRequested = false;
        if (_settingsWindow == 0)
        {
            CreateSettingsWindow();
        }
        AppKit.Set(_settingsWindow, "setTitle:", AppKit.String(_interactiveSmoke ? "Aspire Tray Preview Settings" : "Aspire Tray Settings"));
        AppKit.Set(_settingsAbout, "setStringValue:", AppKit.String(SettingsAboutText));
        ReloadStartupSettings();
        AppKit.Set(_settingsWindow, "makeKeyAndOrderFront:", 0);
        if (AppKit.Supports(_application, "activate"))
        {
            AppKit.SendVoid(_application, AppKit.Selector("activate"));
        }
        else
        {
            AppKit.SendBool(_application, AppKit.Selector("activateIgnoringOtherApps:"), 1);
        }
    }

    private void CreateSettingsWindow()
    {
        // Titled and closable, with buffered backing. Retain the window across close:
        // AppKit otherwise releases it while our native control handles still refer to it.
        // https://developer.apple.com/documentation/appkit/nswindow/isreleasedwhenclosed
        _settingsWindow = AppKit.CreateWindow(AppKit.Get(AppKit.Class("NSWindow"), "alloc"),
            AppKit.Selector("initWithContentRect:styleMask:backing:defer:"),
            new(new(0, 0), new(540, 460)), 3, 2, 0);
        if (_settingsWindow == 0)
        {
            throw new InvalidOperationException("Could not create the Settings window.");
        }
        AppKit.SendBool(_settingsWindow, AppKit.Selector("setReleasedWhenClosed:"), 0);
        var content = AppKit.Get(_settingsWindow, "contentView");
        AddSettingsLabel(content, "General", new(new(24, 410), new(492, 26)), heading: true);
        _startupCheckbox = AppKit.SendThreePointers(AppKit.Class("NSButton"),
            AppKit.Selector("checkboxWithTitle:target:action:"),
            AppKit.String("Launch Aspire Tray when I sign in"), _target, AppKit.Selector("changeStartup:"));
        AppKit.SetRect(_startupCheckbox, AppKit.Selector("setFrame:"), new(new(24, 376), new(492, 26)));
        AppKit.Set(content, "addSubview:", _startupCheckbox);
        AddSettingsLabel(content, "Only the tray starts at sign-in. AppHosts are not started.",
            new(new(44, 346), new(472, 24)));
        _startupStatus = AddSettingsLabel(content, "", new(new(44, 198), new(472, 142)));
        AppKit.SendBool(_startupStatus, AppKit.Selector("setSelectable:"), 1);
        AddSettingsLabel(content, "About", new(new(24, 162), new(492, 26)), heading: true);
        _settingsAbout = AddSettingsLabel(content, SettingsAboutText, new(new(24, 66), new(492, 90)));
        AppKit.SendBool(_settingsAbout, AppKit.Selector("setSelectable:"), 1);
        _settingsDocumentation = AppKit.SendThreePointers(AppKit.Class("NSButton"),
            AppKit.Selector("buttonWithTitle:target:action:"),
            AppKit.String("Documentation - https://aspire.dev"), _target, AppKit.Selector("openDocumentation:"));
        AppKit.SetRect(_settingsDocumentation, AppKit.Selector("setFrame:"), new(new(20, 22), new(320, 32)));
        AppKit.Set(content, "addSubview:", _settingsDocumentation);
        AppKit.SendVoid(_settingsWindow, AppKit.Selector("center"));
    }

    private static nint AddSettingsLabel(nint content, string text, AppKit.NativeRect frame, bool heading = false)
    {
        var label = AppKit.Get(AppKit.Class("NSTextField"), "wrappingLabelWithString:", AppKit.String(text));
        AppKit.SetRect(label, AppKit.Selector("setFrame:"), frame);
        if (heading)
        {
            AppKit.Set(label, "setFont:", AppKit.SendDouble(AppKit.Class("NSFont"), AppKit.Selector("boldSystemFontOfSize:"), 15));
        }
        AppKit.Set(content, "addSubview:", label);
        return label;
    }

    private string SettingsAboutText
    {
        get
        {
            var assembly = Assembly.GetExecutingAssembly();
            var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "Development";
            var build = assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version
                ?? assembly.GetName().Version?.ToString() ?? "Development build";
            return $"Aspire Tray\nVersion {version}\nBuild {build}\n"
                + (_interactiveSmoke ? "Preview: fake AppHosts and startup settings only." : "An experimental companion for Aspire.");
        }
    }

    private void ReloadStartupSettings(string? error = null)
    {
        try
        {
            var state = _startupSettings.Read();
            AppKit.SendBool(_startupCheckbox, AppKit.Selector("setAllowsMixedState:"), 0);
            AppKit.Set(_startupCheckbox, "setState:", state.Enabled ? 1 : 0);
            SetEnabled(_startupCheckbox, state.Enabled || state.CanEnable);
            var status = state.Enabled ? "Launch at sign-in is on." : "Launch at sign-in is off.";
            if (!state.CanEnable)
            {
                status += " Enabling is unavailable.";
            }
            SetStartupStatus(string.Join("\n", new[] { error, status, state.Detail }.Where(text => !string.IsNullOrWhiteSpace(text))));
        }
        catch (Exception ex)
        {
            LogFailure("Unable to read launch-at-sign-in settings", ex);
            // Mixed/disabled is explicitly unknown, never an unchecked success fallback.
            AppKit.SendBool(_startupCheckbox, AppKit.Selector("setAllowsMixedState:"), 1);
            AppKit.Set(_startupCheckbox, "setState:", -1);
            SetEnabled(_startupCheckbox, false);
            SetStartupStatus(string.Join("\n", new[] { error, $"Launch-at-sign-in status is unavailable: {ex.Message}", "Close and reopen Settings to retry." }
                .Where(text => text is not null)));
        }
    }

    private void SetStartupStatus(string text)
        => AppKit.Set(_startupStatus, "setStringValue:", AppKit.String(text));

    private void ChangeStartup(nint sender)
    {
        if (sender != _startupCheckbox || _quitting || _openMenus.Count != 0 || _modalDepth != 0)
        {
            return;
        }
        string? error = null;
        try
        {
            var enabled = AppKit.Get(sender, "state") == 1;
            // Registration availability may have changed while this modeless window was open.
            // Disabling an existing registration remains possible even when enabling is not.
            var current = _startupSettings.Read();
            if (enabled && !current.CanEnable)
            {
                error = "Unable to enable launch at sign-in. Enabling is unavailable.";
            }
            else
            {
                if (_startupSettings.SetEnabled(enabled).Enabled != enabled)
                {
                    error = "The launch-at-sign-in change could not be confirmed. The current registration is shown below.";
                }
            }
        }
        catch (Exception ex)
        {
            LogFailure("Unable to change launch-at-sign-in settings", ex);
            error = $"Unable to change launch at sign-in: {ex.Message}";
        }
        ReloadStartupSettings(error);
    }

    private void CloseSettings()
    {
        _settingsRequested = false;
        if (_settingsWindow != 0)
        {
            AppKit.SendVoid(_settingsWindow, AppKit.Selector("close"));
        }
    }

    private void DisposeSettings()
    {
        CloseSettings();
        AppKit.Set(_startupCheckbox, "setTarget:", 0);
        AppKit.Set(_settingsDocumentation, "setTarget:", 0);
        AppKit.Release(_settingsWindow);
        _settingsWindow = _startupCheckbox = _startupStatus = _settingsAbout = _settingsDocumentation = 0;
        if (_applicationMenu != 0)
        {
            AppKit.Set(_application, "setMainMenu:", 0);
            AppKit.Release(_applicationMenu);
            _applicationMenu = 0;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnShowSettings(nint self, nint selector, nint sender)
        => Route(self, sender, static (app, _) => app.RunAction(app.ShowSettings));

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnChangeStartup(nint self, nint selector, nint sender)
        => Route(self, sender, static (app, item) => app.ChangeStartup(item));
}
