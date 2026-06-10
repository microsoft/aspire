// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Infrastructure.Tests;

/// <summary>
/// Repository root locator for Infrastructure.Tests. Computed once per test run by
/// walking up from <see cref="AppContext.BaseDirectory"/> until a directory containing
/// <c>Aspire.slnx</c> is found.
/// </summary>
internal static class RepoRoot
{
    /// <summary>
    /// Absolute path to the repository root. Throws <see cref="DirectoryNotFoundException"/>
    /// at first access if the marker file cannot be located (e.g. tests assembly running
    /// outside a checkout of the repo).
    /// </summary>
    public static string Path { get; } = FindOrThrow();

    private static string FindOrThrow()
    {
        // Walk parent directories looking for the solution marker. Tests run from
        // artifacts/bin/<project>/<config>/<tfm>/, so the root is several levels up.
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(System.IO.Path.Combine(directory.FullName, "Aspire.slnx")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not find repository root containing Aspire.slnx (searched from '{AppContext.BaseDirectory}').");
    }
}
