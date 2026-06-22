// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Specifies when a hidden resource should be omitted from default resource lists.
/// </summary>
public enum HiddenBehavior
{
    /// <summary>
    /// Always hide the resource from default resource lists.
    /// </summary>
    Always,

    /// <summary>
    /// Hide the resource from default resource lists only after it completes successfully.
    /// </summary>
    OnCompletion
}

/// <summary>
/// Represents visibility metadata that hides a resource from default resource lists.
/// </summary>
/// <param name="behavior">Specifies when the resource should be hidden.</param>
/// <remarks>
/// Hidden resources can still be accessed directly by name or included explicitly by tooling that supports showing hidden resources.
/// </remarks>
public sealed class HiddenAnnotation(HiddenBehavior behavior) : IResourceAnnotation
{
    /// <summary>
    /// Gets when the resource should be hidden from default resource lists.
    /// </summary>
    public HiddenBehavior Behavior { get; } = behavior;

    /// <summary>
    /// Gets the exit codes that are treated as successful completion when <see cref="Behavior"/> is <see cref="HiddenBehavior.OnCompletion"/>.
    /// </summary>
    public List<int> SuccessfulExitCodes { get; init; } = [0];
}
