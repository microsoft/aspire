// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting;

/// <summary>
/// Selects the Chromium user data directory used by tracked browser sessions.
/// </summary>
[Experimental("ASPIREBROWSERLOGS001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public enum BrowserUserDataMode
{
    /// <summary>
    /// Use a persistent Aspire-managed user data directory shared across all AppHosts on the machine. State such as
    /// cookies, sign-ins, and extensions persist across runs and are visible to every AppHost using the same browser.
    /// </summary>
    /// <remarks>
    /// The directory lives under a well-known path (for example <c>%LocalAppData%\Aspire\BrowserData\shared\&lt;browser&gt;</c>
    /// on Windows). The directory is persistent across AppHost restarts, but the default tracked browser process is
    /// owned by the AppHost that launched it.
    /// </remarks>
    Shared,

    /// <summary>
    /// Use a persistent Aspire-managed user data directory scoped to the current AppHost project. Each AppHost gets
    /// its own state that persists across runs but is not shared with other AppHosts.
    /// </summary>
    /// <remarks>
    /// The directory is keyed on a stable hash of the AppHost project path (for example
    /// <c>%LocalAppData%\Aspire\BrowserData\isolated\&lt;hash&gt;\&lt;browser&gt;</c> on Windows). The directory persists across
    /// runs, but the default tracked browser process is owned by the AppHost that launched it.
    /// </remarks>
    Isolated,
}
