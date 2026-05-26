// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Formats.Tar;
using System.IO.Compression;
using Aspire.Cli.Bundles;
using Aspire.Cli.Layout;
using Aspire.Cli.Tests.Acquisition;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Aspire.Cli.Utils;
using Aspire.Shared;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests;

// EnsureExtractedAndAcquireLayoutAsync_SidecarlessInstall_ExtractsToAspireHomeAndAcquiresLayout
// mutates the process-wide ASPIRE_HOME variable. xUnit runs test classes in parallel by default,
// so join EnvVarMutatingTestCollection to serialize with other suites that read ASPIRE_HOME via
// CliPathHelper.GetDefaultAspireHomeDirectory.
[Collection(EnvVarMutatingTestCollection.Name)]
public class BundleServiceIntegrationTests(ITestOutputHelper outputHelper)
{
    /// <summary>
    /// Verifies that a fresh extraction with a synthetic payload creates a
    /// versioned directory, writes a version marker, and establishes reparse
    /// points for managed/ and dcp/.
    /// </summary>
    [Fact]
    public async Task ExtractAsync_FreshExtraction_CreatesVersionedLayoutAndLinks()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var layoutRoot = workspace.WorkspaceRoot.FullName;
        var payload = CreateFakeBundlePayload();
        var provider = new TestBundlePayloadProvider(payload);

        var layoutDiscovery = new TestLayoutDiscovery(layoutRoot);
        var service = CreateService(provider, layoutDiscovery);

        var result = await service.ExtractAsync(layoutRoot, force: true);

        Assert.Equal(BundleExtractResult.Extracted, result);

        // Version marker should exist.
        var marker = BundleService.ReadVersionMarker(layoutRoot);
        Assert.NotNull(marker);

        // versions/ directory should contain exactly one version.
        var versionsDir = Path.Combine(layoutRoot, BundleService.VersionsDirectoryName);
        Assert.True(Directory.Exists(versionsDir));
        var versions = Directory.GetDirectories(versionsDir);
        Assert.Single(versions);

        // bundle/ should be a reparse point pointing into versions/.
        var bundleLink = Path.Combine(layoutRoot, BundleDiscovery.BundleDirectoryName);

        try
        {
            Assert.True(ReparsePoint.IsReparsePoint(bundleLink), "bundle/ should be a reparse point");

            // Verify the managed exe is reachable through the bundle reparse point.
            var managedExe = Path.Combine(bundleLink,
                BundleDiscovery.ManagedDirectoryName,
                BundleDiscovery.GetExecutableFileName(BundleDiscovery.ManagedExecutableName));
            Assert.True(File.Exists(managedExe), $"managed exe should exist at {managedExe}");
        }
        finally
        {
            CleanupReparsePoints(layoutRoot);
        }
    }

    /// <summary>
    /// Verifies that when an already-extracted layout is up to date, extraction
    /// is skipped and returns <see cref="BundleExtractResult.AlreadyUpToDate"/>.
    /// </summary>
    [Fact]
    public async Task ExtractAsync_AlreadyUpToDate_SkipsExtraction()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var layoutRoot = workspace.WorkspaceRoot.FullName;
        var payload = CreateFakeBundlePayload();
        var provider = new TestBundlePayloadProvider(payload);

        var layoutDiscovery = new TestLayoutDiscovery(layoutRoot);
        var service = CreateService(provider, layoutDiscovery);

        // First extraction.
        var result1 = await service.ExtractAsync(layoutRoot, force: true);
        Assert.Equal(BundleExtractResult.Extracted, result1);

        try
        {
            // Second extraction without force — should be up to date.
            var result2 = await service.ExtractAsync(layoutRoot, force: false);
            Assert.Equal(BundleExtractResult.AlreadyUpToDate, result2);
        }
        finally
        {
            CleanupReparsePoints(layoutRoot);
        }
    }

    /// <summary>
    /// Verifies that upgrading from v1 to v2 flips the reparse points to the
    /// new versioned directory and cleans up the old one.
    /// </summary>
    [Fact]
    public async Task ExtractAsync_Upgrade_FlipsLinksAndCleansUpOldVersion()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var layoutRoot = workspace.WorkspaceRoot.FullName;
        var layoutDiscovery = new TestLayoutDiscovery(layoutRoot);

        // Create fake CLI binaries with different content to produce different version IDs.
        var v1BinaryPath = CreateFakeCliBinary(Path.Combine(layoutRoot, ".fake-bins"), "aspire-v1", "cli-v1");
        var v2BinaryPath = CreateFakeCliBinary(Path.Combine(layoutRoot, ".fake-bins"), "aspire-v2", "cli-v2-longer");
        // Ensure different timestamps.
        File.SetLastWriteTimeUtc(v2BinaryPath, File.GetLastWriteTimeUtc(v1BinaryPath).AddHours(1));

        // Install v1.
        var v1Payload = CreateFakeBundlePayload("v1-content");
        var v1Provider = new TestBundlePayloadProvider(v1Payload);
        var v1Service = CreateService(v1Provider, layoutDiscovery, v1BinaryPath);

        var result1 = await v1Service.ExtractAsync(layoutRoot, force: true);
        Assert.Equal(BundleExtractResult.Extracted, result1);

        // Capture the v1 version directory.
        var versionsDir = Path.Combine(layoutRoot, BundleService.VersionsDirectoryName);
        var v1Dirs = Directory.GetDirectories(versionsDir);
        Assert.Single(v1Dirs);
        var v1VersionDir = v1Dirs[0];

        try
        {
            // Install v2 with a different payload and different binary fingerprint.
            var v2Payload = CreateFakeBundlePayload("v2-content");
            var v2Provider = new TestBundlePayloadProvider(v2Payload);
            var v2Service = CreateService(v2Provider, layoutDiscovery, v2BinaryPath);

            var result2 = await v2Service.ExtractAsync(layoutRoot, force: true);
            Assert.Equal(BundleExtractResult.Extracted, result2);

            // Verify we now have a new version directory (old one cleaned up).
            var v2Dirs = Directory.GetDirectories(versionsDir);
            Assert.Single(v2Dirs);
            Assert.NotEqual(v1VersionDir, v2Dirs[0]);

            // The managed exe should be reachable and contain v2 content.
            var managedExe = Path.Combine(layoutRoot, BundleDiscovery.BundleDirectoryName,
                BundleDiscovery.ManagedDirectoryName,
                BundleDiscovery.GetExecutableFileName(BundleDiscovery.ManagedExecutableName));
            var content = File.ReadAllText(managedExe);
            Assert.Contains("v2-content", content);
        }
        finally
        {
            CleanupReparsePoints(layoutRoot);
        }
    }

    /// <summary>
    /// Verifies that stale version directories are cleaned up after extraction.
    /// </summary>
    [Fact]
    public async Task ExtractAsync_CleansUpStaleVersionDirectories()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var layoutRoot = workspace.WorkspaceRoot.FullName;
        var versionsDir = Path.Combine(layoutRoot, BundleService.VersionsDirectoryName);
        Directory.CreateDirectory(versionsDir);

        // Pre-populate with stale directories.
        Directory.CreateDirectory(Path.Combine(versionsDir, "stale-aaaaaaaaaaaaaaaa"));
        Directory.CreateDirectory(Path.Combine(versionsDir, $"stale{BundleService.TempSuffixPrefix}deadbeef"));
        Directory.CreateDirectory(Path.Combine(versionsDir, $"stale{BundleService.BadSuffixPrefix}99999"));

        var payload = CreateFakeBundlePayload();
        var provider = new TestBundlePayloadProvider(payload);
        var layoutDiscovery = new TestLayoutDiscovery(layoutRoot);
        var service = CreateService(provider, layoutDiscovery);

        var result = await service.ExtractAsync(layoutRoot, force: true);
        Assert.Equal(BundleExtractResult.Extracted, result);

        try
        {
            // Only the active version should remain.
            var remaining = Directory.GetDirectories(versionsDir);
            Assert.Single(remaining);
        }
        finally
        {
            CleanupReparsePoints(layoutRoot);
        }
    }

    [Fact]
    public async Task EnsureExtractedAndAcquireLayoutAsync_ReturnsVersionRootedLayoutAndSkipsLeasedCleanup()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var layoutRoot = workspace.WorkspaceRoot.FullName;
        var layoutDiscovery = new TestLayoutDiscovery(layoutRoot);

        var binDir = Path.Combine(layoutRoot, ".fake-bins");
        var v1BinaryPath = CreateFakeCliBinary(binDir, "aspire-v1", "cli-v1");
        var v2BinaryPath = CreateFakeCliBinary(binDir, "aspire-v2", "cli-v2-longer");
        File.SetLastWriteTimeUtc(v2BinaryPath, File.GetLastWriteTimeUtc(v1BinaryPath).AddHours(1));

        // Drop a script sidecar so EnsureExtractedAndAcquireLayoutAsync resolves the
        // process-driven extract directory to the parent of the binary directory
        // (layoutRoot) rather than the default Aspire home.
        File.WriteAllText(Path.Combine(binDir, ".aspire-install.json"), "{\"source\":\"script\"}");

        var v1Service = CreateService(new TestBundlePayloadProvider(CreateFakeBundlePayload("v1")), layoutDiscovery, v1BinaryPath);
        var result1 = await v1Service.ExtractAsync(layoutRoot, force: true);
        Assert.Equal(BundleExtractResult.Extracted, result1);

        var versionsDir = Path.Combine(layoutRoot, BundleService.VersionsDirectoryName);
        var v1VersionDir = Assert.Single(Directory.GetDirectories(versionsDir));

        try
        {
            using var layoutLease = await v1Service.EnsureExtractedAndAcquireLayoutAsync("test", "reader");
            Assert.NotNull(layoutLease);
            Assert.True(layoutLease.HasLease);

            var managedPath = layoutLease.Layout.GetManagedPath();
            Assert.NotNull(managedPath);

            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            Assert.StartsWith(
                Path.GetFullPath(v1VersionDir),
                Path.GetFullPath(managedPath!),
                comparison);

            var v2Service = CreateService(new TestBundlePayloadProvider(CreateFakeBundlePayload("v2")), layoutDiscovery, v2BinaryPath);
            var result2 = await v2Service.ExtractAsync(layoutRoot, force: true);
            Assert.Equal(BundleExtractResult.Extracted, result2);

            Assert.True(Directory.Exists(v1VersionDir), "Leased stale version should not be deleted during upgrade cleanup.");
            Assert.Equal(2, Directory.GetDirectories(versionsDir).Length);
        }
        finally
        {
            CleanupReparsePoints(layoutRoot);
        }
    }

    [Fact]
    public void TryCleanupStaleVersions_SkipsVersionWithActiveLease()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var versionsDir = Path.Combine(workspace.WorkspaceRoot.FullName, BundleService.VersionsDirectoryName);
        var staleVersionDir = Path.Combine(versionsDir, "stale-version");
        Directory.CreateDirectory(staleVersionDir);

        using var lease = BundleVersionLease.Acquire(staleVersionDir, "test", "cleanup");

        BundleService.TryCleanupStaleVersions(versionsDir, activeVersionId: "active-version");

        Assert.True(Directory.Exists(staleVersionDir));
    }

    [Fact]
    public void TryCleanupStaleVersions_RemovesOrphanLeaseFilesBeforeDeletingVersion()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var versionsDir = Path.Combine(workspace.WorkspaceRoot.FullName, BundleService.VersionsDirectoryName);
        var staleVersionDir = Path.Combine(versionsDir, "stale-version");
        var leasesDir = Path.Combine(staleVersionDir, BundleVersionLease.LeasesDirectoryName);
        Directory.CreateDirectory(leasesDir);
        File.WriteAllText(Path.Combine(leasesDir, "orphan.lease"), "{}");

        BundleService.TryCleanupStaleVersions(versionsDir, activeVersionId: "active-version");

        Assert.False(Directory.Exists(staleVersionDir));
    }

    [Fact]
    public void BundleVersionLease_AllowsConcurrentReadersForSameVersion()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var versionDir = workspace.CreateDirectory("version").FullName;

        using var lease1 = BundleVersionLease.Acquire(versionDir, "test", "reader1");
        using var lease2 = BundleVersionLease.Acquire(versionDir, "test", "reader2");

        Assert.NotEqual(lease1.LeasePath, lease2.LeasePath);
        Assert.True(BundleVersionLease.HasActiveLease(versionDir));
    }

    [Fact]
    public async Task EnsureExtractedAsync_DotnetToolStorePath_ExtractsToRidDirectory()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        // RID-specific dotnet-tool layout: the native binary lives in the
        // RID-scoped directory inside the tool store. The sidecar declares
        // source=dotnet-tool so extraction stays at that same RID directory
        // (binaryDir) rather than falling back to Aspire home.
        var ridDir = Path.Combine(
            workspace.WorkspaceRoot.FullName,
            ".dotnet",
            "tools",
            ".store",
            "aspire.cli",
            "9.4.0",
            "aspire.cli.linux-x64",
            "9.4.0",
            "tools",
            "net10.0",
            "linux-x64");
        var processPath = CreateFakeCliBinary(
            ridDir,
            BundleDiscovery.GetExecutableFileName("aspire"),
            "native-aot-cli");
        File.WriteAllText(Path.Combine(ridDir, ".aspire-install.json"), "{\"source\":\"dotnet-tool\"}");
        var payload = CreateFakeBundlePayload();
        var provider = new TestBundlePayloadProvider(payload);
        var layoutDiscovery = new TestLayoutDiscovery(ridDir);
        var service = CreateService(provider, layoutDiscovery, processPath);

        await service.EnsureExtractedAsync();

        var bundleLink = Path.Combine(ridDir, BundleDiscovery.BundleDirectoryName);
        try
        {
            Assert.True(ReparsePoint.IsReparsePoint(bundleLink), "bundle/ should be a reparse point under the tool RID directory");

            var managedExe = Path.Combine(bundleLink,
                BundleDiscovery.ManagedDirectoryName,
                BundleDiscovery.GetExecutableFileName(BundleDiscovery.ManagedExecutableName));
            Assert.True(File.Exists(managedExe), $"managed exe should exist at {managedExe}");
        }
        finally
        {
            CleanupReparsePoints(ridDir);
        }
    }

    [Fact]
    public async Task EnsureExtractedAndAcquireLayoutAsync_SidecarlessInstall_ExtractsToAspireHomeAndAcquiresLayout()
    {
        // End-to-end coverage for the sidecar-less install scenario fixed by this PR:
        //   1. ComputeDefaultExtractDir routes a no-sidecar CLI to $ASPIRE_HOME instead of
        //      Path.GetDirectoryName(binaryDir) so binaries in read-only locations (e.g.
        //      Nix stores) can still extract their bundle.
        //   2. The real LayoutDiscovery's Aspire-home probe finds the freshly extracted
        //      layout even though the CLI binary is outside the bundle hierarchy.
        // Individual pieces are covered by ComputeDefaultExtractDir tests and
        // LayoutDiscovery_FallsBackToAspireHomeWhenLayoutIsNotRelativeToCli; this test
        // locks in the combined behavior end-to-end through EnsureExtractedAndAcquireLayoutAsync.
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        // CLI lives in a directory unrelated to any layout, with no .aspire-install.json
        // sidecar. This simulates an installation in an arbitrary location such as a
        // package-manager-controlled, read-only prefix.
        var binaryDir = Path.Combine(workspace.WorkspaceRoot.FullName, "opt", "aspire-readonly", "bin");
        var processPath = CreateFakeCliBinary(
            binaryDir,
            BundleDiscovery.GetExecutableFileName("aspire"),
            "sidecarless-cli");

        var aspireHome = Path.Combine(workspace.WorkspaceRoot.FullName, "home", ".aspire");
        Directory.CreateDirectory(aspireHome);

        var payload = CreateFakeBundlePayload();
        var provider = new TestBundlePayloadProvider(payload);

        // Use the real LayoutDiscovery (not TestLayoutDiscovery) so the post-extract
        // probe must actually find the freshly extracted bundle via the Aspire-home
        // fallback. Relative-to-CLI discovery must fail (binary lives outside any
        // layout) for the home probe to be exercised.
        var layoutDiscovery = new LayoutDiscovery(NullLogger<LayoutDiscovery>.Instance)
        {
            ProcessPathOverride = processPath
        };
        var service = CreateService(provider, layoutDiscovery, processPath);

        var originalAspireHome = Environment.GetEnvironmentVariable(CliPathHelper.AspireHomeEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(CliPathHelper.AspireHomeEnvironmentVariable, aspireHome);

            using var layoutLease = await service.EnsureExtractedAndAcquireLayoutAsync("test", "sidecarless-flow");

            Assert.NotNull(layoutLease);
            Assert.True(layoutLease!.HasLease);

            // Bundle should have been extracted under $ASPIRE_HOME (not next to the CLI binary).
            var bundleLink = Path.Combine(aspireHome, BundleDiscovery.BundleDirectoryName);
            Assert.True(ReparsePoint.IsReparsePoint(bundleLink),
                $"bundle/ should be a reparse point under $ASPIRE_HOME ({aspireHome})");

            var managedExe = Path.Combine(bundleLink,
                BundleDiscovery.ManagedDirectoryName,
                BundleDiscovery.GetExecutableFileName(BundleDiscovery.ManagedExecutableName));
            Assert.True(File.Exists(managedExe), $"managed exe should exist at {managedExe}");

            // No stray extraction should have leaked next to the CLI binary itself.
            Assert.False(
                Directory.Exists(Path.Combine(binaryDir, BundleDiscovery.BundleDirectoryName)),
                "bundle/ must not be created next to the sidecar-less CLI binary.");
            Assert.False(
                Directory.Exists(Path.Combine(Path.GetDirectoryName(binaryDir)!, BundleDiscovery.BundleDirectoryName)),
                "bundle/ must not be created in the parent of the sidecar-less binary directory.");

            // The resolved layout must point into the active version directory under $ASPIRE_HOME,
            // proving the post-extract discovery succeeded via the Aspire-home probe rather than a
            // cached lookup.
            var versionsDir = Path.Combine(aspireHome, BundleService.VersionsDirectoryName);
            var activeVersionDir = Assert.Single(Directory.GetDirectories(versionsDir));

            var managedPath = layoutLease.Layout.GetManagedPath();
            Assert.NotNull(managedPath);

            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            Assert.StartsWith(
                Path.GetFullPath(activeVersionDir),
                Path.GetFullPath(managedPath!),
                comparison);
        }
        finally
        {
            Environment.SetEnvironmentVariable(CliPathHelper.AspireHomeEnvironmentVariable, originalAspireHome);
            CleanupReparsePoints(aspireHome);
        }
    }

    /// <summary>
    /// Verifies that the static <see cref="BundleService.ExtractPayloadAsync(Stream, string, CancellationToken)"/>
    /// overload correctly extracts a tar.gz stream with strip-components=1 behavior.
    /// </summary>
    [Fact]
    public async Task ExtractPayloadAsync_Static_ExtractsTarGzWithStripComponents()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var dest = workspace.WorkspaceRoot.FullName;
        var payload = CreateFakeBundlePayload("test-content");

        using var stream = new MemoryStream(payload);
        await BundleService.ExtractPayloadAsync(stream, dest, CancellationToken.None);

        // Verify strip-components=1 removed the wrapper directory.
        var managedExe = Path.Combine(dest, BundleDiscovery.ManagedDirectoryName,
            BundleDiscovery.GetExecutableFileName(BundleDiscovery.ManagedExecutableName));
        Assert.True(File.Exists(managedExe));

        var content = File.ReadAllText(managedExe);
        Assert.Contains("test-content", content);

        Assert.True(Directory.Exists(Path.Combine(dest, BundleDiscovery.DcpDirectoryName)));
    }

    /// <summary>
    /// On Windows, verifies that upgrading replaces existing reparse points with
    /// new targets pointing at the updated versioned directory.
    /// </summary>
    [Fact]
    public async Task ExtractAsync_ReplacesReparsePointsOnUpgrade()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var layoutRoot = workspace.WorkspaceRoot.FullName;
        var layoutDiscovery = new TestLayoutDiscovery(layoutRoot);

        // Create fake CLI binaries with different fingerprints.
        var v1BinaryPath = CreateFakeCliBinary(Path.Combine(layoutRoot, ".fake-bins"), "aspire-v1", "cli-v1");
        var v2BinaryPath = CreateFakeCliBinary(Path.Combine(layoutRoot, ".fake-bins"), "aspire-v2", "cli-v2-longer");
        File.SetLastWriteTimeUtc(v2BinaryPath, File.GetLastWriteTimeUtc(v1BinaryPath).AddHours(1));

        // Install v1.
        var v1Payload = CreateFakeBundlePayload("v1");
        var v1Service = CreateService(new TestBundlePayloadProvider(v1Payload), layoutDiscovery, v1BinaryPath);
        await v1Service.ExtractAsync(layoutRoot, force: true);

        var bundleLink = Path.Combine(layoutRoot, BundleDiscovery.BundleDirectoryName);
        var v1Target = ReparsePoint.GetTarget(bundleLink);
        Assert.NotNull(v1Target);

        try
        {
            // Install v2.
            var v2Payload = CreateFakeBundlePayload("v2");
            var v2Service = CreateService(new TestBundlePayloadProvider(v2Payload), layoutDiscovery, v2BinaryPath);
            await v2Service.ExtractAsync(layoutRoot, force: true);

            var v2Target = ReparsePoint.GetTarget(bundleLink);
            Assert.NotNull(v2Target);
            Assert.NotEqual(v1Target, v2Target);
        }
        finally
        {
            CleanupReparsePoints(layoutRoot);
        }
    }

    /// <summary>
    /// Creates a tar.gz byte array containing a fake bundle layout with the
    /// required wrapper directory for strip-components=1 extraction.
    /// </summary>
    internal static byte[] CreateFakeBundlePayload(string contentMarker = "fake-bundle")
    {
        using var ms = new MemoryStream();

        using (var gzip = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(gzip, leaveOpen: true))
        {
            // Top-level wrapper directory (stripped during extraction).
            tar.WriteEntry(new PaxTarEntry(TarEntryType.Directory, "aspire-payload/"));

            // managed/ directory and executable.
            tar.WriteEntry(new PaxTarEntry(TarEntryType.Directory, $"aspire-payload/{BundleDiscovery.ManagedDirectoryName}/"));

            var exeName = BundleDiscovery.GetExecutableFileName(BundleDiscovery.ManagedExecutableName);
            var managedEntry = new PaxTarEntry(TarEntryType.RegularFile, $"aspire-payload/{BundleDiscovery.ManagedDirectoryName}/{exeName}")
            {
                DataStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes($"#!/bin/sh\necho {contentMarker}\n"))
            };
            tar.WriteEntry(managedEntry);

            // dcp/ directory with a placeholder file.
            tar.WriteEntry(new PaxTarEntry(TarEntryType.Directory, $"aspire-payload/{BundleDiscovery.DcpDirectoryName}/"));

            var dcpEntry = new PaxTarEntry(TarEntryType.RegularFile, $"aspire-payload/{BundleDiscovery.DcpDirectoryName}/dcp-placeholder")
            {
                DataStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes($"dcp-{contentMarker}\n"))
            };
            tar.WriteEntry(dcpEntry);
        }

        return ms.ToArray();
    }

    /// <summary>
    /// Removes reparse points created during tests to prevent
    /// <see cref="TemporaryWorkspace.Dispose"/> from following them
    /// and deleting target contents.
    /// </summary>
    private static void CleanupReparsePoints(string layoutRoot)
    {
        foreach (var dir in BundleService.s_linkedLayoutDirectories)
        {
            var linkPath = Path.Combine(layoutRoot, dir);
            ReparsePoint.RemoveIfExists(linkPath);
        }
    }

    private static BundleService CreateService(TestBundlePayloadProvider provider, ILayoutDiscovery layoutDiscovery, string? processPathOverride = null)
    {
        return new BundleService(provider, layoutDiscovery, NullLogger<BundleService>.Instance)
        {
            ProcessPathOverride = processPathOverride
        };
    }

    /// <summary>
    /// Creates a fake CLI binary file with the given content and returns its path.
    /// Different content produces different version fingerprints.
    /// </summary>
    private static string CreateFakeCliBinary(string directory, string name, string content)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>
    /// A layout discovery implementation that discovers from a specific root path,
    /// checking for bundle/managed/ and bundle/dcp/ subdirectories.
    /// </summary>
    private sealed class TestLayoutDiscovery(string layoutRoot) : ILayoutDiscovery
    {
        public LayoutConfiguration? DiscoverLayout(string? projectDirectory = null)
        {
            var bundleDir = Path.Combine(layoutRoot, BundleDiscovery.BundleDirectoryName);
            var managedDir = Path.Combine(bundleDir, BundleDiscovery.ManagedDirectoryName);
            var dcpDir = Path.Combine(bundleDir, BundleDiscovery.DcpDirectoryName);
            var managedExeName = BundleDiscovery.GetExecutableFileName(BundleDiscovery.ManagedExecutableName);
            var managedExe = Path.Combine(managedDir, managedExeName);

            if (!Directory.Exists(managedDir) || !File.Exists(managedExe) || !Directory.Exists(dcpDir))
            {
                return null;
            }

            return new LayoutConfiguration
            {
                LayoutPath = layoutRoot,
                Components = new LayoutComponents
                {
                    Managed = Path.Combine(BundleDiscovery.BundleDirectoryName, BundleDiscovery.ManagedDirectoryName),
                    Dcp = Path.Combine(BundleDiscovery.BundleDirectoryName, BundleDiscovery.DcpDirectoryName),
                }
            };
        }

        public string? GetComponentPath(LayoutComponent component, string? projectDirectory = null)
        {
            var bundleDir = Path.Combine(layoutRoot, BundleDiscovery.BundleDirectoryName);
            return component switch
            {
                LayoutComponent.Managed => Path.Combine(bundleDir, BundleDiscovery.ManagedDirectoryName,
                    BundleDiscovery.GetExecutableFileName(BundleDiscovery.ManagedExecutableName)),
                LayoutComponent.Dcp => Path.Combine(bundleDir, BundleDiscovery.DcpDirectoryName),
                _ => null,
            };
        }

        public bool IsBundleModeAvailable(string? projectDirectory = null) => true;
    }
}
