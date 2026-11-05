// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Defines a named group of resource deployment operations that must not execute concurrently.
/// </summary>
/// <remarks>
/// Use the same <see cref="Name"/> across <see cref="DeploymentConcurrencyGroupAnnotation"/> values
/// whose deployment operations must be mutually exclusive. Names are compared using ordinal comparison.
/// A resource can participate in multiple groups, and publishers should enforce every applicable group.
/// Publishers lower group membership into their native scheduling dependencies before
/// serializing deployment artifacts, so group identity does not become an artifact-level contract.
/// </remarks>
[Experimental("ASPIRECOMPUTE004", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public sealed class DeploymentConcurrencyGroup
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DeploymentConcurrencyGroup"/> class.
    /// </summary>
    /// <param name="name">The name that identifies the concurrency group.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="name"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="name"/> is empty or consists only of white-space characters.</exception>
    public DeploymentConcurrencyGroup(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        Name = name;
    }

    /// <summary>
    /// Gets the name that identifies the concurrency group.
    /// </summary>
    public string Name { get; }
}
