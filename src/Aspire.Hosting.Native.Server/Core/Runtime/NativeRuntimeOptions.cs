// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Native.Runtime;

/// <summary>Defines workload lifecycle budgets independently of integrations and execution backends.</summary>
internal sealed class NativeRuntimeOptions
{
    public TimeSpan WorkloadStartupTimeout { get; set; } = TimeSpan.FromMinutes(2);
    public TimeSpan ObservationInterval { get; set; } = TimeSpan.FromMilliseconds(250);
    public int RetainedResourceLogEntries { get; set; } = 256;
    public int MaximumPendingRequestsPerResource { get; set; } = 64;

    public void Validate()
    {
        if (WorkloadStartupTimeout <= TimeSpan.Zero || WorkloadStartupTimeout.TotalMilliseconds > int.MaxValue ||
            ObservationInterval <= TimeSpan.Zero || ObservationInterval > WorkloadStartupTimeout ||
            RetainedResourceLogEntries is < 1 or > 65536 || MaximumPendingRequestsPerResource is < 1 or > 4096)
        {
            throw new ArgumentException("Runtime intervals must be positive and within the workload startup budget.");
        }
    }
}
