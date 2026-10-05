// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO.Hashing;
using System.Text;
using Aspire.Hosting.Utils;

namespace Aspire.Cli.Projects;

/// <summary>
/// Creates stable DCP workload identifiers for AppHost paths.
/// </summary>
internal static class AppHostWorkloadId
{
    private const string Prefix = "apphost-";

    /// <summary>
    /// Creates the workload identifier for an AppHost file.
    /// </summary>
    public static string Create(FileInfo appHostFile)
    {
        ArgumentNullException.ThrowIfNull(appHostFile);

        return Create(appHostFile.FullName);
    }

    /// <summary>
    /// Creates the workload identifier for an AppHost path after applying path normalization.
    /// </summary>
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
