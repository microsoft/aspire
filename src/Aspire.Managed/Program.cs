// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.Diagnostics;
using System.Globalization;
using Aspire.Hosting;
using Aspire.Managed.NuGet.Commands;
using Aspire.Shared;
using Aspire.TerminalHost;

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
    var startInfo = CreateStartInfo(AppContext.BaseDirectory, args);
    if (!File.Exists(startInfo.FileName))
    {
        Console.Error.WriteLine($"Dashboard executable was not found at '{startInfo.FileName}'. Reinstall or rebuild the Aspire bundle.");
        return 1;
    }

    using var process = new Process { StartInfo = startInfo };
    // Legacy callers can launch this compatibility forwarder with a CLI parent identity.
    // Watch that parent as well as having the native Dashboard watch this forwarding process.
    // Without a parent identity, this is a no-op (including Windows callers using kill-on-close jobs).
    using var shutdownCts = new CancellationTokenSource();
    var parentWatchdog = ParentProcessWatchdog.Start(shutdownCts);
    try
    {
        process.Start();
        await process.WaitForExitAsync(shutdownCts.Token).ConfigureAwait(false);
        return process.ExitCode;
    }
    catch (OperationCanceledException) when (shutdownCts.IsCancellationRequested)
    {
        // Cancelling WaitForExitAsync does not stop the child. Keep the watchdog's force-exit
        // backstop armed until cleanup completes in case terminating the process gets stuck.
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
        }

        await process.WaitForExitAsync().ConfigureAwait(false);
        return 0;
    }
    finally
    {
        if (parentWatchdog is not null)
        {
            await parentWatchdog.DisposeAsync().ConfigureAwait(false);
        }
    }

    static ProcessStartInfo CreateStartInfo(string managedDirectory, string[] args)
    {
        // Older AppHosts launch "aspire-managed dashboard". Keep that contract without loading
        // the Dashboard into the managed helper or requiring the AppHost to understand native executables.
        var dashboardDirectory = Path.GetFullPath(Path.Combine(managedDirectory, "..", BundleDiscovery.DashboardDirectoryName));
        var startInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(dashboardDirectory, BundleDiscovery.GetExecutableFileName(BundleDiscovery.DashboardExecutableName)),
            WorkingDirectory = dashboardDirectory,
            UseShellExecute = false
        };

        foreach (var argument in args)
        {
            startInfo.ArgumentList.Add(argument);
        }

        // DCP owns this forwarding process. The native Dashboard must stop if DCP terminates it.
        startInfo.Environment[KnownConfigNames.CliProcessId] = Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
        startInfo.Environment[KnownConfigNames.CliProcessStartedStable] = ProcessStartTimeHelper.GetCurrentProcessStartTimeUnixMilliseconds().ToString(CultureInfo.InvariantCulture);
        startInfo.Environment[KnownConfigNames.CliProcessStarted] = ProcessStartTimeHelper.GetCurrentProcessRuntimeStartTimeUnixSeconds().ToString(CultureInfo.InvariantCulture);

        return startInfo;
    }
}

static async Task<int> RunServer(string[] args)
{
    await Aspire.Hosting.RemoteHost.RemoteHostServer.RunAsync(args).ConfigureAwait(false);
    return 0;
}

static async Task<int> RunNuGet(string[] args)
{
    // Tear this helper down if the launching CLI dies so a hung/slow NuGet operation cannot linger as an
    // orphaned aspire-managed process. No-op when ASPIRE_CLI_PID is not set — either invoked directly, or
    // on Windows where the CLI relies on the kernel kill-on-close job instead (see LayoutProcessRunner).
    using var operationCts = new CancellationTokenSource();
    var parentWatchdog = ParentProcessWatchdog.Start(operationCts);
    try
    {
        var rootCommand = new RootCommand("Aspire NuGet Helper - Package operations for Aspire CLI bundle");
        rootCommand.Subcommands.Add(SearchCommand.Create());
        rootCommand.Subcommands.Add(RestoreCommand.Create());
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
    return await TerminalHostProcessRunner.RunAsync(args).ConfigureAwait(false);
}

static int ShowUsage()
{
    Console.Error.WriteLine($"Usage: {AppDomain.CurrentDomain.FriendlyName} <dashboard|server|nuget|terminalhost> [args...]");
    return 1;
}
