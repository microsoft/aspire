// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard;
using Aspire.Managed.NuGet.Commands;
using Aspire.TerminalHost;
using Aspire.Shared;
using System.CommandLine;

BundleVersionLease? acquiredBundleLease;
try
{
    acquiredBundleLease = BundleVersionLease.TryAcquireFromEnvironment("aspire-managed", args.FirstOrDefault());
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException or ArgumentException or NotSupportedException)
{
    Console.Error.WriteLine($"Failed to acquire Aspire bundle lease: {ex.Message}");
    return 1;
}

using var bundleLease = acquiredBundleLease;

return args switch
{
    ["dashboard", .. var rest] => await RunDashboard(rest).ConfigureAwait(false),
    ["server", .. var rest] => await RunServer(rest).ConfigureAwait(false),
    ["nuget", .. var rest] => await RunNuGet(rest).ConfigureAwait(false),
    ["terminalhost", .. var rest] => await RunTerminalHost(rest).ConfigureAwait(false),
    _ => ShowUsage()
};

static async Task<int> RunDashboard(string[] args)
{
    var options = new WebApplicationOptions
    {
        Args = args,
        ContentRootPath = AppContext.BaseDirectory
    };

    var app = new DashboardWebApplication(options: options);

    // Tear the dashboard down if the launching CLI dies so a hard-killed `aspire dashboard run` (or the
    // profiling collector, which also launches aspire-managed dashboard) cannot leave an orphaned
    // dashboard process behind. No-op when ASPIRE_CLI_PID is not set (e.g. the dashboard is launched
    // directly), so the embedded/in-process dashboard is unaffected.
    using var shutdownCts = new CancellationTokenSource();
    var parentWatchdog = Aspire.Managed.ParentProcessWatchdog.Start(shutdownCts);
    try
    {
        return await app.RunAsync(shutdownCts.Token).ConfigureAwait(false);
    }
    finally
    {
        if (parentWatchdog is not null)
        {
            await parentWatchdog.DisposeAsync().ConfigureAwait(false);
        }
    }
}

static async Task<int> RunServer(string[] args)
{
    await Aspire.Hosting.RemoteHost.RemoteHostServer.RunAsync(args).ConfigureAwait(false);
    return 0;
}

static async Task<int> RunNuGet(string[] args)
{
    // Tear this helper down if the launching CLI dies so a hung/slow NuGet operation cannot linger as
    // an orphaned aspire-managed process. No-op when ASPIRE_CLI_PID is not set (invoked directly).
    using var operationCts = new CancellationTokenSource();
    var parentWatchdog = Aspire.Managed.ParentProcessWatchdog.Start(operationCts);
    try
    {
        var rootCommand = new RootCommand("Aspire NuGet Helper - Package operations for Aspire CLI bundle");
        rootCommand.Subcommands.Add(SearchCommand.Create());
        rootCommand.Subcommands.Add(RestoreCommand.Create());
        rootCommand.Subcommands.Add(LayoutCommand.Create());
        rootCommand.Subcommands.Add(ManifestCommand.Create());
        return await rootCommand.Parse(args).InvokeAsync(cancellationToken: operationCts.Token).ConfigureAwait(false);
    }
    finally
    {
        if (parentWatchdog is not null)
        {
            await parentWatchdog.DisposeAsync().ConfigureAwait(false);
        }
    }
}

static async Task<int> RunTerminalHost(string[] args)
{
    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        cts.Cancel();
    };

    return await TerminalHostApp.RunAsync(args, cts.Token).ConfigureAwait(false);
}

static int ShowUsage()
{
    Console.Error.WriteLine($"Usage: {AppDomain.CurrentDomain.FriendlyName} <dashboard|server|nuget|terminalhost> [args...]");
    return 1;
}
