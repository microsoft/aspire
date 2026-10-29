// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Tray;

/// <summary>
/// What the panel's Settings view shows besides the values in <see cref="TrayViewState"/>. Hosts
/// read the startup registration (which does I/O) and pass it in, so the panel itself never
/// touches it.
/// </summary>
/// <param name="Startup">The launch-at-sign-in registration, or null when it could not be read.</param>
/// <param name="StartupError">A read or write failure to show under the toggle.</param>
/// <param name="Preview">True for the interactive smoke preview, which uses fake AppHosts.</param>
internal sealed record PanelSettings(TrayStartupState? Startup, string? StartupError, bool Preview)
{
    /// <summary>Reads the startup registration. A failure is shown in the view, never thrown.</summary>
    public static PanelSettings Read(ITrayStartupSettings settings, bool preview, Action<Exception>? logFailure = null,
        string? error = null)
    {
        try
        {
            return new(settings.Read(), error, preview);
        }
        catch (Exception ex)
        {
            logFailure?.Invoke(ex);
            return new(null, TraySettingsText.GetReadError(ex, error), preview);
        }
    }

    /// <summary>
    /// Applies the panel's launch-at-sign-in toggle and returns the registration as it is
    /// afterwards, with any failure as the view's error.
    /// </summary>
    public static PanelSettings SetLaunchAtSignIn(ITrayStartupSettings settings, bool enabled, bool preview,
        Action<Exception>? logFailure = null)
    {
        string? error = null;
        try
        {
            // Registration availability may have changed since the view was drawn. Disabling an
            // existing registration remains possible even when enabling is not.
            var current = settings.Read();
            if (enabled && !current.CanEnable)
            {
                error = TraySettingsText.StableNativeInstallationRequired;
            }
            else if (settings.SetEnabled(enabled).Enabled != enabled)
            {
                error = "The launch-at-sign-in change could not be confirmed.";
            }
        }
        catch (Exception ex)
        {
            logFailure?.Invoke(ex);
            error = TraySettingsText.GetWriteError(ex);
        }
        return Read(settings, preview, logFailure, error);
    }
}
