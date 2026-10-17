// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.Hosting.Native.Server;

namespace Aspire.Hosting.Native.Core.Tests;

public class NativeServerOptionsTests
{
    [Fact]
    public void SourceGeneratedOptionsApplyExplicitBudgetsAndCapacity()
    {
        var options = JsonSerializer.Deserialize("""
            {"controllerStartupTimeout":"00:03:00","cleanupTimeout":"00:01:30",
             "maximumConcurrentRequests":7,"retainedAppHostLogEntries":13,
             "runtime":{"workloadStartupTimeout":"00:05:00","observationInterval":"00:00:01",
                        "retainedResourceLogEntries":5,"maximumPendingRequestsPerResource":3}}
            """, NativeServerJsonContext.Default.NativeServerOptions)!;
        options.Validate();
        Assert.Equal(TimeSpan.FromMinutes(3), options.ControllerStartupTimeout);
        Assert.Equal(TimeSpan.FromSeconds(90), options.CleanupTimeout);
        Assert.Equal(7, options.MaximumConcurrentRequests);
        Assert.Equal(13, options.RetainedAppHostLogEntries);
        Assert.Equal(TimeSpan.FromMinutes(5), options.Runtime.WorkloadStartupTimeout);
        Assert.Equal(TimeSpan.FromSeconds(1), options.Runtime.ObservationInterval);
        Assert.Equal(5, options.Runtime.RetainedResourceLogEntries);
        Assert.Equal(3, options.Runtime.MaximumPendingRequestsPerResource);
    }

    [Theory]
    [InlineData("""{"maximumConcurrentRequests":0}""")]
    [InlineData("""{"cleanupTimeout":"00:00:00"}""")]
    [InlineData("""{"runtime":{"observationInterval":"00:05:00"}}""")]
    [InlineData("""{"runtime":{"retainedResourceLogEntries":0}}""")]
    [InlineData("""{"runtime":{"maximumPendingRequestsPerResource":0}}""")]
    public void InvalidPoliciesAreRejected(string json)
    {
        var options = JsonSerializer.Deserialize(json, NativeServerJsonContext.Default.NativeServerOptions)!;
        Assert.Throws<ArgumentException>(options.Validate);
    }

    [Fact]
    public void UnknownAndNullConfigurationFailInsteadOfFallingBack()
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("""{"cleanupTimout":"00:01:00"}""",
            NativeServerJsonContext.Default.NativeServerOptions));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("""{"runtime":null}""",
            NativeServerJsonContext.Default.NativeServerOptions));
    }
}
