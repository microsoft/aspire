// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Bunit;

namespace Aspire.Dashboard.Components.Tests.Shared;
public abstract class DashboardTestContext : TestContext
{
    public DashboardTestContext()
    {
        // Increase from default 1 second as Helix/GitHub Actions can be slow.
        DefaultWaitTimeout = TimeSpan.FromSeconds(10);
    }
}
