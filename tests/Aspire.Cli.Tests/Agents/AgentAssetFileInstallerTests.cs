// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using Aspire.Cli.Agents;
using Aspire.Cli.Tests.TestServices;

namespace Aspire.Cli.Tests.Agents;

public class AgentAssetFileInstallerTests(ITestOutputHelper outputHelper)
{
    private const string AssetName = "example";

    [Theory]
    [InlineData(65001)]
    [InlineData(1200)]
    [InlineData(1201)]
    [InlineData(12000)]
    [InlineData(12001)]
    public async Task InstallAsync_SkillsPreserveEquivalentEncodedFiles(int codePage)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var root = workspace.CreateDirectory("root");
        var assetPath = root.CreateSubdirectory(Path.Combine("assets", AssetName));
        var encoding = Encoding.GetEncoding(codePage);
        var existingBytes = encoding.GetPreamble().Concat(encoding.GetBytes("first\r\nsecond\r\n")).ToArray();
        AgentAssetFile[] files = [new("SKILL.md", "first\nsecond\n"), new("helper.py", "first\nsecond\n")];
        var timestamp = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var cancellationToken = TestContext.Current.CancellationToken;
        foreach (var file in files)
        {
            var path = Path.Combine(assetPath.FullName, file.RelativePath);
            await File.WriteAllBytesAsync(path, existingBytes, cancellationToken);
            File.SetLastWriteTimeUtc(path, timestamp);
        }

        Assert.False(await AgentAssetFileInstaller.Additive.InstallAsync(root, "assets", AssetName, files, cancellationToken));

        foreach (var file in files)
        {
            var path = Path.Combine(assetPath.FullName, file.RelativePath);
            Assert.Equal(existingBytes, await File.ReadAllBytesAsync(path, cancellationToken));
            Assert.Equal(timestamp, File.GetLastWriteTimeUtc(path));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InstallAsync_PreservesBinaryBytesAndSkipsEquivalentText(bool managedDirectory)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var root = workspace.CreateDirectory("root");
        var installer = managedDirectory ? AgentAssetFileInstaller.ManagedDirectory : AgentAssetFileInstaller.Additive;
        var binaryBytes = new byte[] { 0, 255, 254, 128, 13, 10, 42 };
        var scriptBytes = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("Write-Host 'Hello'")).ToArray();
        AgentAssetFile[] files =
        [
            new(Path.Combine("ui", "icon.bin"), binaryBytes, AgentAssetFileComparison.ExactBytes),
            new("index.js", "first\nsecond\n"),
            new("script.ps1", scriptBytes, AgentAssetFileComparison.NormalizedUtf8Text)
        ];
        var assetPath = Path.Combine(root.FullName, "assets", AssetName);
        var binaryPath = Path.Combine(assetPath, "ui", "icon.bin");
        var textPath = Path.Combine(assetPath, "index.js");
        var scriptPath = Path.Combine(assetPath, "script.ps1");
        var cancellationToken = TestContext.Current.CancellationToken;

        Assert.True(await installer.InstallAsync(root, "assets", AssetName, files, cancellationToken));
        Assert.Equal(binaryBytes, await File.ReadAllBytesAsync(binaryPath, cancellationToken));
        Assert.Equal(files[1].Bytes.ToArray(), await File.ReadAllBytesAsync(textPath, cancellationToken));
        Assert.Equal(scriptBytes, await File.ReadAllBytesAsync(scriptPath, cancellationToken));

        var equivalentTextBytes = Encoding.UTF8.GetBytes("\uFEFFfirst\r\nsecond\r\n");
        await File.WriteAllBytesAsync(textPath, equivalentTextBytes, cancellationToken);
        File.SetLastWriteTimeUtc(binaryPath, new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(textPath, new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(scriptPath, new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var binaryWriteTime = File.GetLastWriteTimeUtc(binaryPath);
        var textWriteTime = File.GetLastWriteTimeUtc(textPath);
        var scriptWriteTime = File.GetLastWriteTimeUtc(scriptPath);

        Assert.False(await installer.InstallAsync(root, "assets", AssetName, files, cancellationToken));
        Assert.Equal(binaryBytes, await File.ReadAllBytesAsync(binaryPath, cancellationToken));
        Assert.Equal(equivalentTextBytes, await File.ReadAllBytesAsync(textPath, cancellationToken));
        Assert.Equal(binaryWriteTime, File.GetLastWriteTimeUtc(binaryPath));
        Assert.Equal(textWriteTime, File.GetLastWriteTimeUtc(textPath));
        Assert.Equal(scriptBytes, await File.ReadAllBytesAsync(scriptPath, cancellationToken));
        Assert.Equal(scriptWriteTime, File.GetLastWriteTimeUtc(scriptPath));

        await File.WriteAllBytesAsync(binaryPath, new byte[] { 0, 255 }, cancellationToken);

        Assert.True(await installer.InstallAsync(root, "assets", AssetName, files, cancellationToken));
        Assert.Equal(binaryBytes, await File.ReadAllBytesAsync(binaryPath, cancellationToken));
        Assert.Equal(equivalentTextBytes, await File.ReadAllBytesAsync(textPath, cancellationToken));
        Assert.Equal(textWriteTime, File.GetLastWriteTimeUtc(textPath));
        Assert.Equal(scriptWriteTime, File.GetLastWriteTimeUtc(scriptPath));
        AssertTransactionDirectoriesRemoved(root);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InstallAsync_RemovesStaleFilesOnlyForManagedDirectories(bool managedDirectory)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var root = workspace.CreateDirectory("root");
        var installer = managedDirectory ? AgentAssetFileInstaller.ManagedDirectory : AgentAssetFileInstaller.Additive;
        var assetPath = root.CreateSubdirectory(Path.Combine("assets", AssetName));
        var staleDirectory = assetPath.CreateSubdirectory("old");
        var cancellationToken = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(Path.Combine(assetPath.FullName, "index.js"), "old", cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(staleDirectory.FullName, "script.js"), "stale", cancellationToken);
        var otherAsset = root.CreateSubdirectory(Path.Combine("assets", "other"));
        await File.WriteAllTextAsync(Path.Combine(otherAsset.FullName, "keep.txt"), "other asset", cancellationToken);
        AgentAssetFile[] files = [new("index.js", "new")];

        Assert.True(await installer.InstallAsync(root, "assets", AssetName, files, cancellationToken));
        Assert.Equal("new", await File.ReadAllTextAsync(Path.Combine(assetPath.FullName, "index.js"), cancellationToken));
        var installedFiles = Directory.GetFiles(assetPath.FullName, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(assetPath.FullName, path))
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(managedDirectory ? ["index.js"] : new[] { "index.js", Path.Combine("old", "script.js") }, installedFiles);
        if (!managedDirectory)
        {
            Assert.Equal("stale", await File.ReadAllTextAsync(Path.Combine(staleDirectory.FullName, "script.js"), cancellationToken));
        }

        Assert.Equal("other asset", await File.ReadAllTextAsync(Path.Combine(otherAsset.FullName, "keep.txt"), cancellationToken));
        Assert.False(await installer.InstallAsync(root, "assets", AssetName, files, cancellationToken));
        AssertTransactionDirectoriesRemoved(root);
    }

    [Fact]
    public async Task InstallAsync_ReportsUpdateWhenOnlyStaleManagedFilesChange()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var root = workspace.CreateDirectory("root");
        var assetPath = root.CreateSubdirectory(Path.Combine("assets", AssetName));
        var cancellationToken = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(Path.Combine(assetPath.FullName, "index.js"), "current", cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(assetPath.FullName, "stale.js"), "stale", cancellationToken);
        AgentAssetFile[] files = [new("index.js", "current")];

        Assert.True(await AgentAssetFileInstaller.ManagedDirectory.InstallAsync(root, "assets", AssetName, files, cancellationToken));
        Assert.Equal(["index.js"], assetPath.GetFiles().Select(file => file.Name));
        Assert.False(await AgentAssetFileInstaller.ManagedDirectory.InstallAsync(root, "assets", AssetName, files, cancellationToken));
        AssertTransactionDirectoriesRemoved(root);
    }

    [Fact]
    public async Task InstallAsync_SkillsRetainEarlierWritesWhenALaterFileFails()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var root = workspace.CreateDirectory("root");
        var assetPath = root.CreateSubdirectory(Path.Combine("assets", AssetName));
        var originalPath = Path.Combine(assetPath.FullName, "SKILL.md");
        var blockedPath = Path.Combine(assetPath.FullName, "blocked");
        var cancellationToken = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(originalPath, "original", cancellationToken);
        await File.WriteAllTextAsync(blockedPath, "user file", cancellationToken);
        AgentAssetFile[] files = [new("SKILL.md", "replacement"), new(Path.Combine("blocked", "child.md"), "new")];

        await Assert.ThrowsAsync<IOException>(() => AgentAssetFileInstaller.Additive.InstallAsync(
            root, "assets", AssetName, files, cancellationToken));

        Assert.Equal("replacement", await File.ReadAllTextAsync(originalPath, cancellationToken));
        Assert.Equal("user file", await File.ReadAllTextAsync(blockedPath, cancellationToken));
        AssertTransactionDirectoriesRemoved(root);
    }

    [Fact]
    public async Task InstallAsync_SkillsUpdateWithoutDeleteSharing()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "This test validates Windows delete-sharing behavior.");
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var root = workspace.CreateDirectory("root");
        var assetPath = root.CreateSubdirectory(Path.Combine("assets", AssetName));
        var originalPath = Path.Combine(assetPath.FullName, "SKILL.md");
        var cancellationToken = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(originalPath, "original", cancellationToken);
        AgentAssetFile[] files = [new("SKILL.md", "replacement")];

        using (File.Open(originalPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            Assert.True(await AgentAssetFileInstaller.Additive.InstallAsync(root, "assets", AssetName, files, cancellationToken));
        }

        Assert.Equal("replacement", await File.ReadAllTextAsync(originalPath, cancellationToken));
        AssertTransactionDirectoriesRemoved(root);
    }

    [Fact]
    public async Task InstallAsync_SkillsContinueFollowingFileLinks()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var root = workspace.CreateDirectory("root");
        var assetPath = root.CreateSubdirectory(Path.Combine("assets", AssetName));
        var targetPath = Path.Combine(workspace.Path, "original.md");
        var linkPath = Path.Combine(assetPath.FullName, "SKILL.md");
        var cancellationToken = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(targetPath, "original", cancellationToken);
        try
        {
            TestSymlinkHelper.TryCreateSymlink(linkPath, targetPath, isDirectory: false);
            AgentAssetFile[] files = [new("SKILL.md", "replacement")];

            Assert.True(await AgentAssetFileInstaller.Additive.InstallAsync(root, "assets", AssetName, files, cancellationToken));
            Assert.Equal("replacement", await File.ReadAllTextAsync(targetPath, cancellationToken));
            Assert.Equal(targetPath, new FileInfo(linkPath).LinkTarget);
            Assert.False(await AgentAssetFileInstaller.Additive.InstallAsync(root, "assets", AssetName, files, cancellationToken));
        }
        finally
        {
            File.Delete(linkPath);
        }
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData(@"..\outside.txt")]
    [InlineData("/outside.txt")]
    [InlineData(@"C:\outside.txt")]
    [InlineData("nested/../../outside.txt")]
    [InlineData("index.js:stream")]
    [InlineData("nested//file.js")]
    public async Task InstallAsync_RejectsMalformedFilePathsWithoutChangingOriginals(string relativePath)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var root = workspace.CreateDirectory("root");
        var assetPath = root.CreateSubdirectory(Path.Combine("assets", AssetName));
        var originalPath = Path.Combine(assetPath.FullName, "index.js");
        var outsidePath = Path.Combine(root.FullName, "outside.txt");
        var cancellationToken = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(originalPath, "original", cancellationToken);
        await File.WriteAllTextAsync(outsidePath, "outside", cancellationToken);
        AgentAssetFile[] files = [new("index.js", "replacement"), new(relativePath, "invalid")];

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AgentAssetFileInstaller.ManagedDirectory.InstallAsync(root, "assets", AssetName, files, cancellationToken));

        Assert.Equal("original", await File.ReadAllTextAsync(originalPath, cancellationToken));
        Assert.Equal("outside", await File.ReadAllTextAsync(outsidePath, cancellationToken));
        Assert.Equal(["index.js"], assetPath.GetFiles().Select(file => file.Name));
        AssertTransactionDirectoriesRemoved(root);
    }

    [Theory]
    [InlineData("../outside", "example")]
    [InlineData("assets", "../outside")]
    [InlineData("assets", "nested/example")]
    public async Task InstallAsync_RejectsEscapingAssetDirectories(string relativeDirectory, string assetName)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var root = workspace.CreateDirectory("root");
        AgentAssetFile[] files = [new("index.js", "replacement")];

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AgentAssetFileInstaller.ManagedDirectory.InstallAsync(root, relativeDirectory, assetName, files, TestContext.Current.CancellationToken));

        Assert.Empty(root.GetFileSystemInfos());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InstallAsync_BlockedDestinationPreservesOriginalFiles(bool blockedByDirectory)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var root = workspace.CreateDirectory("root");
        var assetPath = root.CreateSubdirectory(Path.Combine("assets", AssetName));
        var originalPath = Path.Combine(assetPath.FullName, "index.js");
        var cancellationToken = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(originalPath, "original", cancellationToken);
        if (blockedByDirectory)
        {
            assetPath.CreateSubdirectory("blocked");
        }
        else
        {
            await File.WriteAllTextAsync(Path.Combine(assetPath.FullName, "blocked"), "blocking file", cancellationToken);
        }

        AgentAssetFile[] files =
        [
            new("index.js", "replacement"),
            new(blockedByDirectory ? "blocked" : Path.Combine("blocked", "child.js"), "new")
        ];

        await Assert.ThrowsAsync<IOException>(() =>
            AgentAssetFileInstaller.ManagedDirectory.InstallAsync(root, "assets", AssetName, files, cancellationToken));

        Assert.Equal("original", await File.ReadAllTextAsync(originalPath, cancellationToken));
        Assert.Equal(["blocked", "index.js"], assetPath.GetFileSystemInfos().Select(entry => entry.Name).Order(StringComparer.Ordinal));
        if (!blockedByDirectory)
        {
            Assert.Equal("blocking file", await File.ReadAllTextAsync(Path.Combine(assetPath.FullName, "blocked"), cancellationToken));
        }

        AssertTransactionDirectoriesRemoved(root);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InstallAsync_PublicationFailureRestoresReplacedAndNewFiles(bool staleLockedFile)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "This test validates Windows delete-sharing behavior.");
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var root = workspace.CreateDirectory("root");
        var assetPath = root.CreateSubdirectory(Path.Combine("assets", AssetName));
        var originalPath = Path.Combine(assetPath.FullName, "index.js");
        var lockedPath = Path.Combine(assetPath.FullName, "locked.js");
        var cancellationToken = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(originalPath, "original", cancellationToken);
        await File.WriteAllTextAsync(lockedPath, "locked", cancellationToken);
        List<AgentAssetFile> files =
        [
            new("index.js", "replacement"),
            new("new.js", "new file")
        ];
        if (!staleLockedFile)
        {
            files.Add(new("locked.js", "replacement"));
        }

        // Reads during staging succeed, but the final rename fails after earlier files
        // have been published. This exercises rollback rather than preflight validation.
        using (File.Open(lockedPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await Assert.ThrowsAsync<IOException>(() =>
                AgentAssetFileInstaller.ManagedDirectory.InstallAsync(root, "assets", AssetName, files, cancellationToken));
        }

        Assert.Equal("original", await File.ReadAllTextAsync(originalPath, cancellationToken));
        Assert.Equal("locked", await File.ReadAllTextAsync(lockedPath, cancellationToken));
        Assert.Equal(["index.js", "locked.js"], assetPath.GetFiles().Select(file => file.Name).Order(StringComparer.Ordinal));
        AssertTransactionDirectoriesRemoved(root);

        Assert.True(await AgentAssetFileInstaller.ManagedDirectory.InstallAsync(root, "assets", AssetName, files, cancellationToken));
        Assert.Equal("replacement", await File.ReadAllTextAsync(originalPath, cancellationToken));
        Assert.Equal("new file", await File.ReadAllTextAsync(Path.Combine(assetPath.FullName, "new.js"), cancellationToken));
        Assert.Equal(
            staleLockedFile ? ["index.js", "new.js"] : new[] { "index.js", "locked.js", "new.js" },
            assetPath.GetFiles().Select(file => file.Name).Order(StringComparer.Ordinal));
        if (!staleLockedFile)
        {
            Assert.Equal("replacement", await File.ReadAllTextAsync(lockedPath, cancellationToken));
        }

        Assert.False(await AgentAssetFileInstaller.ManagedDirectory.InstallAsync(root, "assets", AssetName, files, cancellationToken));
        AssertTransactionDirectoriesRemoved(root);
    }

    [Theory]
    [InlineData("root")]
    [InlineData("asset")]
    [InlineData("directory")]
    [InlineData("file")]
    [InlineData("dangling")]
    public async Task InstallAsync_RejectsSymbolicLinksWithoutChangingTheirTargets(string linkKind)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var root = workspace.CreateDirectory("root");
        var outside = workspace.CreateDirectory("outside");
        var outsidePath = Path.Combine(outside.FullName, "index.js");
        var cancellationToken = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(outsidePath, "outside", cancellationToken);
        var assetPath = root.CreateSubdirectory(Path.Combine("assets", AssetName));
        var originalPath = Path.Combine(assetPath.FullName, "original.js");
        await File.WriteAllTextAsync(originalPath, "original", cancellationToken);
        var linkPath = linkKind switch
        {
            "root" => Path.Combine(workspace.Path, "linked-root"),
            "asset" => Path.Combine(root.FullName, "linked-assets"),
            "directory" => Path.Combine(assetPath.FullName, "linked"),
            _ => Path.Combine(assetPath.FullName, "index.js"),
        };
        var directoryLink = linkKind is "root" or "asset" or "directory";
        var targetPath = directoryLink ? outside.FullName : outsidePath;
        if (linkKind == "dangling")
        {
            targetPath = Path.Combine(outside.FullName, "missing.js");
        }

        try
        {
            TestSymlinkHelper.TryCreateSymlink(linkPath, targetPath, directoryLink);
            AgentAssetFile[] files =
            [
                new("original.js", "replacement"),
                new(linkKind == "directory" ? Path.Combine("linked", "index.js") : "index.js", "replacement")
            ];

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                AgentAssetFileInstaller.ManagedDirectory.InstallAsync(
                    linkKind == "root" ? new DirectoryInfo(linkPath) : root,
                    linkKind == "asset" ? "linked-assets" : "assets",
                    AssetName,
                    files,
                    cancellationToken));

            Assert.Equal("outside", await File.ReadAllTextAsync(outsidePath, cancellationToken));
            Assert.Equal("original", await File.ReadAllTextAsync(originalPath, cancellationToken));
            Assert.Equal(["index.js"], outside.GetFiles().Select(file => file.Name));
            AssertTransactionDirectoriesRemoved(root);
        }
        finally
        {
            // Remove the link explicitly before the shared workspace cleans up directories.
            if (directoryLink && new DirectoryInfo(linkPath).LinkTarget is not null)
            {
                Directory.Delete(linkPath);
            }
            else if (!directoryLink && new FileInfo(linkPath).LinkTarget is not null)
            {
                File.Delete(linkPath);
            }
        }
    }

    [Fact]
    public async Task InstallAsync_RejectsSymbolicLinksInStaleManagedFiles()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var root = workspace.CreateDirectory("root");
        var outside = workspace.CreateDirectory("outside");
        var assetPath = root.CreateSubdirectory(Path.Combine("assets", AssetName));
        var originalPath = Path.Combine(assetPath.FullName, "index.js");
        var outsidePath = Path.Combine(outside.FullName, "keep.js");
        var linkPath = Path.Combine(assetPath.FullName, "stale.js");
        var cancellationToken = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(originalPath, "original", cancellationToken);
        await File.WriteAllTextAsync(outsidePath, "outside", cancellationToken);

        try
        {
            TestSymlinkHelper.TryCreateSymlink(linkPath, outsidePath, isDirectory: false);
            AgentAssetFile[] files = [new("index.js", "replacement")];

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                AgentAssetFileInstaller.ManagedDirectory.InstallAsync(root, "assets", AssetName, files, cancellationToken));

            Assert.Equal("original", await File.ReadAllTextAsync(originalPath, cancellationToken));
            Assert.Equal("outside", await File.ReadAllTextAsync(outsidePath, cancellationToken));
            AssertTransactionDirectoriesRemoved(root);
        }
        finally
        {
            File.Delete(linkPath);
        }
    }

    private static void AssertTransactionDirectoriesRemoved(DirectoryInfo root)
        => Assert.Empty(Directory.GetDirectories(Path.Combine(root.FullName, "assets"), $".{AssetName}.*"));
}
