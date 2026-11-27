// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;

namespace Aspire.Hosting.Tests.Utils;

public static class ParameterWaitTestHelpers
{
    public static IResourceBuilder<ParameterResource> AddPendingSecretParameter(this IDistributedApplicationBuilder builder, string name)
    {
        var parameter = builder.AddParameter(name, secret: true);
        parameter.Resource.EnsureValueTask();
        return parameter;
    }

    public static async Task AssertWaitingForParameterAsync(this ResourceNotificationService notifications, IResource resource, ParameterResource parameter, CancellationToken cancellationToken)
    {
        var waiting = await notifications.WaitForResourceAsync(resource.Name,
            e => e.Snapshot.State?.Text == KnownResourceStates.UnresolvedParameters, cancellationToken);
        var unresolved = Assert.Single(waiting.Snapshot.Properties, p => p.Name == KnownProperties.Resource.UnresolvedParameters);
        Assert.Equal(new[] { parameter.Name }, Assert.IsType<string[]>(unresolved.Value));
        Assert.False(unresolved.IsSensitive);
    }

    public static ResourceStateSnapshot? AssertParameterWaitCleared(this ResourceNotificationService notifications, IResource resource)
    {
        Assert.True(notifications.TryGetCurrentState(resource.Name, out var resumed));
        Assert.Empty(resumed.Snapshot.Properties.Where(p => p.Name == KnownProperties.Resource.UnresolvedParameters));
        return resumed.Snapshot.State;
    }
}
