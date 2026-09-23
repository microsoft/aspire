// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Semver;

namespace Aspire.Hosting.Utils;

internal static class DotnetSdkUtils
{
    private static readonly SemVersion s_minimumMultiThreadedBuildVersion = SemVersion.Parse("11.0.100-rc.1");

    public static bool SupportsMultiThreadedBuild(SemVersion? version) =>
        version is not null &&
        SemVersion.ComparePrecedence(version, s_minimumMultiThreadedBuildVersion) >= 0;

    public static string? FindNearestGlobalJson(string workingDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(workingDirectory);

        var physicalWorkingDirectory = PathNormalizer.ResolveSymlinks(Path.GetFullPath(workingDirectory));
        for (var directory = new DirectoryInfo(physicalWorkingDirectory); directory is not null; directory = directory.Parent)
        {
            var globalJsonPath = Path.Combine(directory.FullName, "global.json");
            if (File.Exists(globalJsonPath))
            {
                return PathNormalizer.ResolveToFilesystemPath(globalJsonPath);
            }
        }

        return null;
    }
}
