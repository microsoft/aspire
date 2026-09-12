// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using Aspire.Cli.Agents;
using Aspire.Cli.Tests.TestServices;

namespace Aspire.Cli.Tests.Agents;

public class AgentAssetFileInstallerTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task InstallAsync_PreservesBinaryBytesAndSkipsEquivalentText()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var root = workspace.CreateDirectory("root");
        var asset = CreateAsset(AgentAssetKind.Extension);
        var binaryBytes = new byte[] { 0, 255, 254, 128, 13, 10, 42 };
        AgentAssetFile[] files =
        [
            new(Path.Combine("ui", "icon.bin"), binaryBytes, AgentAssetFileComparison.ExactBytes),
            new("index.js", "first\nsecond\n")
        ];
        var assetPath = Path.Combine(root.FullName, "assets", asset.Name);
        var binaryPath = Path.Combine(assetPath, "ui", "icon.bin");
        var textPath = Path.Combine(assetPath, "index.js");
        var cancellationToken = TestContext.Current.CancellationToken;

        Assert.True(await AgentAssetFileInstaller.InstallAsync(root, "assets", asset, files, cancellationToken));
        Assert.Equal(binaryBytes, await File.ReadAllBytesAsync(binaryPath, cancellationToken));
        Assert.Equal(files[1].Bytes.ToArray(), await File.ReadAllBytesAsync(textPath, cancellationToken));

        var equivalentTextBytes = Encoding.UTF8.GetBytes("\uFEFFfirst\r\nsecond\r\n");
        await File.WriteAllBytesAsync(textPath, equivalentTextBytes, cancellationToken);
        File.SetLastWriteTimeUtc(binaryPath, new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(textPath, new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var binaryWriteTime = File.GetLastWriteTimeUtc(binaryPath);
        var textWriteTime = File.GetLastWriteTimeUtc(textPath);

        Assert.False(await AgentAssetFileInstaller.InstallAsync(root, "assets", asset, files, cancellationToken));
        Assert.Equal(binaryBytes, await File.ReadAllBytesAsync(binaryPath, cancellationToken));
        Assert.Equal(equivalentTextBytes, await File.ReadAllBytesAsync(textPath, cancellationToken));
        Assert.Equal(binaryWriteTime, File.GetLastWriteTimeUtc(binaryPath));
        Assert.Equal(textWriteTime, File.GetLastWriteTimeUtc(textPath));

        await File.WriteAllBytesAsync(binaryPath, new byte[] { 0, 255 }, cancellationToken);

        Assert.True(await AgentAssetFileInstaller.InstallAsync(root, "assets", asset, files, cancellationToken));
        Assert.Equal(binaryBytes, await File.ReadAllBytesAsync(binaryPath, cancellationToken));
        Assert.Equal(equivalentTextBytes, await File.ReadAllBytesAsync(textPath, cancellationToken));
        Assert.Equal(textWriteTime, File.GetLastWriteTimeUtc(textPath));
        AssertTransactionDirectoriesRemoved(root, asset);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InstallAsync_RemovesStaleFilesOnlyForExtensions(bool extension)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var root = workspace.CreateDirectory("root");
        var asset = CreateAsset(extension ? AgentAssetKind.Extension : AgentAssetKind.Skill);
        var assetPath = root.CreateSubdirectory(Path.Combine("assets", asset.Name));
        var staleDirectory = assetPath.CreateSubdirectory("old");
        var cancellationToken = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(Path.Combine(assetPath.FullName, "index.js"), "old", cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(staleDirectory.FullName, "script.js"), "stale", cancellationToken);
        var otherAsset = root.CreateSubdirectory(Path.Combine("assets", "other"));
        await File.WriteAllTextAsync(Path.Combine(otherAsset.FullName, "keep.txt"), "other asset", cancellationToken);
        AgentAssetFile[] files = [new("index.js", "new")];

        Assert.True(await AgentAssetFileInstaller.InstallAsync(root, "assets", asset, files, cancellationToken));
        Assert.Equal("new", await File.ReadAllTextAsync(Path.Combine(assetPath.FullName, "index.js"), cancellationToken));
        var installedFiles = Directory.GetFiles(assetPath.FullName, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(assetPath.FullName, path))
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(extension ? ["index.js"] : new[] { "index.js", Path.Combine("old", "script.js") }, installedFiles);
        if (!extension)
        {
            Assert.Equal("stale", await File.ReadAllTextAsync(Path.Combine(staleDirectory.FullName, "script.js"), cancellationToken));
        }

        Assert.Equal("other asset", await File.ReadAllTextAsync(Path.Combine(otherAsset.FullName, "keep.txt"), cancellationToken));
        Assert.False(await AgentAssetFileInstaller.InstallAsync(root, "assets", asset, files, cancellationToken));
        AssertTransactionDirectoriesRemoved(root, asset);
    }

    [Fact]
    public async Task InstallAsync_ReportsUpdateWhenOnlyStaleExtensionFilesChange()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var root = workspace.CreateDirectory("root");
        var asset = CreateAsset(AgentAssetKind.Extension);
        var assetPath = root.CreateSubdirectory(Path.Combine("assets", asset.Name));
        var cancellationToken = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(Path.Combine(assetPath.FullName, "index.js"), "current", cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(assetPath.FullName, "stale.js"), "stale", cancellationToken);
        AgentAssetFile[] files = [new("index.js", "current")];

        Assert.True(await AgentAssetFileInstaller.InstallAsync(root, "assets", asset, files, cancellationToken));
        Assert.Equal(["index.js"], assetPath.GetFiles().Select(file => file.Name));
        Assert.False(await AgentAssetFileInstaller.InstallAsync(root, "assets", asset, files, cancellationToken));
        AssertTransactionDirectoriesRemoved(root, asset);
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
        var asset = CreateAsset(AgentAssetKind.Extension);
        var assetPath = root.CreateSubdirectory(Path.Combine("assets", asset.Name));
        var originalPath = Path.Combine(assetPath.FullName, "index.js");
        var outsidePath = Path.Combine(root.FullName, "outside.txt");
        var cancellationToken = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(originalPath, "original", cancellationToken);
        await File.WriteAllTextAsync(outsidePath, "outside", cancellationToken);
        AgentAssetFile[] files = [new("index.js", "replacement"), new(relativePath, "invalid")];

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AgentAssetFileInstaller.InstallAsync(root, "assets", asset, files, cancellationToken));

        Assert.Equal("original", await File.ReadAllTextAsync(originalPath, cancellationToken));
        Assert.Equal("outside", await File.ReadAllTextAsync(outsidePath, cancellationToken));
        Assert.Equal(["index.js"], assetPath.GetFiles().Select(file => file.Name));
        AssertTransactionDirectoriesRemoved(root, asset);
    }

    [Theory]
    [InlineData("../outside", "example")]
    [InlineData("assets", "../outside")]
    [InlineData("assets", "nested/example")]
    public async Task InstallAsync_RejectsEscapingAssetDirectories(string relativeDirectory, string assetName)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var root = workspace.CreateDirectory("root");
        var asset = CreateAsset(AgentAssetKind.Extension, assetName);
        AgentAssetFile[] files = [new("index.js", "replacement")];

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AgentAssetFileInstaller.InstallAsync(root, relativeDirectory, asset, files, TestContext.Current.CancellationToken));

        Assert.Empty(root.GetFileSystemInfos());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InstallAsync_BlockedDestinationPreservesOriginalFiles(bool blockedByDirectory)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var root = workspace.CreateDirectory("root");
        var asset = CreateAsset(AgentAssetKind.Extension);
        var assetPath = root.CreateSubdirectory(Path.Combine("assets", asset.Name));
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
            AgentAssetFileInstaller.InstallAsync(root, "assets", asset, files, cancellationToken));

        Assert.Equal("original", await File.ReadAllTextAsync(originalPath, cancellationToken));
        Assert.Equal(["blocked", "index.js"], assetPath.GetFileSystemInfos().Select(entry => entry.Name).Order(StringComparer.Ordinal));
        if (!blockedByDirectory)
        {
            Assert.Equal("blocking file", await File.ReadAllTextAsync(Path.Combine(assetPath.FullName, "blocked"), cancellationToken));
        }

        AssertTransactionDirectoriesRemoved(root, asset);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InstallAsync_PublicationFailureRestoresReplacedAndNewFiles(bool staleLockedFile)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "This test validates Windows delete-sharing behavior.");
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var root = workspace.CreateDirectory("root");
        var asset = CreateAsset(AgentAssetKind.Extension);
        var assetPath = root.CreateSubdirectory(Path.Combine("assets", asset.Name));
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
                AgentAssetFileInstaller.InstallAsync(root, "assets", asset, files, cancellationToken));
        }

        Assert.Equal("original", await File.ReadAllTextAsync(originalPath, cancellationToken));
        Assert.Equal("locked", await File.ReadAllTextAsync(lockedPath, cancellationToken));
        Assert.Equal(["index.js", "locked.js"], assetPath.GetFiles().Select(file => file.Name).Order(StringComparer.Ordinal));
        AssertTransactionDirectoriesRemoved(root, asset);
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
        var asset = CreateAsset(AgentAssetKind.Extension);
        var assetPath = root.CreateSubdirectory(Path.Combine("assets", asset.Name));
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
                AgentAssetFileInstaller.InstallAsync(
                    linkKind == "root" ? new DirectoryInfo(linkPath) : root,
                    linkKind == "asset" ? "linked-assets" : "assets",
                    asset,
                    files,
                    cancellationToken));

            Assert.Equal("outside", await File.ReadAllTextAsync(outsidePath, cancellationToken));
            Assert.Equal("original", await File.ReadAllTextAsync(originalPath, cancellationToken));
            Assert.Equal(["index.js"], outside.GetFiles().Select(file => file.Name));
            AssertTransactionDirectoriesRemoved(root, asset);
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
    public async Task InstallAsync_RejectsSymbolicLinksInStaleExtensionFiles()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var root = workspace.CreateDirectory("root");
        var outside = workspace.CreateDirectory("outside");
        var asset = CreateAsset(AgentAssetKind.Extension);
        var assetPath = root.CreateSubdirectory(Path.Combine("assets", asset.Name));
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
                AgentAssetFileInstaller.InstallAsync(root, "assets", asset, files, cancellationToken));

            Assert.Equal("original", await File.ReadAllTextAsync(originalPath, cancellationToken));
            Assert.Equal("outside", await File.ReadAllTextAsync(outsidePath, cancellationToken));
            AssertTransactionDirectoriesRemoved(root, asset);
        }
        finally
        {
            File.Delete(linkPath);
        }
    }

    private static AgentAssetDefinition CreateAsset(AgentAssetKind kind, string name = "example")
        => AgentAssetDefinition.CreateAspireSkillsBundle(kind, name, "Test asset");

    private static void AssertTransactionDirectoriesRemoved(DirectoryInfo root, AgentAssetDefinition asset)
        => Assert.Empty(Directory.GetDirectories(Path.Combine(root.FullName, "assets"), $".{asset.Name}.*"));
}
