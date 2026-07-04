// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Go;

/// <summary>
/// Options for configuring the headless Delve debug server used by <c>WithDelveServer</c>.
/// </summary>
[AspireExport(ExposeProperties = true)]
public sealed class GoDelveServerOptions
{
    /// <summary>
    /// Gets or sets the TCP port that Delve listens on. Defaults to <c>2345</c>.
    /// </summary>
    public int Port { get; set; } = 2345;

    /// <summary>
    /// Gets or sets a value indicating whether Delve accepts multiple debugger clients. Defaults to <c>true</c>.
    /// </summary>
    /// <remarks>
    /// Keeping this enabled allows a debugger to detach and attach again without restarting the
    /// Go application resource. Without <c>--accept-multiclient</c>, Delve exits when the debugger detaches.
    /// </remarks>
    public bool AcceptMulticlient { get; set; } = true;

    /// <summary>
    /// Gets or sets whether Delve allows connections only from the same operating system user.
    /// When <see langword="null"/>, the <c>--only-same-user</c> flag is not passed and Delve uses its default behavior.
    /// </summary>
    public bool? OnlySameUser { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether Delve continues the debuggee immediately after startup. Defaults to <c>false</c>.
    /// </summary>
    /// <remarks>
    /// Enable this when you want the Go application to run normally under Delve and attach a debugger later.
    /// </remarks>
    public bool ContinueOnStart { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether Delve debug server logging is enabled. Defaults to <c>false</c>.
    /// </summary>
    public bool Log { get; set; }

    /// <summary>
    /// Gets or sets the Delve logging components enabled when <see cref="Log"/> is <see langword="true"/>.
    /// </summary>
    public string LogOutput { get; set; } = string.Empty;
}
