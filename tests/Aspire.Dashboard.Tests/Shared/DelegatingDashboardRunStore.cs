// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.ServiceClient;

namespace Aspire.Dashboard.Tests.Shared;

internal sealed class DelegatingDashboardRunStore(IDashboardRunStore inner) : IDashboardRunStore
{
    public Action? AfterGetCurrentRun { get; set; }
    public Action? AfterGetRunById { get; set; }
    public bool SupportsRunSelection => inner.SupportsRunSelection;
    public IReadOnlyList<DashboardRunDescriptor> GetRuns() => inner.GetRuns();

    public DashboardRunDescriptor GetCurrentRun()
    {
        var run = inner.GetCurrentRun();
        AfterGetCurrentRun?.Invoke();
        return run;
    }

    public DashboardRunDescriptor? GetRunById(string runId, bool onlyCompatible)
    {
        var run = inner.GetRunById(runId, onlyCompatible);
        AfterGetRunById?.Invoke();
        return run;
    }
    public void SetRunPinned(DashboardRunDescriptor run, bool isPinned) => inner.SetRunPinned(run, isPinned);
    public IDisposable? TryAcquireRunLease(DashboardRunDescriptor run) => inner.TryAcquireRunLease(run);
    public void PublishRun() => inner.PublishRun();
    public void PruneExpiredRuns() => inner.PruneExpiredRuns();
}
