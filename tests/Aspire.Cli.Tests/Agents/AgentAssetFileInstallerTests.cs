// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using Aspire.Cli.Agents;
using Aspire.Cli.Tests.TestServices;
using Microsoft.DotNet.RemoteExecutor;

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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InstallAsync_WaitsForAnotherProcessBeforeComparingFiles(bool managedDirectory)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var root = workspace.CreateDirectory("root");
        var assetPath = Path.Combine(root.FullName, "assets", AssetName);
        var installer = managedDirectory ? AgentAssetFileInstaller.ManagedDirectory : AgentAssetFileInstaller.Additive;
        AgentAssetFile[] files = [new("index.js", "requested revision"), new("second.js", "requested revision")];
        var cancellationToken = TestContext.Current.CancellationToken;
        var leasePath = AgentAssetFileInstaller.GetDestinationLeasePath(assetPath);
        Assert.True(await installer.InstallAsync(root, "assets", AssetName, files, cancellationToken));
        Assert.Equal(leasePath, AgentAssetFileInstaller.GetDestinationLeasePath(assetPath));
        var readyPath = Path.Combine(workspace.Path, "ready");
        var releasePath = Path.Combine(workspace.Path, "release");

        using var process = RemoteExecutor.Invoke(static (assetPath, readyPath, releasePath) =>
        {
            using var lease = HoldDestinationLease(assetPath);
            File.WriteAllText(readyPath, string.Empty);
            if (!SpinWait.SpinUntil(() => File.Exists(releasePath), TimeSpan.FromSeconds(30)))
            {
                throw new TimeoutException("Timed out waiting to publish the competing asset revision.");
            }

            File.WriteAllText(Path.Combine(assetPath, "index.js"), "competing revision");
            File.WriteAllText(Path.Combine(assetPath, "second.js"), "competing revision");
            File.WriteAllText(Path.Combine(assetPath, "stale.js"), "competing revision");
        }, assetPath, readyPath, releasePath);

        using var waitingCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task<bool>? installation = null;
        try
        {
            Assert.True(
                SpinWait.SpinUntil(() => File.Exists(readyPath), TimeSpan.FromSeconds(30)),
                "Timed out waiting for the child process to acquire the destination lease.");

            // These files initially match. The competing writer changes them only after this
            // invocation starts, so comparing before acquiring the lease would produce a false no-op.
            installation = installer.InstallAsync(root, "assets", AssetName, files, waitingCancellation.Token);
            Assert.False(installation.IsCompleted);
            File.WriteAllText(releasePath, string.Empty);
            Assert.True(await installation.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken));

            var indexPath = Path.Combine(assetPath, "index.js");
            var secondPath = Path.Combine(assetPath, "second.js");
            Assert.Equal(files[0].Bytes.ToArray(), await File.ReadAllBytesAsync(indexPath, cancellationToken));
            Assert.Equal(files[1].Bytes.ToArray(), await File.ReadAllBytesAsync(secondPath, cancellationToken));
            Assert.Equal(
                managedDirectory ? ["index.js", "second.js"] : new[] { "index.js", "second.js", "stale.js" },
                Directory.GetFiles(assetPath).Select(Path.GetFileName).Order(StringComparer.Ordinal));

            var timestamp = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(indexPath, timestamp);
            File.SetLastWriteTimeUtc(secondPath, timestamp);
            for (var attempt = 0; attempt < 3; attempt++)
            {
                Assert.False(await installer.InstallAsync(root, "assets", AssetName, files, cancellationToken));
                Assert.Equal(timestamp, File.GetLastWriteTimeUtc(indexPath));
                Assert.Equal(timestamp, File.GetLastWriteTimeUtc(secondPath));
            }

            Assert.True(File.Exists(leasePath));
            Assert.Equal(0, new FileInfo(leasePath).Length);
            AssertTransactionDirectoriesRemoved(root);
        }
        finally
        {
            File.WriteAllText(releasePath, string.Empty);
            await waitingCancellation.CancelAsync();
            if (installation is not null)
            {
                await ((Task)installation).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
            }
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task InstallAsync_CancellationWhileWaitingDoesNotReadOrMutateDestination(bool managedDirectory, bool assetExists)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var root = workspace.CreateDirectory("root");
        var assetPath = Path.Combine(root.FullName, "assets", AssetName);
        var originalPath = Path.Combine(assetPath, "index.js");
        var cancellationToken = TestContext.Current.CancellationToken;
        if (assetExists)
        {
            Directory.CreateDirectory(assetPath);
            await File.WriteAllTextAsync(originalPath, "original", cancellationToken);
        }

        using var lease = HoldDestinationLease(assetPath);
        // An attempted comparison before acquiring the lease fails immediately, rather than
        // depending on how quickly an asynchronous read or staging write happens to complete.
        using var original = assetExists ? File.Open(originalPath, FileMode.Open, FileAccess.Read, FileShare.None) : null;
        using var waitingCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var installer = managedDirectory ? AgentAssetFileInstaller.ManagedDirectory : AgentAssetFileInstaller.Additive;
        AgentAssetFile[] files = [new("index.js", "replacement"), new("new.js", "new file")];
        var installation = installer.InstallAsync(root, "assets", AssetName, files, waitingCancellation.Token);
        try
        {
            Assert.False(installation.IsCompleted);
            Assert.Equal(assetExists ? ["assets"] : Array.Empty<string>(), root.GetFileSystemInfos().Select(entry => entry.Name));
            await waitingCancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => installation);

            original?.Dispose();
            if (assetExists)
            {
                Assert.Equal("original", await File.ReadAllTextAsync(originalPath, cancellationToken));
                Assert.Equal(["index.js"], Directory.GetFiles(assetPath).Select(Path.GetFileName));
                AssertTransactionDirectoriesRemoved(root);
            }
            else
            {
                Assert.Empty(root.GetFileSystemInfos());
            }
        }
        finally
        {
            await waitingCancellation.CancelAsync();
            await ((Task)installation).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        }
    }

    [Theory]
    [InlineData("root")]
    [InlineData("parent")]
    [InlineData("asset")]
    public async Task InstallAsync_DoesNotBlockUnrelatedAssetDestinations(string differentComponent)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var root = workspace.CreateDirectory("root");
        var assetPath = Path.Combine(root.FullName, "assets", AssetName);
        using var lease = HoldDestinationLease(assetPath);
        var cancellationToken = TestContext.Current.CancellationToken;
        using var waitingCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        AgentAssetFile[] files = [new("index.js", "new file")];
        var waitingInstallation = AgentAssetFileInstaller.ManagedDirectory.InstallAsync(root, "assets", AssetName, files, waitingCancellation.Token);
        Task<bool>? otherInstallation = null;
        try
        {
            Assert.False(waitingInstallation.IsCompleted);
            var otherRoot = differentComponent == "root" ? workspace.CreateDirectory("other-root") : root;
            var otherParent = differentComponent == "parent" ? "other-assets" : "assets";
            var otherName = differentComponent == "asset" ? "other" : AssetName;

            otherInstallation = AgentAssetFileInstaller.Additive.InstallAsync(otherRoot, otherParent, otherName, files, waitingCancellation.Token);
            Assert.True(await otherInstallation.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken));
            Assert.Equal(
                files[0].Bytes.ToArray(),
                await File.ReadAllBytesAsync(Path.Combine(otherRoot.FullName, otherParent, otherName, "index.js"), cancellationToken));
            Assert.False(waitingInstallation.IsCompleted);
            Assert.False(Directory.Exists(assetPath));
        }
        finally
        {
            await waitingCancellation.CancelAsync();
            if (otherInstallation is not null)
            {
                await ((Task)otherInstallation).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
            }

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waitingInstallation);
        }
    }

    [Fact]
    public void GetDestinationLeasePath_NormalizesMissingDirectoryAliases()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var root = workspace.CreateDirectory("root");
        var assetPath = Path.Combine(root.FullName, "assets", AssetName);
        var leasePath = AgentAssetFileInstaller.GetDestinationLeasePath(assetPath);

        Assert.Equal(leasePath, AgentAssetFileInstaller.GetDestinationLeasePath(assetPath + Path.DirectorySeparatorChar));
        Assert.Equal(leasePath, AgentAssetFileInstaller.GetDestinationLeasePath(Path.Combine(root.FullName, "unused", "..", "assets", AssetName)));
        Assert.Equal(leasePath, AgentAssetFileInstaller.GetDestinationLeasePath(Path.Combine(root.FullName, "ASSETS", AssetName.ToUpperInvariant())));
        Assert.Equal(
            AgentAssetFileInstaller.GetDestinationLeasePath(Path.Combine(root.FullName, "assets", "caf\u00e9")),
            AgentAssetFileInstaller.GetDestinationLeasePath(Path.Combine(root.FullName, "assets", "cafe\u0301")));
        Assert.Empty(root.GetFileSystemInfos());
    }

    [Fact]
    public async Task InstallAsync_AncestorDirectoryAliasesShareDestinationLease()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var parent = workspace.CreateDirectory("parent");
        var root = parent.CreateSubdirectory("root");
        var alias = Path.Combine(workspace.Path, "alias");
        var assetPath = Path.Combine(root.FullName, "assets", AssetName);
        try
        {
            TestSymlinkHelper.TryCreateSymlink(alias, parent.FullName, isDirectory: true);
            var aliasedRoot = new DirectoryInfo(Path.Combine(alias, "root"));
            Assert.Equal(
                AgentAssetFileInstaller.GetDestinationLeasePath(assetPath),
                AgentAssetFileInstaller.GetDestinationLeasePath(Path.Combine(aliasedRoot.FullName, "assets", AssetName)));

            using var lease = HoldDestinationLease(assetPath);
            using var waitingCancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            AgentAssetFile[] files = [new("index.js", "new file")];
            var installation = AgentAssetFileInstaller.ManagedDirectory.InstallAsync(aliasedRoot, "assets", AssetName, files, waitingCancellation.Token);
            try
            {
                Assert.False(installation.IsCompleted);
                Assert.Empty(root.GetFileSystemInfos());
            }
            finally
            {
                await waitingCancellation.CancelAsync();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => installation);
            }
        }
        finally
        {
            if (new DirectoryInfo(alias).LinkTarget is not null)
            {
                Directory.Delete(alias);
            }
        }
    }

    [Fact]
    public async Task InstallAsync_InvalidLeaseFileReportsIoErrorWithoutMutatingDestination()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var root = workspace.CreateDirectory("root");
        var leasePath = AgentAssetFileInstaller.GetDestinationLeasePath(Path.Combine(root.FullName, "assets", AssetName));
        Directory.CreateDirectory(leasePath);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            AgentAssetFile[] files = [new("index.js", "new file")];
            await Assert.ThrowsAsync<IOException>(() =>
                AgentAssetFileInstaller.ManagedDirectory.InstallAsync(root, "assets", AssetName, files, cancellation.Token));
            Assert.Empty(root.GetFileSystemInfos());
        }
        finally
        {
            Directory.Delete(leasePath);
        }
    }

    [Fact]
    public async Task InstallAsync_RejectsSymbolicLinkLeaseWithoutChangingTarget()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var root = workspace.CreateDirectory("root");
        var outsidePath = Path.Combine(workspace.Path, "outside");
        var cancellationToken = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(outsidePath, "outside", cancellationToken);
        var leasePath = AgentAssetFileInstaller.GetDestinationLeasePath(Path.Combine(root.FullName, "assets", AssetName));
        Directory.CreateDirectory(Path.GetDirectoryName(leasePath)!);
        try
        {
            TestSymlinkHelper.TryCreateSymlink(leasePath, outsidePath, isDirectory: false);
            AgentAssetFile[] files = [new("index.js", "new file")];
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                AgentAssetFileInstaller.ManagedDirectory.InstallAsync(root, "assets", AssetName, files, cancellationToken));

            Assert.Empty(root.GetFileSystemInfos());
            Assert.Equal("outside", await File.ReadAllTextAsync(outsidePath, cancellationToken));
        }
        finally
        {
            File.Delete(leasePath);
        }
    }

    [Fact]
    public void GetDestinationLeasePath_RejectsManagingLeaseDirectory()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var leasePath = AgentAssetFileInstaller.GetDestinationLeasePath(Path.Combine(workspace.Path, "assets", AssetName));

        Assert.Throws<InvalidOperationException>(() =>
            AgentAssetFileInstaller.GetDestinationLeasePath(Path.GetDirectoryName(leasePath)!));
    }

    [Fact]
    public void InstallAsync_RejectsDisabledUnixFileLockingWithoutMutatingDestination()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The .NET file-locking switch only affects Unix.");
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var root = workspace.CreateDirectory("root");
        var options = new RemoteInvokeOptions();
        options.StartInfo.Environment["DOTNET_SYSTEM_IO_DISABLEFILELOCKING"] = "1";

        using var process = RemoteExecutor.Invoke(static async rootPath =>
        {
            AgentAssetFile[] files = [new("index.js", "new file")];
            var exception = await Assert.ThrowsAsync<IOException>(() => AgentAssetFileInstaller.ManagedDirectory
                .InstallAsync(new DirectoryInfo(rootPath), "assets", AssetName, files, CancellationToken.None));
            var leasePath = AgentAssetFileInstaller.GetDestinationLeasePath(Path.Combine(rootPath, "assets", AssetName));
            Assert.Equal($"Exclusive file locking is required for agent asset lease '{leasePath}'.", exception.Message);
            Assert.Empty(Directory.GetFileSystemEntries(rootPath));
        }, root.FullName, options);
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

    private static FileStream HoldDestinationLease(string assetPath)
    {
        var leasePath = AgentAssetFileInstaller.GetDestinationLeasePath(assetPath);
        Directory.CreateDirectory(Path.GetDirectoryName(leasePath)!);
        return new FileStream(leasePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, bufferSize: 1);
    }

    private static void AssertTransactionDirectoriesRemoved(DirectoryInfo root)
        => Assert.Empty(Directory.GetDirectories(Path.Combine(root.FullName, "assets"), $".{AssetName}.*"));
}
