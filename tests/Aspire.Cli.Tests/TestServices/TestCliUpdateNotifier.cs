// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Utils;

namespace Aspire.Cli.Tests.TestServices;

internal sealed class TestCliUpdateNotifier : ICliUpdateNotifier
{
    public bool NotifyWasCalled { get; private set; }

    public Func<bool>? IsUpdateAvailableCallback { get; set; }

    public Func<DirectoryInfo, CancellationToken, Task<CliVersionStatus>>? GetVersionStatusAsyncCallback { get; set; }

    public Func<DirectoryInfo, CancellationToken, Task>? CheckForCliUpdatesAsyncCallback { get; set; }

    public Action<bool>? NotifyIfUpdateAvailableCallback { get; set; }
    public bool LastIncludeAppHostUpdateCommand { get; private set; }

    public Task CheckForCliUpdatesAsync(DirectoryInfo workingDirectory, CancellationToken cancellationToken)
    {
        if (CheckForCliUpdatesAsyncCallback is not null)
        {
            return CheckForCliUpdatesAsyncCallback(workingDirectory, cancellationToken);
        }

        return Task.CompletedTask;
    }

    public Task<CliVersionStatus> GetVersionStatusAsync(DirectoryInfo workingDirectory, CancellationToken cancellationToken)
    {
        return GetVersionStatusAsyncCallback is not null
            ? GetVersionStatusAsyncCallback(workingDirectory, cancellationToken)
            : Task.FromResult(new CliVersionStatus("13.0.0", null, null));
    }

    public void NotifyIfUpdateAvailable(bool includeAppHostUpdateCommand = false)
    {
        NotifyWasCalled = true;
        LastIncludeAppHostUpdateCommand = includeAppHostUpdateCommand;
        NotifyIfUpdateAvailableCallback?.Invoke(includeAppHostUpdateCommand);
    }

    public bool IsUpdateAvailable()
    {
        return IsUpdateAvailableCallback?.Invoke() ?? false;
    }
}
