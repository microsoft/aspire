// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Utils;

namespace Aspire.Cli.Tests.Utils;

public class ReparsePointTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public void CreateOrReplace_CreatesReparsePointToTargetDirectory()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var root = workspace.WorkspaceRoot.FullName;

        var target = Path.Combine(root, "real");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "marker"), "hello");

        var link = Path.Combine(root, "link");
        ReparsePoint.CreateOrReplace(link, target);

        Assert.True(ReparsePoint.IsReparsePoint(link));
        Assert.True(Directory.Exists(link));
        Assert.Equal("hello", File.ReadAllText(Path.Combine(link, "marker")));
    }

    [Fact]
    public void CreateOrReplace_ReplacesExistingReparsePoint()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var root = workspace.WorkspaceRoot.FullName;

        var target1 = Path.Combine(root, "t1");
        var target2 = Path.Combine(root, "t2");
        Directory.CreateDirectory(target1);
        Directory.CreateDirectory(target2);
        File.WriteAllText(Path.Combine(target1, "id"), "one");
        File.WriteAllText(Path.Combine(target2, "id"), "two");

        var link = Path.Combine(root, "link");
        ReparsePoint.CreateOrReplace(link, target1);
        Assert.Equal("one", File.ReadAllText(Path.Combine(link, "id")));

        ReparsePoint.CreateOrReplace(link, target2);
        Assert.Equal("two", File.ReadAllText(Path.Combine(link, "id")));
        Assert.True(ReparsePoint.IsReparsePoint(link));
    }

    [Fact]
    public void CreateOrReplace_ThrowsWhenExistingPathIsRealDirectory()
    {
        // CreateOrReplace must refuse to remove a real directory to prevent
        // accidental data loss. Callers must handle migration explicitly.
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var root = workspace.WorkspaceRoot.FullName;

        var real = Path.Combine(root, "link");
        Directory.CreateDirectory(real);
        File.WriteAllText(Path.Combine(real, "legacy"), "legacy");

        var target = Path.Combine(root, "target");
        Directory.CreateDirectory(target);

        // A real directory at the link path should throw — callers must remove
        // or migrate it before calling CreateOrReplace.
        Assert.Throws<InvalidOperationException>(() => ReparsePoint.CreateOrReplace(real, target));

        // The real directory and its contents must be preserved.
        Assert.True(Directory.Exists(real));
        Assert.True(File.Exists(Path.Combine(real, "legacy")));
    }

    [Fact]
    public void IsReparsePoint_ReturnsFalseForRegularDirectory()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var dir = Path.Combine(workspace.WorkspaceRoot.FullName, "plain");
        Directory.CreateDirectory(dir);

        Assert.False(ReparsePoint.IsReparsePoint(dir));
    }

    [Fact]
    public void IsReparsePoint_ReturnsFalseForMissingPath()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        Assert.False(ReparsePoint.IsReparsePoint(Path.Combine(workspace.WorkspaceRoot.FullName, "nope")));
    }

    [Fact]
    public void RemoveIfExists_RemovesReparsePointWithoutTouchingTarget()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var root = workspace.WorkspaceRoot.FullName;

        var target = Path.Combine(root, "target");
        Directory.CreateDirectory(target);
        var markerPath = Path.Combine(target, "keep");
        File.WriteAllText(markerPath, "still here");

        var link = Path.Combine(root, "link");
        ReparsePoint.CreateOrReplace(link, target);

        ReparsePoint.RemoveIfExists(link);

        Assert.False(Directory.Exists(link));
        Assert.True(File.Exists(markerPath));
    }

    [Fact]
    public void RemoveIfExists_RemovesRegularDirectoryRecursively()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var dir = Path.Combine(workspace.WorkspaceRoot.FullName, "plain");
        Directory.CreateDirectory(Path.Combine(dir, "nested"));
        File.WriteAllText(Path.Combine(dir, "nested", "f"), "x");

        ReparsePoint.RemoveIfExists(dir);

        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public void RemoveIfExists_DoesNothingForMissingPath()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        ReparsePoint.RemoveIfExists(Path.Combine(workspace.WorkspaceRoot.FullName, "missing"));
    }

    [Fact]
    public void ResolveTargetPath_ResolvesRelativeTargetAgainstLinkDirectory()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var root = workspace.WorkspaceRoot.FullName;

        var link = Path.Combine(root, "bundle");
        var target = Path.Combine(root, "versions", "v1");

        var resolvedTarget = ReparsePoint.ResolveTargetPath(link, Path.Combine("versions", "v1"));

        Assert.Equal(Path.GetFullPath(target), resolvedTarget);
    }

    [Fact]
    public void CanFollowDirectoryReparsePoint_ReturnsFalseWhenSymlinkTargetCannotBeOpened()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var root = workspace.WorkspaceRoot.FullName;

        var link = Path.Combine(root, "bundle");
        try
        {
            Directory.CreateSymbolicLink(link, Path.Combine("versions", "missing"));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            Assert.Skip("Symlink creation is not available (Developer Mode not enabled or not running as admin).");
            return;
        }

        try
        {
            Assert.False(ReparsePoint.CanFollowDirectoryReparsePoint(link));
        }
        finally
        {
            ReparsePoint.RemoveIfExists(link);
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // Windows-specific: explicitly exercise the junction code path.
    //
    // On a Windows machine with Developer Mode enabled (or when running
    // elevated) Directory.CreateSymbolicLink succeeds, so the junction
    // fallback inside CreateOrReplace never executes. These tests call
    // CreateWindowsJunction directly to ensure the reparse-buffer
    // layout and FSCTL_SET_REPARSE_POINT path remain correct regardless
    // of dev-mode state on the build/test machine.
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void CreateWindowsJunction_CreatesReparsePointToTargetDirectory()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Directory junctions are Windows-only.");

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var root = workspace.WorkspaceRoot.FullName;

        var target = Path.Combine(root, "real");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "marker"), "hello");

        var link = Path.Combine(root, "junction");
#pragma warning disable CA1416
        ReparsePoint.CreateWindowsJunction(link, target);
#pragma warning restore CA1416

        try
        {
            Assert.True(ReparsePoint.IsReparsePoint(link));
            Assert.True(Directory.Exists(link));
            Assert.Equal("hello", File.ReadAllText(Path.Combine(link, "marker")));
        }
        finally
        {
            // Remove the junction before workspace disposal — Directory.Delete(recursive: true)
            // follows through junctions on Windows and would try to delete the target contents.
            ReparsePoint.RemoveIfExists(link);
        }
    }

    [Fact]
    public void CreateWindowsJunction_TargetIsReachableThroughLink()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Directory junctions are Windows-only.");

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var root = workspace.WorkspaceRoot.FullName;

        var target = Path.Combine(root, "real");
        var nestedDir = Path.Combine(target, "nested");
        Directory.CreateDirectory(nestedDir);
        File.WriteAllText(Path.Combine(nestedDir, "data.txt"), "payload");

        var link = Path.Combine(root, "junction");
#pragma warning disable CA1416
        ReparsePoint.CreateWindowsJunction(link, target);
#pragma warning restore CA1416

        try
        {
            // Directory enumeration through the junction should see the real content.
            var nestedThroughLink = Path.Combine(link, "nested");
            Assert.True(Directory.Exists(nestedThroughLink));
            Assert.Equal("payload", File.ReadAllText(Path.Combine(nestedThroughLink, "data.txt")));

            // Enumerating files through the junction should surface the nested tree.
            var enumerated = Directory.EnumerateFiles(link, "*", SearchOption.AllDirectories)
                .Select(p => Path.GetFileName(p))
                .ToArray();
            Assert.Contains("data.txt", enumerated);
        }
        finally
        {
            // Remove the junction before workspace disposal — Directory.Delete(recursive: true)
            // follows through junctions on Windows and would try to delete the target contents.
            ReparsePoint.RemoveIfExists(link);
        }
    }

    [Fact]
    public void CreateWindowsJunction_CanBeRemovedWithoutTouchingTarget()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Directory junctions are Windows-only.");

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var root = workspace.WorkspaceRoot.FullName;

        var target = Path.Combine(root, "real");
        Directory.CreateDirectory(target);
        var markerPath = Path.Combine(target, "keep");
        File.WriteAllText(markerPath, "still here");

        var link = Path.Combine(root, "junction");
#pragma warning disable CA1416
        ReparsePoint.CreateWindowsJunction(link, target);
#pragma warning restore CA1416

        ReparsePoint.RemoveIfExists(link);

        Assert.False(Directory.Exists(link));
        Assert.True(File.Exists(markerPath));
    }

    [Fact]
    public void CreateWindowsJunction_ReportsCorrectTarget()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Directory junctions are Windows-only.");

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var root = workspace.WorkspaceRoot.FullName;

        var target = Path.Combine(root, "real");
        Directory.CreateDirectory(target);

        var link = Path.Combine(root, "junction");
#pragma warning disable CA1416
        ReparsePoint.CreateWindowsJunction(link, target);
#pragma warning restore CA1416

        try
        {
            // DirectoryInfo.LinkTarget on a junction surfaces the resolved target.
            var linkTarget = ReparsePoint.GetTarget(link);
            Assert.NotNull(linkTarget);
            Assert.Equal(
                Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(linkTarget!).TrimEnd(Path.DirectorySeparatorChar));
        }
        finally
        {
            // Remove the junction before workspace disposal — Directory.Delete(recursive: true)
            // follows through junctions on Windows and would try to delete the target contents.
            ReparsePoint.RemoveIfExists(link);
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // Windows-specific: junction → symlink migration tests.
    //
    // When a CLI install was originally created without Developer Mode (so
    // junctions were used), and the user later enables Developer Mode, a
    // subsequent CreateOrReplace call should transparently replace the
    // junction with a symlink. These tests verify:
    //   1) CreateOrReplace can replace an existing junction (deterministic)
    //   2) The replacement is a symlink when symlink creation is available
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void CreateOrReplace_ReplacesExistingJunction()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Directory junctions are Windows-only.");

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var root = workspace.WorkspaceRoot.FullName;

        var targetV1 = Path.Combine(root, "v1");
        var targetV2 = Path.Combine(root, "v2");
        Directory.CreateDirectory(targetV1);
        Directory.CreateDirectory(targetV2);
        File.WriteAllText(Path.Combine(targetV1, "id"), "one");
        File.WriteAllText(Path.Combine(targetV2, "id"), "two");

        var link = Path.Combine(root, "link");

        // Simulate a pre-dev-mode install that created a junction.
#pragma warning disable CA1416
        ReparsePoint.CreateWindowsJunction(link, targetV1);
        Assert.Equal(Win32Constants.IO_REPARSE_TAG_MOUNT_POINT, ReparsePoint.GetReparseTag(link));
#pragma warning restore CA1416
        Assert.Equal("one", File.ReadAllText(Path.Combine(link, "id")));

        try
        {
            // Act: CreateOrReplace should replace the junction regardless of
            // whether the new link is a symlink or another junction.
            ReparsePoint.CreateOrReplace(link, targetV2);

            // Assert: link now resolves to v2.
            Assert.True(ReparsePoint.IsReparsePoint(link));
            Assert.Equal("two", File.ReadAllText(Path.Combine(link, "id")));

            var resolvedTarget = ReparsePoint.GetTarget(link);
            Assert.NotNull(resolvedTarget);
            Assert.Equal(
                Path.GetFullPath(targetV2).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(resolvedTarget!).TrimEnd(Path.DirectorySeparatorChar));

            // v1 directory must still be intact — removing a reparse point must
            // never delete the target directory's contents.
            Assert.True(File.Exists(Path.Combine(targetV1, "id")));
        }
        finally
        {
            // Remove the reparse point before workspace disposal — if CreateOrReplace
            // failed, the original junction is still live and Directory.Delete(recursive: true)
            // would follow through it.
            ReparsePoint.RemoveIfExists(link);
        }
    }

    [Fact]
    public void CreateOrReplace_MigratesJunctionToSymlink_WhenSymlinksAreAvailable()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Directory junctions are Windows-only.");

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var root = workspace.WorkspaceRoot.FullName;

        // Probe: can we create and evaluate symlinks on this machine? If not, skip —
        // CreateOrReplace should fall back to a junction and this test cannot assert
        // that a symlink was created.
        var probe = Path.Combine(root, "symlink-probe");
        var probeTarget = Path.Combine(root, "probe-target");
        Directory.CreateDirectory(probeTarget);
        try
        {
            Directory.CreateSymbolicLink(probe, probeTarget);
            if (!ReparsePoint.CanFollowDirectoryReparsePoint(probe))
            {
                Assert.Skip("Symlink evaluation is not available on this machine.");
                return;
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            Assert.Skip("Symlink creation is not available (Developer Mode not enabled or not running as admin).");
            return;
        }
        finally
        {
            ReparsePoint.RemoveIfExists(probe);
            Directory.Delete(probeTarget);
        }

        // Arrange: create targets for the old and new version.
        var targetV1 = Path.Combine(root, "v1");
        var targetV2 = Path.Combine(root, "v2");
        Directory.CreateDirectory(targetV1);
        Directory.CreateDirectory(targetV2);
        File.WriteAllText(Path.Combine(targetV1, "id"), "one");
        File.WriteAllText(Path.Combine(targetV2, "id"), "two");

        var link = Path.Combine(root, "link");

        // Create the initial junction (simulating a pre-dev-mode install).
#pragma warning disable CA1416
        ReparsePoint.CreateWindowsJunction(link, targetV1);
        Assert.Equal(Win32Constants.IO_REPARSE_TAG_MOUNT_POINT, ReparsePoint.GetReparseTag(link));
#pragma warning restore CA1416

        try
        {
            // Act: CreateOrReplace with symlinks available should produce a symlink.
            ReparsePoint.CreateOrReplace(link, targetV2);

            // Assert: the reparse point was migrated from junction to symlink.
            Assert.True(ReparsePoint.IsReparsePoint(link));
#pragma warning disable CA1416
            Assert.Equal(Win32Constants.IO_REPARSE_TAG_SYMLINK, ReparsePoint.GetReparseTag(link));
#pragma warning restore CA1416
            Assert.Equal("two", File.ReadAllText(Path.Combine(link, "id")));

            // v1 contents remain intact.
            Assert.True(File.Exists(Path.Combine(targetV1, "id")));
        }
        finally
        {
            // Remove the reparse point before workspace disposal.
            ReparsePoint.RemoveIfExists(link);
        }
    }
}
