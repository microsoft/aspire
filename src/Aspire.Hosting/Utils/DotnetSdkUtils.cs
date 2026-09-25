// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Semver;

namespace Aspire.Hosting.Utils;

internal static class DotnetSdkUtils
{
    private static readonly SemVersion s_minimumMultiThreadedBuildVersion = SemVersion.Parse("11.0.100-rc.1");
    // File-based apps require both the SDK argument-forwarding fix and the compiler-client mutex mitigation.
    // 11.0.100-rtm.26473.104 is verified to contain both; earlier prereleases remain disabled:
    // https://github.com/dotnet/sdk/pull/56207
    // https://github.com/dotnet/roslyn/pull/85675
    // https://github.com/dotnet/dotnet/pull/9565
    // RC builds remain below this SemVer floor so they must be explicitly verified before being enabled.
    private static readonly SemVersion s_minimumFileBasedMultiThreadedBuildVersion =
        SemVersion.Parse("11.0.100-rtm.26473.104");

    public static bool SupportsMultiThreadedBuild(SemVersion? version) =>
        version is not null &&
        SemVersion.ComparePrecedence(version, s_minimumMultiThreadedBuildVersion) >= 0;

    public static bool SupportsFileBasedMultiThreadedBuild(SemVersion? version) =>
        version is not null &&
        SemVersion.ComparePrecedence(version, s_minimumFileBasedMultiThreadedBuildVersion) >= 0;

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
