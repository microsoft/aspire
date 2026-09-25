// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// The set of well known resource states.
/// </summary>
public static class KnownResourceStates
{
    /// <summary>
    /// The hidden state. Useful for hiding the resource.
    /// </summary>
    [Obsolete("Use CustomResourceSnapshot.IsHidden instead.")]
    public static readonly string Hidden = nameof(Hidden);

    /// <summary>
    /// The starting state. Useful for showing the resource is starting.
    /// </summary>
    public static readonly string Starting = nameof(Starting);

    /// <summary>
    /// The running state. Useful for showing the resource is running.
    /// </summary>
    public static readonly string Running = nameof(Running);

    /// <summary>
    /// The failed to start state. Useful for showing the resource has failed to start successfully.
    /// </summary>
    public static readonly string FailedToStart = nameof(FailedToStart);

    /// <summary>
    /// The runtime unhealthy state. Indicates that a resource could not be started because the runtime is not in a healthy state.
    /// </summary>
    public static readonly string RuntimeUnhealthy = nameof(RuntimeUnhealthy);

    /// <summary>
    /// The stopping state. Useful for showing the resource is stopping.
    /// </summary>
    public static readonly string Stopping = nameof(Stopping);

    /// <summary>
    /// The exited state. Useful for showing the resource has exited.
    /// </summary>
    public static readonly string Exited = nameof(Exited);

    /// <summary>
    /// The finished state. Useful for showing the resource has finished.
    /// </summary>
    public static readonly string Finished = nameof(Finished);

    /// <summary>
    /// The waiting state. Useful for showing the resource is waiting for a dependency.
    /// </summary>
    public static readonly string Waiting = nameof(Waiting);

    /// <summary>
    /// The not started state. Useful for showing the resource was created without being started.
    /// </summary>
    public static readonly string NotStarted = nameof(NotStarted);

    /// <summary>
    /// The building state. Useful for showing the resource is being rebuilt.
    /// </summary>
    public static readonly string Building = nameof(Building);

    /// <summary>
    /// The value missing state. Useful for showing a parameter resource is waiting for a value.
    /// </summary>
    public static readonly string ValueMissing = nameof(ValueMissing);

    /// <summary>
    /// The active state. Useful for resources without a lifetime.
    /// </summary>
    public static readonly string Active = nameof(Active);

    /// <summary>
    /// List of terminal states.
    /// </summary>
    public static readonly IReadOnlyList<string> TerminalStates = [Finished, FailedToStart, Exited];

    /// <summary>
    /// List of states in which a resource can be rebuilt.
    /// </summary>
    public static readonly IReadOnlyList<string> BuildableStates = [Running, Waiting, Finished, FailedToStart, Exited];
}
