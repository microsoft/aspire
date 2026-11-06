// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Shared.DotnetWatch;

/// <summary>
/// The command line and environment contract between <c>Aspire.Hosting.Dotnet</c> and the watch host
/// (<c>Aspire.Hosting.Dotnet.WatchHost.dll</c>) that ships next to it under <c>tools/dotnet-watch-host/</c>. Both
/// sides compile this file and always ship together in one package, so there is no version handshake.
/// </summary>
internal static class DotnetWatchHostProtocol
{
    /// <summary>File name of the watch host, started with <c>dotnet exec</c>.</summary>
    public const string AssemblyFileName = "Aspire.Hosting.Dotnet.WatchHost.dll";

    /// <summary>
    /// <c>proxy --server &lt;pipe&gt; --entrypoint &lt;project&gt; [--launch-gate &lt;file&gt;] [--working-directory &lt;dir&gt;] -- [application args]</c>:
    /// one watched service replica, launched by DCP.
    /// </summary>
    public const string ProxyCommand = "proxy";

    /// <summary><c>server -- &lt;executable&gt; [arguments]</c>: hosts the watcher's <c>server</c> process.</summary>
    public const string ServerCommand = "server";

    public const string ServerOption = "--server";
    public const string EntryPointOption = "--entrypoint";
    public const string LaunchGateOption = "--launch-gate";
    public const string WorkingDirectoryOption = "--working-directory";

    /// <summary>Process-environment variable naming (not valuing) the variables the proxy forwards to the service.</summary>
    public const string ForwardedEnvironmentNamesVariable = "ASPIRE_DOTNET_WATCH_FORWARD_ENV";

    /// <summary>
    /// Process-environment variable naming variables set only for the watcher (temp directory, build configuration,
    /// build-only environment). The service inherits the watcher's environment, so the proxy resets each of these to
    /// its own value (or unsets it) for the service.
    /// </summary>
    public const string RestoreEnvironmentNamesVariable = "ASPIRE_DOTNET_WATCH_RESTORE_ENV";

    /// <summary>
    /// Process-environment variable with the replica's DCP resource name. The proxy passes it to <c>dotnet run</c> so
    /// the watcher's launch and exit log lines can be attributed to one replica.
    /// </summary>
    public const string InstanceVariable = "ASPIRE_DOTNET_WATCH_INSTANCE";

    /// <summary>Overrides how long the proxy waits to connect to the watcher's resource pipe.</summary>
    public const string ConnectTimeoutVariable = "ASPIRE_DOTNET_WATCH_PROXY_CONNECT_TIMEOUT_SECONDS";

    /// <summary>Overrides how long the server host lets the watcher exit on its own after a stop signal.</summary>
    public const string ShutdownGraceVariable = "ASPIRE_DOTNET_WATCH_SERVER_SHUTDOWN_GRACE_MS";
}
