// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Tray;

/// <summary>
/// Describes the current user's launch-at-sign-in registration.
/// </summary>
internal sealed record TrayStartupState(bool Enabled, bool CanEnable, string? Detail);

/// <summary>
/// Reads and updates an opt-in startup registration without starting AppHosts.
/// </summary>
internal interface ITrayStartupSettings
{
    TrayStartupState Read();
    TrayStartupState SetEnabled(bool enabled);
}

/// <summary>
/// Keeps native smoke settings isolated from the user's startup registration.
/// </summary>
internal sealed class MemoryTrayStartupSettings : ITrayStartupSettings
{
    private bool _enabled;

    public TrayStartupState Read() => new(_enabled, true, "Isolated smoke setting; no startup registration is changed.");

    public TrayStartupState SetEnabled(bool enabled)
    {
        _enabled = enabled;
        return Read();
    }
}
