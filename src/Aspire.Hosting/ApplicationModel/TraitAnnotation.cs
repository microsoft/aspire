// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Dashboard.Model;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Immutable, non-sensitive metadata attached to a resource. Multiple annotations with the same name represent multiple values.
/// </summary>
public sealed class TraitAnnotation : IResourceAnnotation
{
    /// <summary>
    /// Creates a resource trait.
    /// </summary>
    /// <param name="name">The case-sensitive trait name.</param>
    /// <param name="value">The trait value. Traits are visible in dashboard and tooling output and must not contain secrets.</param>
    public TraitAnnotation(string name, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        if (name == KnownResourceTraits.DashboardTag &&
            (value.Length > 50 || value != string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))))
        {
            throw new ArgumentException("Dashboard tags must be at most 50 characters and use single spaces between words, without leading or trailing whitespace.", nameof(value));
        }
        if (name == KnownResourceTraits.DashboardPosition && !int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
        {
            throw new ArgumentException("Dashboard positions must be 32-bit integers.", nameof(value));
        }

        Name = name;
        Value = value;
    }

    /// <summary>Gets the case-sensitive trait name.</summary>
    public string Name { get; }

    /// <summary>Gets the trait value.</summary>
    public string Value { get; }
}
