// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO.Hashing;
using System.Text;

namespace Aspire.Cli.Projects;

internal static class AppHostWorkloadId
{
    private const string Prefix = "apphost-";

    public static string Create(FileInfo appHostFile, bool isWindows)
    {
        ArgumentNullException.ThrowIfNull(appHostFile);

        return Create(appHostFile.FullName, isWindows);
    }

    internal static string Create(string appHostPath, bool isWindows)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appHostPath);

        var normalizedPath = Path.GetFullPath(appHostPath);
        if (isWindows)
        {
            normalizedPath = normalizedPath.ToUpperInvariant();
        }

        var hashBytes = XxHash3.Hash(Encoding.UTF8.GetBytes(normalizedPath));
        return Prefix + Convert.ToHexString(hashBytes).ToLowerInvariant();
    }
}
