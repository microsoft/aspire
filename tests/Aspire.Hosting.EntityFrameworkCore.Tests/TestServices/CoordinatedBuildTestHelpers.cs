// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.EntityFrameworkCore.Tests.TestServices;

internal static class CoordinatedBuildTestHelpers
{
    public static async Task CompleteBuildsAsync(
        IDistributedApplicationBuilder builder,
        DistributedApplication app,
        CancellationToken cancellationToken)
    {
        await ExecuteBeforeStartHooksAsync(app, cancellationToken);

        foreach (var buildResource in builder.Resources.Where(
            resource => resource.Name.StartsWith("__dotnet-project-build", StringComparison.Ordinal)))
        {
            await app.ResourceNotifications.PublishUpdateAsync(
                buildResource,
                snapshot => snapshot with
                {
                    State = KnownResourceStates.Finished,
                    ExitCode = 0,
                });
        }
    }

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "ExecuteBeforeStartHooksAsync")]
    private static extern Task ExecuteBeforeStartHooksAsync(
        DistributedApplication app,
        CancellationToken cancellationToken);
}
