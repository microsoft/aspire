// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Shared;

namespace Aspire.Cli.Packaging;

internal static class PackageSourceIdentity
{
    public static IEqualityComparer<string> Comparer { get; } = new IdentityComparer();

    public static bool IsNamedSourceReference(string source)
    {
        var trimmed = source.Trim();
        return !Uri.TryCreate(trimmed, UriKind.Absolute, out _) &&
            !Path.IsPathFullyQualified(trimmed);
    }

    public static string Normalize(string source) => NuGetSourceIdentity.Normalize(source);

    private sealed class IdentityComparer : IEqualityComparer<string>
    {
        public bool Equals(string? x, string? y)
        {
            if (ReferenceEquals(x, y))
            {
                return true;
            }

            return x is not null &&
                y is not null &&
                string.Equals(Normalize(x), Normalize(y), StringComparison.Ordinal);
        }

        public int GetHashCode(string obj) => StringComparer.Ordinal.GetHashCode(Normalize(obj));
    }
}
