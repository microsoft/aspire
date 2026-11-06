// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.DotnetWatch;

/// <summary>
/// SPIKE (plan Session 7): environment the CLI projects into the AppHost process for <c>aspire run --watch</c>.
/// </summary>
internal static class DotnetWatchRunEnvironment
{
    // Must match RunConfiguration.WatchEnabled binding (AppHost:Run:WatchEnabled) in Aspire.Hosting.
    public const string WatchEnabledVariable = "AppHost__Run__WatchEnabled";

    // Must match DotnetWatchCoordinator.ProxyPathConfigurationKey in Aspire.Hosting.Dotnet.
    public const string ProxyPathVariable = "ASPIRE_DOTNET_WATCH_PROXY_PATH";

    // Must match DotnetWatchCoordinator.ProxyHostConfigurationKey in Aspire.Hosting.Dotnet.
    public const string ProxyHostVariable = "ASPIRE_DOTNET_WATCH_PROXY_HOST";

    // Must match DotnetWatchCoordinator.ProxyProtocolConfigurationKey in Aspire.Hosting.Dotnet. Bump when hosting starts
    // relying on new proxy/host behavior, so a newer hosting package with an older CLI falls back to a normal run.
    public const string ProxyProtocolVariable = "ASPIRE_DOTNET_WATCH_PROXY_PROTOCOL";

    // 2: __dotnet-watch-server host, proxy --working-directory/--instance/--restore-env.
    // 3: the proxy selects the project with `dotnet run --project`.
    public const int ProxyProtocolVersion = 3;

    public static void Apply(IDictionary<string, string> environment)
    {
        environment[WatchEnabledVariable] = "true";
        if (GetCurrentCliPath() is { } path)
        {
            environment[ProxyPathVariable] = path;
            environment[ProxyProtocolVariable] = ProxyProtocolVersion.ToString(System.Globalization.CultureInfo.InvariantCulture);

            // A framework-dependent CLI must be hosted by the same muxer that runs this CLI (e.g. the repo-local
            // .dotnet/dotnet in dev builds), not whatever `dotnet` is first on PATH.
            if (path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && Environment.ProcessPath is { } muxer)
            {
                environment[ProxyHostVariable] = muxer;
            }
        }
    }

    /// <summary>
    /// The path the hosting layer uses to launch <c>aspire __dotnet-watch-proxy</c>. When the CLI itself runs under
    /// the dotnet muxer (dev builds: <c>dotnet aspire.dll</c>) return the managed entry assembly; hosting maps a
    /// <c>.dll</c> path to <c>dotnet &lt;dll&gt;</c>.
    /// </summary>
    internal static string? GetCurrentCliPath()
    {
        var processPath = Environment.ProcessPath;
        if (processPath is null)
        {
            return null;
        }

        var name = Path.GetFileNameWithoutExtension(processPath);
        if (string.Equals(name, "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            // Not single-file in this case, so the entry assembly sits next to AppContext.BaseDirectory.
            var entryName = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name;
            return entryName is null ? null : Path.Combine(AppContext.BaseDirectory, entryName + ".dll");
        }

        return processPath;
    }
}
