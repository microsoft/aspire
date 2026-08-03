// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Orleans;

/// <summary>
/// Specifies the Orleans Invariant for an ADO.NET resource.
/// </summary>
/// <param name="invariant">The Orleans ADO.NET Invariant to use for the resource.</param>
public sealed class OrleansAdoNetInvariantAnnotation(string invariant) : IResourceAnnotation
{
    /// <summary>
    /// Gets the Orleans ADO.NET Invariant to use for the resource.
    /// </summary>
    public string Invariant { get; } = ValidateAdoNetInvariant(invariant);

    private static string ValidateAdoNetInvariant(string invariant)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(invariant);

        return invariant;
    }
}
