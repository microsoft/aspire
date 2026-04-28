// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Bundles;
using Aspire.Cli.Tests.Utils;
using Aspire.Shared;

namespace Aspire.Cli.Tests;

public class BundleServiceTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public void IsBundle_ReturnsFalse_WhenNoEmbeddedResource()
    {
        // Test assembly has no embedded bundle.tar.gz resource — verify via provider
        var provider = new EmbeddedBundlePayloadProvider();
        Assert.False(provider.HasPayload);
    }

    [Fact]
    public void OpenPayload_ReturnsNull_WhenNoEmbeddedResource()
    {
        var provider = new EmbeddedBundlePayloadProvider();
        Assert.Null(provider.OpenPayload());
    }

    [Fact]
    public void VersionMarker_WriteAndRead_Roundtrips()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var dir = workspace.WorkspaceRoot.FullName;

        BundleService.WriteVersionMarker(dir, "13.2.0-dev");

        Assert.Equal("13.2.0-dev", BundleService.ReadVersionMarker(dir));
    }

    [Fact]
    public void VersionMarker_ReturnsNull_WhenMissing()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        Assert.Null(BundleService.ReadVersionMarker(workspace.WorkspaceRoot.FullName));
    }

    [Fact]
    public void GetDefaultExtractDir_ReturnsParentOfParent()
    {
        if (OperatingSystem.IsWindows())
        {
            var result = BundleService.GetDefaultExtractDir(@"C:\Users\test\.aspire\bin\aspire.exe");
            Assert.Equal(@"C:\Users\test\.aspire", result);
        }
        else
        {
            var result = BundleService.GetDefaultExtractDir("/home/test/.aspire/bin/aspire");
            Assert.Equal("/home/test/.aspire", result);
        }
    }

    [Fact]
    public void GetCurrentVersion_ReturnsNonNull()
    {
        var version = BundleService.GetCurrentVersion();
        Assert.NotNull(version);
        Assert.NotEqual("unknown", version);
    }

    [Fact]
    public void GetCurrentVersion_ChangesWhenCliBinaryChanges()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var processPath = Path.Combine(workspace.WorkspaceRoot.FullName, "aspire");
        File.WriteAllText(processPath, "old");
        File.SetLastWriteTimeUtc(processPath, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var firstVersion = BundleService.GetCurrentVersion(processPath);

        File.WriteAllText(processPath, "new-content");
        File.SetLastWriteTimeUtc(processPath, new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc));

        var secondVersion = BundleService.GetCurrentVersion(processPath);

        Assert.NotEqual(firstVersion, secondVersion);
    }

    [Fact]
    public void ComputeVersionId_IsDeterministic()
    {
        var a = BundleService.ComputeVersionId("1.2.3|1234|56789");
        var b = BundleService.ComputeVersionId("1.2.3|1234|56789");
        Assert.Equal(a, b);
    }

    [Fact]
    public void ComputeVersionId_DiffersWhenFingerprintDiffers()
    {
        var a = BundleService.ComputeVersionId("1.2.3|1234|56789");
        var b = BundleService.ComputeVersionId("1.2.3|1235|56789");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void ComputeVersionId_IsFilesystemSafe()
    {
        var id = BundleService.ComputeVersionId("1.2.3+ci/branch?name|1234|56789");
        Assert.DoesNotContain('/', id);
        Assert.DoesNotContain('\\', id);
        Assert.DoesNotContain('+', id);
        Assert.DoesNotContain('?', id);
        Assert.DoesNotContain('|', id);
        Assert.StartsWith("1.2.3_ci_branch_name-", id, StringComparison.Ordinal);
    }

    [Fact]
    public void IsVersionedLayoutValid_RequiresManagedExecutableAndDcpDirectory()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var dir = workspace.WorkspaceRoot.FullName;

        Assert.False(BundleService.IsVersionedLayoutValid(dir));

        CreateFakeBundleLayout(dir);
        Assert.True(BundleService.IsVersionedLayoutValid(dir));

        // Removing the managed exe invalidates the layout.
        var managedExe = Path.Combine(dir, BundleDiscovery.ManagedDirectoryName,
            BundleDiscovery.GetExecutableFileName(BundleDiscovery.ManagedExecutableName));
        File.Delete(managedExe);
        Assert.False(BundleService.IsVersionedLayoutValid(dir));

        // A zero-length managed exe is also invalid.
        File.WriteAllBytes(managedExe, []);
        Assert.False(BundleService.IsVersionedLayoutValid(dir));
    }

    [Fact]
    public void IsVersionedLayoutValid_RequiresDcpDirectory()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var dir = workspace.WorkspaceRoot.FullName;
        CreateFakeBundleLayout(dir);

        Directory.Delete(Path.Combine(dir, BundleDiscovery.DcpDirectoryName), recursive: true);
        Assert.False(BundleService.IsVersionedLayoutValid(dir));
    }

    [Fact]
    public void TryCleanupStaleVersions_RemovesNonActiveVersionsAndStaleTempDirs()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var versionsRoot = Path.Combine(workspace.WorkspaceRoot.FullName, BundleService.VersionsDirectoryName);
        Directory.CreateDirectory(versionsRoot);

        var active = "active-aaaaaaaaaaaaaaaa";
        Directory.CreateDirectory(Path.Combine(versionsRoot, active));
        Directory.CreateDirectory(Path.Combine(versionsRoot, "stale-bbbbbbbbbbbbbbbb"));
        Directory.CreateDirectory(Path.Combine(versionsRoot, $"active{BundleService.TempSuffixPrefix}deadbeef"));
        Directory.CreateDirectory(Path.Combine(versionsRoot, $"active{BundleService.BadSuffixPrefix}99999"));

        BundleService.TryCleanupStaleVersions(versionsRoot, active);

        Assert.True(Directory.Exists(Path.Combine(versionsRoot, active)));
        var remaining = Directory.EnumerateDirectories(versionsRoot).Select(Path.GetFileName).ToArray();
        Assert.Single(remaining);
        Assert.Equal(active, remaining[0]);
    }

    [Fact]
    public void CaptureLinkTargets_ReturnsNullForMissingAndRealDirectories()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var dir = workspace.WorkspaceRoot.FullName;

        // bundle/ does not exist → null entry.
        var captured = BundleService.CaptureLinkTargets(dir);
        Assert.Null(captured[BundleDiscovery.BundleDirectoryName]);

        // bundle/ is a real (non-reparse) directory → also null entry.
        Directory.CreateDirectory(Path.Combine(dir, BundleDiscovery.BundleDirectoryName));
        captured = BundleService.CaptureLinkTargets(dir);
        Assert.Null(captured[BundleDiscovery.BundleDirectoryName]);
    }

    private static void CreateFakeBundleLayout(string root)
    {
        var managedDir = Path.Combine(root, BundleDiscovery.ManagedDirectoryName);
        Directory.CreateDirectory(managedDir);
        File.WriteAllText(
            Path.Combine(managedDir, BundleDiscovery.GetExecutableFileName(BundleDiscovery.ManagedExecutableName)),
            "#!/bin/sh\necho aspire-managed\n");

        var dcpDir = Path.Combine(root, BundleDiscovery.DcpDirectoryName);
        Directory.CreateDirectory(dcpDir);
        File.WriteAllText(Path.Combine(dcpDir, "placeholder"), "dcp");
    }
}
