// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting;

/// <summary>
/// Selects the Chromium user data directory used by tracked browser sessions.
/// </summary>
public enum BrowserUserDataMode
{
    /// <summary>
    /// Use the browser's real user data directory so the tracked session behaves like a persistent browser context
    /// with real cookies, sessions, extensions, and profile selection.
    /// </summary>
    /// <remarks>
    /// Aspire can adopt a shared browser only when it previously launched that browser with remote debugging enabled.
    /// If a normal non-debuggable browser is already using the selected user data directory, the tracked session fails
    /// with guidance instead of opening a second browser against the same profile store. Google Chrome also blocks
    /// remote debugging against its default user data directory; use Microsoft Edge or <see cref="Isolated"/> mode when
    /// Chrome is selected.
    /// </remarks>
    Shared,

    /// <summary>
    /// Launch the tracked browser against a temporary user data directory, like a disposable persistent browser
    /// context, so the session starts from clean state and does not affect the user's normal browser profiles.
    /// </summary>
    Isolated,
}
