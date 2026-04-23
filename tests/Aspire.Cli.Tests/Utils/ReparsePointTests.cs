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
    public void CreateOrReplace_ReplacesExistingRealDirectoryWhenRemovedFirst()
    {
        // CreateOrReplace expects the caller to have removed any conflicting
        // non-reparse-point entry. Verify it does not follow a real directory.
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var root = workspace.WorkspaceRoot.FullName;

        var real = Path.Combine(root, "link");
        Directory.CreateDirectory(real);
        File.WriteAllText(Path.Combine(real, "legacy"), "legacy");

        var target = Path.Combine(root, "target");
        Directory.CreateDirectory(target);

        // A real directory in the way should prevent the atomic rename-over on
        // most platforms. Callers must delete it first; simulate that.
        Directory.Delete(real, recursive: true);
        ReparsePoint.CreateOrReplace(real, target);

        Assert.True(ReparsePoint.IsReparsePoint(real));
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

        Assert.True(ReparsePoint.IsReparsePoint(link));
        Assert.True(Directory.Exists(link));
        Assert.Equal("hello", File.ReadAllText(Path.Combine(link, "marker")));
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

        // DirectoryInfo.LinkTarget on a junction surfaces the resolved target.
        var linkTarget = ReparsePoint.GetTarget(link);
        Assert.NotNull(linkTarget);
        Assert.Equal(
            Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(linkTarget!).TrimEnd(Path.DirectorySeparatorChar));
    }
}
