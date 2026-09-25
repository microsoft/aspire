// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Eventing;
using Aspire.Hosting.Lifecycle;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Tests.Utils;

internal static class EventingTestHelpers
{
    public static async Task SubscribeEventingSubscribersAsync(
        DistributedApplication app,
        CancellationToken cancellationToken = default)
    {
        var eventing = app.Services.GetRequiredService<IDistributedApplicationEventing>();
        var executionContext = app.Services.GetRequiredService<DistributedApplicationExecutionContext>();
        foreach (var subscriber in app.Services.GetServices<IDistributedApplicationEventingSubscriber>())
        {
            await subscriber.SubscribeAsync(eventing, executionContext, cancellationToken);
        }
    }
}
