// Copyright The OpenTelemetry Authors
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using OpenTelemetry.Internal;

namespace OpenTelemetry.Trace;

internal static class ActivitySourceFactory
{
    /// <summary>
    /// Creates a new <see cref="ActivitySource"/> for the assembly associated
    /// with the specified type and semantic conventions version.
    /// </summary>
    /// <typeparam name="T">The type for which the <see cref="ActivitySource"/> is created for its assembly.</typeparam>
    /// <param name="semanticConventionsVersion">The version of the semantic conventions.</param>
    /// <param name="tags">The optional tags to associate with the created <see cref="ActivitySource"/>.</param>
    /// <returns>A new <see cref="ActivitySource"/> instance.</returns>
    public static ActivitySource Create<T>(Version? semanticConventionsVersion, IEnumerable<KeyValuePair<string, object?>>? tags = null)
        => Create(typeof(T), semanticConventionsVersion, tags);

    /// <summary>
    /// Creates a new <see cref="ActivitySource"/> for the assembly associated
    /// with the specified type and semantic conventions version.
    /// </summary>
    /// <param name="type">The type for which the <see cref="ActivitySource"/> is created for its assembly.</param>
    /// <param name="semanticConventionsVersion">The version of the semantic conventions.</param>
    /// <param name="tags">The optional tags to associate with the created <see cref="ActivitySource"/>.</param>
    /// <param name="name">The optional name to use for the <see cref="ActivitySource"/> instead of the assembly name associated with <paramref name="type"/>.</param>
    /// <returns>A new <see cref="ActivitySource"/> instance.</returns>
    public static ActivitySource Create(Type type, Version? semanticConventionsVersion, IEnumerable<KeyValuePair<string, object?>>? tags = null, string? name = null)
    {
        Guard.ThrowIfNull(type);

        var assembly = type.Assembly;
        var assemblyName = assembly.GetName();
        var version = assembly.GetPackageVersion();

#pragma warning disable IDE0370 // Suppression is unnecessary
        name ??= assemblyName.Name!;
#pragma warning restore IDE0370 // Suppression is unnecessary

        var options = new ActivitySourceOptions(name)
        {
            Version = version,
        };

        if (tags is not null)
        {
            options.Tags = tags;
        }

        if (semanticConventionsVersion is not null)
        {
            options.TelemetrySchemaUrl = $"https://opentelemetry.io/schemas/{semanticConventionsVersion.ToString(3)}";
        }

        return new(options);
    }
}
