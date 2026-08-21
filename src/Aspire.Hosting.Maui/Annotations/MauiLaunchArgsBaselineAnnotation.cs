// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Maui.Annotations;

/// <summary>
/// Captures the pristine <see cref="ProjectLaunchArgsOverrideAnnotation"/> arguments before any
/// launch-argument callbacks run, so re-applying the callbacks stays idempotent across restarts.
/// </summary>
internal sealed class MauiLaunchArgsBaselineAnnotation(IReadOnlyList<string> arguments) : IResourceAnnotation
{
    /// <summary>
    /// Gets the untouched launch arguments captured before the first callback pass.
    /// </summary>
    public IReadOnlyList<string> Arguments { get; } = arguments.ToArray();
}
