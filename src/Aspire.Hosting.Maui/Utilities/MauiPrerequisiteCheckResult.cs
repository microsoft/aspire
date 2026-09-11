// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Maui.Utilities;

/// <summary>
/// Describes the result of a MAUI prerequisite check.
/// </summary>
internal sealed record MauiPrerequisiteCheckResult(bool IsAvailable, string? Details = null)
{
    public static MauiPrerequisiteCheckResult Available { get; } = new(true);

    public static MauiPrerequisiteCheckResult Missing(string details) => new(false, details);
}
