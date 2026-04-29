// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting;

/// <summary>
/// Selects how Aspire manages the browser process created for tracked browser sessions.
/// </summary>
public enum BrowserProcessLifetime
{
    /// <summary>
    /// Create the browser when a tracked browser session starts and terminate it when the AppHost process shuts down.
    /// </summary>
    /// <remarks>
    /// This is the default for pipe-backed tracked browsers. The private CDP pipe cannot be reattached by a later AppHost,
    /// so Aspire treats the browser process as owned by the AppHost and uses best-effort process cleanup.
    /// </remarks>
    Session,

    /// <summary>
    /// Leave the pipe-created browser process running after the AppHost process shuts down.
    /// </summary>
    /// <remarks>
    /// Browser logs stop when the AppHost exits because the private CDP pipe is gone. The remaining browser process cannot
    /// be adopted by a later AppHost and may need to be closed manually before opening another tracked browser for the
    /// same user data directory.
    /// </remarks>
    Persistent,
}
