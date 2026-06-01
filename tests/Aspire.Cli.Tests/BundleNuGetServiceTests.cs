// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.NuGet;

namespace Aspire.Cli.Tests;

public class BundleNuGetServiceTests
{
    [Fact]
    public void ComputePackageHash_ChangesWhenManagedBinaryChanges()
    {
        var tempDir = Directory.CreateTempSubdirectory("aspire-test");

        try
        {
            var managedPath = Path.Combine(tempDir.FullName, "aspire-managed");
            var packages = new List<(string Id, string Version)>
            {
                ("Microsoft.Data.SqlClient", "6.1.4")
            };

            File.WriteAllText(managedPath, "old");
            File.SetLastWriteTimeUtc(managedPath, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

            var firstHash = BundleNuGetService.ComputePackageHash(packages, "net10.0", "osx-arm64", managedPath);

            File.WriteAllText(managedPath, "new-content");
            File.SetLastWriteTimeUtc(managedPath, new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc));

            var secondHash = BundleNuGetService.ComputePackageHash(packages, "net10.0", "osx-arm64", managedPath);

            Assert.NotEqual(firstHash, secondHash);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }
}
