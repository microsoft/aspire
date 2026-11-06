// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using Aspire.Hosting.Dotnet.Watch;

namespace Aspire.Hosting.Dotnet.Tests;

internal sealed class TestDotnetWatchToolInstaller : IDotnetWatchToolInstaller
{
    public ConcurrentQueue<(string Version, string Directory, string WorkingDirectory)> Requests { get; } = new();

    public Func<CancellationToken, Task> BeforeInstall { get; set; } = _ => Task.CompletedTask;

    public async Task InstallAsync(string version, string directory, string workingDirectory, CancellationToken cancellationToken)
    {
        Requests.Enqueue((version, directory, workingDirectory));
        await BeforeInstall(cancellationToken);
        DotnetWatchToolTestPayload.Create(directory);
    }
}
