// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Utils;

namespace Aspire.Cli.Backchannel;

/// <summary>
/// Provides the platform-specific path identity rules used for AppHost selection.
/// </summary>
internal static class AppHostPathComparer
{
    public static bool PathsEqual(string leftPath, string rightPath)
    {
        return PathsEqual(leftPath, rightPath, Comparer);
    }

    internal static bool PathsEqual(string leftPath, string rightPath, StringComparer fallbackComparer)
    {
        if (StringComparer.Ordinal.Equals(leftPath, rightPath))
        {
            return true;
        }

        var leftCanonicalized = TryGetCanonicalPath(leftPath, out var canonicalLeftPath);
        var rightCanonicalized = TryGetCanonicalPath(rightPath, out var canonicalRightPath);

        if (leftCanonicalized || rightCanonicalized)
        {
            if (!leftCanonicalized || !rightCanonicalized)
            {
                return false;
            }

            // Stored filesystem spelling is authoritative even on Windows because an individual
            // directory can opt into case-sensitive semantics. The volume root is identity rather
            // than a directory segment, so canonicalize only that root before the ordinal comparison.
            return StringComparer.Ordinal.Equals(
                NormalizeRootIdentity(canonicalLeftPath),
                NormalizeRootIdentity(canonicalRightPath));
        }

        return fallbackComparer.Equals(
            PathNormalizer.ResolveToFilesystemPath(PathNormalizer.ResolveSymlinks(leftPath)),
            PathNormalizer.ResolveToFilesystemPath(PathNormalizer.ResolveSymlinks(rightPath)));
    }

    internal static string NormalizeRootIdentity(string path)
    {
        var driveLetterIndex = path.Length >= 3 &&
            path[1] == ':' &&
            path[2] is '\\' or '/' &&
            char.IsAsciiLetter(path[0])
                ? 0
                : path.Length >= 7 &&
                    path[0] is '\\' or '/' &&
                    path[1] is '\\' or '/' &&
                    path[2] is '?' or '.' &&
                    path[3] is '\\' or '/' &&
                    path[5] == ':' &&
                    path[6] is '\\' or '/' &&
                    char.IsAsciiLetter(path[4])
                        ? 4
                        : -1;

        if (driveLetterIndex >= 0)
        {
            var normalizedDriveLetter = char.ToUpperInvariant(path[driveLetterIndex]);
            return normalizedDriveLetter == path[driveLetterIndex]
                ? path
                : $"{path[..driveLetterIndex]}{normalizedDriveLetter}{path[(driveLetterIndex + 1)..]}";
        }

        var rootSegmentStart = path.Length >= 8 &&
            path[0] is '\\' or '/' &&
            path[1] is '\\' or '/' &&
            path[2] is '?' or '.' &&
            path[3] is '\\' or '/' &&
            path.AsSpan(4, 3).Equals("UNC", StringComparison.OrdinalIgnoreCase) &&
            path[7] is '\\' or '/'
                ? 8
                : path.Length >= 2 &&
                    path[0] is '\\' or '/' &&
                    path[1] is '\\' or '/'
                        ? 2
                        : -1;

        if (rootSegmentStart < 0)
        {
            return path;
        }

        var serverSeparator = FindSeparator(path, rootSegmentStart);
        if (serverSeparator < 0 || serverSeparator == path.Length - 1)
        {
            return path;
        }

        var shareSeparator = FindSeparator(path, serverSeparator + 1);
        var rootEnd = shareSeparator >= 0 ? shareSeparator : path.Length;
        var normalizedRoot = path[..rootEnd].ToUpperInvariant();
        return normalizedRoot.Equals(path.AsSpan(0, rootEnd), StringComparison.Ordinal)
            ? path
            : string.Concat(normalizedRoot, path.AsSpan(rootEnd));
    }

    private static int FindSeparator(string path, int startIndex)
    {
        for (var i = startIndex; i < path.Length; i++)
        {
            if (path[i] is '\\' or '/')
            {
                return i;
            }
        }

        return -1;
    }

    private static bool TryGetCanonicalPath(string path, out string canonicalPath)
    {
        canonicalPath = path;
        return PathNormalizer.TryResolveSymlinks(path, out var symlinkResolvedPath) &&
            PathNormalizer.TryResolveToFilesystemPath(symlinkResolvedPath, out canonicalPath);
    }

    private static StringComparer Comparer =>
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
}
