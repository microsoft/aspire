// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO.Hashing;
using System.Text;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting;

/// <summary>
/// Resolves workload-scoped host directories for volume mounts used by local processes.
/// </summary>
internal static class VolumeMountPathResolver
{
    internal static string GetOrCreateLocalPath(IAspireStore store, IResource resource, string volumeName)
    {
        var path = GetLocalPath(store, resource, volumeName);
        Directory.CreateDirectory(path);
        return path;
    }

    internal static string GetLocalPath(IAspireStore store, IResource resource, string volumeName)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentException.ThrowIfNullOrEmpty(volumeName);

        // Generic target publishers do not share volume identity consistently, so local process
        // storage is scoped to the workload. First-class target volume resources can supply a
        // target-specific resolver when they intentionally model shared storage.
        return GetPathUnderStore(
            store,
            "volumes",
            GetStablePathSegment(resource.Name),
            GetStablePathSegment(volumeName));
    }

    internal static string GetStablePathSegment(string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);

        // Hash the complete identity instead of sanitizing it. Sanitization can collapse distinct
        // names and can still produce platform-reserved file names such as CON on Windows.
        return Convert.ToHexString(XxHash3.Hash(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }

    internal static string GetPathUnderStore(IAspireStore store, params string[] pathSegments)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(pathSegments);

        var storePath = Path.GetFullPath(store.BasePath);
        var path = Path.GetFullPath(Path.Combine([storePath, .. pathSegments]));
        var storePrefix = Path.EndsInDirectorySeparator(storePath)
            ? storePath
            : storePath + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (!path.StartsWith(storePrefix, comparison))
        {
            throw new InvalidOperationException($"The volume path '{path}' is outside the Aspire store '{storePath}'.");
        }

        return path;
    }
}
