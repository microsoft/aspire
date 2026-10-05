// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.DashboardService.Proto.V1;

namespace Aspire.Dashboard.Model.Interaction;

public sealed class InteractionMessageBoxContent
{
    public required string MarkupMessage { get; init; }

    public required MessageIntent Intent { get; init; }
}
