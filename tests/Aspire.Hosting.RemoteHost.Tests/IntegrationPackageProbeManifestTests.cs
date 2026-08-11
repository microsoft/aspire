// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Xunit;

namespace Aspire.Hosting.RemoteHost.Tests;

public class IntegrationPackageProbeManifestTests
{
    [Fact]
    public void Load_ThrowsInvalidOperationExceptionForInvalidManifestPath()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => IntegrationPackageProbeManifest.Load("integration-package-probe-manifest\0.json"));

        Assert.Equal("Integration package probe manifest path is invalid.", exception.Message);
        Assert.IsType<ArgumentException>(exception.InnerException);
    }

    [Fact]
    public void Load_ThrowsInvalidOperationExceptionForInvalidManifestEntryPath()
    {
        var manifestDirectory = Directory.CreateTempSubdirectory("aspire-remotehost-manifest-");
        try
        {
            var manifestPath = Path.Combine(manifestDirectory.FullName, "integration-package-probe-manifest.json");
            File.WriteAllText(
                manifestPath,
                """
                {
                  "managedAssemblies": [
                    {
                      "name": "Aspire.Hosting.Redis",
                      "path": "Aspire.Hosting.Redis\u0000.dll"
                    }
                  ],
                  "nativeLibraries": []
                }
                """);

            var exception = Assert.Throws<InvalidOperationException>(
                () => IntegrationPackageProbeManifest.Load(manifestPath));

            Assert.Equal("Integration package probe manifest entry path 'managedAssemblies[].path' is invalid.", exception.Message);
            Assert.IsType<ArgumentException>(exception.InnerException);
        }
        finally
        {
            Directory.Delete(manifestDirectory.FullName, recursive: true);
        }
    }
}
