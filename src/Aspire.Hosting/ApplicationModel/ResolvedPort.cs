// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Represents a resolved port with information about whether it was allocated or explicitly specified.
/// </summary>
public readonly struct ResolvedPort
{
    /// <summary>
    /// Gets the port number, or null if no port was resolved.
    /// </summary>
    public int? Value { get; init; }

    /// <summary>
    /// Gets a value indicating whether the port was dynamically allocated.
    /// When true, the target environment may choose to ignore this port and use its own allocation mechanism.
    /// When false, the port was explicitly specified and should be used as-is.
    /// </summary>
    public bool IsAllocated { get; init; }

    /// <summary>
    /// Gets a value indicating whether the port was implicitly inferred from another value.
    /// When true, the port was derived (e.g., exposed port matching target port) rather than explicitly specified by the user.
    /// </summary>
    public bool IsImplicit { get; init; }

    /// <summary>
    /// Creates a <see cref="ResolvedPort"/> with an explicitly specified port.
    /// </summary>
    /// <param name="port">The explicitly specified port number.</param>
    /// <returns>A <see cref="ResolvedPort"/> with IsAllocated set to false.</returns>
    public static ResolvedPort Explicit(int port) => new() { Value = port, IsAllocated = false };

    /// <summary>
    /// Creates a <see cref="ResolvedPort"/> with an allocated port.
    /// </summary>
    /// <param name="port">The allocated port number.</param>
    /// <returns>A <see cref="ResolvedPort"/> with IsAllocated set to true.</returns>
    public static ResolvedPort Allocated(int port) => new() { Value = port, IsAllocated = true };

    /// <summary>
    /// Creates a <see cref="ResolvedPort"/> with an implicitly inferred port.
    /// </summary>
    /// <param name="port">The implicitly inferred port number.</param>
    /// <returns>A <see cref="ResolvedPort"/> with IsImplicit set to true.</returns>
    public static ResolvedPort Implicit(int port) => new() { Value = port, IsImplicit = true };

    /// <summary>
    /// Creates a <see cref="ResolvedPort"/> with no port (null).
    /// </summary>
    /// <returns>A <see cref="ResolvedPort"/> with Value set to null.</returns>
    public static ResolvedPort None() => new() { Value = null, IsAllocated = false };

    /// <summary>
    /// Implicitly converts a <see cref="ResolvedPort"/> to a nullable int.
    /// </summary>
    public static implicit operator int?(ResolvedPort resolvedPort) => resolvedPort.Value;
}
