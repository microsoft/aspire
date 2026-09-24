// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Security.Cryptography;
using System.Text;

namespace Aspire.Shared;

internal static class NuGetSourceIdentity
{
    public const int KeySizeInBytes = 32;

    public static string Compute(string source, ReadOnlySpan<byte> key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);

        if (key.Length != KeySizeInBytes)
        {
            throw new ArgumentException($"The NuGet source identity key must be {KeySizeInBytes} bytes.", nameof(key));
        }

        return Convert.ToHexString(HMACSHA256.HashData(
            key,
            Encoding.UTF8.GetBytes(Normalize(source))));
    }

    public static string Normalize(string source)
    {
        var trimmed = source.Trim();
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            if (uri.IsFile)
            {
                return $"path:{NormalizePath(uri.LocalPath)}";
            }

            return $"uri:{uri.AbsoluteUri}";
        }

        if (Path.IsPathFullyQualified(trimmed))
        {
            return $"path:{NormalizePath(trimmed)}";
        }

        return $"name:{trimmed.ToUpperInvariant()}";
    }

    public static bool HasCredentialMaterial(string source)
    {
        var trimmedSource = source.Trim();
        var looksHttp =
            trimmedSource.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            trimmedSource.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

        if (!Uri.TryCreate(trimmedSource, UriKind.Absolute, out var uri))
        {
            return looksHttp;
        }

        return
            (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) &&
            (!string.IsNullOrEmpty(uri.UserInfo) ||
                !string.IsNullOrEmpty(uri.Query) ||
                !string.IsNullOrEmpty(uri.Fragment));
    }

    private static string NormalizePath(string path)
    {
        var normalized = Path.GetFullPath(path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar));
        return OperatingSystem.IsWindows() ? normalized.ToUpperInvariant() : normalized;
    }
}
