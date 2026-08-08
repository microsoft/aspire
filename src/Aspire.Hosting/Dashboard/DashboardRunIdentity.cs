// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Dashboard;

internal sealed class DashboardRunIdentity
{
    public string? RunId { get; set; }

    public bool IsUserSpecified { get; init; }
}