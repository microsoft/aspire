// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard;
using Aspire.Managed.NuGet.Commands;
using System.CommandLine;

return args switch
{
    ["dashboard", .. var rest] => RunDashboard(rest),
    ["server", .. var rest] => await RunServer(rest).ConfigureAwait(false),
    ["nuget", .. var rest] => await RunNuGet(rest).ConfigureAwait(false),
    _ => ShowUsage()
};

static int RunDashboard(string[] args)
{
    var options = new WebApplicationOptions
    {
        Args = args,
        ContentRootPath = AppContext.BaseDirectory
    };

    var app = new DashboardWebApplication(options: options);
    return app.Run();
}

static async Task<int> RunServer(string[] args)
{
    await Aspire.Hosting.RemoteHost.RemoteHostServer.RunAsync(args).ConfigureAwait(false);
    return 0;
}

static async Task<int> RunNuGet(string[] args)
{
    var rootCommand = new RootCommand("Aspire NuGet Helper - Package operations for Aspire CLI bundle");
    rootCommand.Subcommands.Add(SearchCommand.Create());
    rootCommand.Subcommands.Add(RestoreCommand.Create());
    rootCommand.Subcommands.Add(LayoutCommand.Create());
    return await rootCommand.Parse(args).InvokeAsync().ConfigureAwait(false);
}

static int ShowUsage()
{
    Console.Error.WriteLine($"Usage: {AppDomain.CurrentDomain.FriendlyName} <dashboard|server|nuget> [args...]");
    return 1;
}
