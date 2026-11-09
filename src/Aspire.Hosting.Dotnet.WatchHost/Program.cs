// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Dotnet.WatchHost;
using Aspire.Shared;
using Aspire.Shared.DotnetWatch;

// Started by Aspire.Hosting.Dotnet, never by users: `dotnet exec Aspire.Hosting.Dotnet.WatchHost.dll proxy|server ...`.
// The server host re-executes this assembly as the guardian of the watcher process (see ServerHost).
if (ProcessSupervisor.IsSupervisor)
{
    await ProcessSupervisor.RunAsync().ConfigureAwait(false);
    return Environment.ExitCode;
}

return args switch
{
    [DotnetWatchHostProtocol.ProxyCommand, .. var rest] => await ResourceProxy.RunAsync(rest).ConfigureAwait(false),
    [DotnetWatchHostProtocol.ServerCommand, .. var rest] => await ServerHost.RunAsync(rest).ConfigureAwait(false),
    _ => Usage(),
};

static int Usage()
{
    Console.Error.WriteLine($"usage: {DotnetWatchHostProtocol.AssemblyFileName} {DotnetWatchHostProtocol.ProxyCommand}|{DotnetWatchHostProtocol.ServerCommand} ...");
    return ResourceProxy.ExitUsage;
}
