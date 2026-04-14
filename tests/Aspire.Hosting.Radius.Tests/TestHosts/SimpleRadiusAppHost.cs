// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Radius.Tests.TestHosts;

/// <summary>
/// Minimal Radius app host with one container resource and no databases or custom recipes.
/// </summary>
internal static class SimpleRadiusAppHost
{
    public static void Configure(IDistributedApplicationBuilder builder)
    {
        builder.AddRadiusEnvironment();
        builder.AddContainer("api", "myapp/api:latest");
    }
}
