// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO.Hashing;
using System.Text;
using Aspire.Hosting.Utils;

namespace Aspire.Cli.Projects;

internal static class AppHostWorkloadId
{
    private const string Prefix = "apphost-";

    public static string Create(FileInfo appHostFile)
    {
        ArgumentNullException.ThrowIfNull(appHostFile);

        return Create(appHostFile.FullName);
    }

    internal static string Create(string appHostPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appHostPath);

        var normalizedPath = PathNormalizer.ResolveSymlinks(appHostPath);
        if (OperatingSystem.IsWindows())
        {
            normalizedPath = normalizedPath.ToLowerInvariant();
        }

        var hashBytes = XxHash3.Hash(Encoding.UTF8.GetBytes(normalizedPath));
        return Prefix + Convert.ToHexString(hashBytes).ToLowerInvariant();
    }
}
