// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Utils;

namespace Aspire.Cli.Tests.TestServices;

internal sealed class TestCliUpdateNotifier : ICliUpdateNotifier
{
    public bool NotifyWasCalled { get; private set; }

    public Func<bool>? IsUpdateAvailableCallback { get; set; }

    public Func<DirectoryInfo, CancellationToken, Task>? CheckForCliUpdatesAsyncCallback { get; set; }

    public Action? NotifyIfUpdateAvailableCallback { get; set; }

    public Task CheckForCliUpdatesAsync(DirectoryInfo workingDirectory, CancellationToken cancellationToken)
    {
        if (CheckForCliUpdatesAsyncCallback is not null)
        {
            return CheckForCliUpdatesAsyncCallback(workingDirectory, cancellationToken);
        }

        return Task.CompletedTask;
    }

    public void NotifyIfUpdateAvailable()
    {
        NotifyWasCalled = true;
        NotifyIfUpdateAvailableCallback?.Invoke();
    }

    public bool IsUpdateAvailable()
    {
        return IsUpdateAvailableCallback?.Invoke() ?? false;
    }
}
