// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Dashboard;

// Shared by the AppHost startup pipeline and the resource-service host. Keeping the
// attachment independent of either DI container avoids resolving the web host to wait for it.
internal sealed class DashboardRunState
{
    private readonly TaskCompletionSource _attached = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public string RunId { get; } = Guid.NewGuid().ToString("N");

    public Task WhenAttached => _attached.Task;

    public void MarkAttached() => _attached.TrySetResult();

    public Task WaitForAttachmentAsync(CancellationToken cancellationToken) =>
        _attached.Task.WaitAsync(TimeSpan.FromSeconds(60), cancellationToken);
}
