// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using Aspire.Cli.Agents;
using Aspire.Cli.Tests.TestServices;

namespace Aspire.Cli.Tests.Agents;

public class SkillFileInstallerTests(ITestOutputHelper outputHelper)
{
    private const string SkillName = "example";
    private const string RelativeSkillDirectory = "skills";

    [Theory]
    [InlineData(65001)]
    [InlineData(1200)]
    [InlineData(1201)]
    [InlineData(12000)]
    [InlineData(12001)]
    public async Task InstallAsync_PreservesEquivalentEncodedFiles(int codePage)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var root = workspace.CreateDirectory("root");
        var skillDirectory = root.CreateSubdirectory(Path.Combine(RelativeSkillDirectory, SkillName));
        var encoding = Encoding.GetEncoding(codePage);
        var existingBytes = encoding.GetPreamble().Concat(encoding.GetBytes("first\r\nsecond\r")).ToArray();
        SkillAssetFile[] files =
        [
            new("SKILL.md", "first\nsecond\n"),
            new("helper.py", "first\nsecond\n"),
            new("data.bin", "first\nsecond\n")
        ];
        var timestamp = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var cancellationToken = TestContext.Current.CancellationToken;
        foreach (var file in files)
        {
            var path = Path.Combine(skillDirectory.FullName, file.RelativePath);
            await File.WriteAllBytesAsync(path, existingBytes, cancellationToken);
            File.SetLastWriteTimeUtc(path, timestamp);
        }

        Assert.False(await SkillFileInstaller.Instance.InstallAsync(root, RelativeSkillDirectory, SkillName, files, cancellationToken));
        Assert.True(await SkillFileInstaller.Instance.InstallAsync(
            root, RelativeSkillDirectory, SkillName, [.. files, new("new.py", "print('hello')")], cancellationToken));

        foreach (var file in files)
        {
            var path = Path.Combine(skillDirectory.FullName, file.RelativePath);
            Assert.Equal(existingBytes, await File.ReadAllBytesAsync(path, cancellationToken));
            Assert.Equal(timestamp, File.GetLastWriteTimeUtc(path));
        }

        AssertTransactionDirectoriesRemoved(root);
    }

    [Fact]
    public async Task InstallAsync_PreservesEquivalentReplacementDecodedFiles()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var root = workspace.CreateDirectory("root");
        var skillDirectory = root.CreateSubdirectory(Path.Combine(RelativeSkillDirectory, SkillName));
        var path = Path.Combine(skillDirectory.FullName, "helper.py");
        byte[] existingBytes = [0x41, 0xff, 0x0d, 0x0a];
        var cancellationToken = TestContext.Current.CancellationToken;
        await File.WriteAllBytesAsync(path, existingBytes, cancellationToken);
        var timestamp = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, timestamp);
        SkillAssetFile[] files = [new("helper.py", "A\uFFFD\n")];

        Assert.False(await SkillFileInstaller.Instance.InstallAsync(root, RelativeSkillDirectory, SkillName, files, cancellationToken));

        Assert.Equal(existingBytes, await File.ReadAllBytesAsync(path, cancellationToken));
        Assert.Equal(timestamp, File.GetLastWriteTimeUtc(path));
        AssertTransactionDirectoriesRemoved(root);
    }

    [Fact]
    public async Task InstallAsync_WritesUtf8TextAndRetainsUserFiles()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var root = workspace.CreateDirectory("root");
        var skillDirectory = root.CreateSubdirectory(Path.Combine(RelativeSkillDirectory, SkillName));
        var userDirectory = skillDirectory.CreateSubdirectory("user");
        var otherSkill = root.CreateSubdirectory(Path.Combine(RelativeSkillDirectory, "other"));
        var cancellationToken = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(Path.Combine(skillDirectory.FullName, "SKILL.md"), "old", cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(userDirectory.FullName, "notes.txt"), "user notes", cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(otherSkill.FullName, "keep.txt"), "other skill", cancellationToken);
        SkillAssetFile[] files =
        [
            new("SKILL.md", "# Caf\u00e9\r\n"),
            new(Path.Combine("scripts", "helper.py"), "print('caf\u00e9')\r\n"),
            new("data.bin", "A\uFFFD\0")
        ];

        Assert.True(await SkillFileInstaller.Instance.InstallAsync(root, RelativeSkillDirectory, SkillName, files, cancellationToken));

        foreach (var file in files)
        {
            Assert.Equal(
                Encoding.UTF8.GetBytes(file.Content),
                await File.ReadAllBytesAsync(Path.Combine(skillDirectory.FullName, file.RelativePath), cancellationToken));
        }

        Assert.Equal("user notes", await File.ReadAllTextAsync(Path.Combine(userDirectory.FullName, "notes.txt"), cancellationToken));
        Assert.Equal("other skill", await File.ReadAllTextAsync(Path.Combine(otherSkill.FullName, "keep.txt"), cancellationToken));
        Assert.Equal(
            new[] { "SKILL.md", "data.bin", Path.Combine("scripts", "helper.py"), Path.Combine("user", "notes.txt") },
            Directory.GetFiles(skillDirectory.FullName, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(skillDirectory.FullName, path)).Order(StringComparer.Ordinal));
        Assert.False(await SkillFileInstaller.Instance.InstallAsync(root, RelativeSkillDirectory, SkillName, files, cancellationToken));
        AssertTransactionDirectoriesRemoved(root);
    }

    [Fact]
    public async Task InstallAsync_EmptyFilesDoesNotCreateDirectories()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var root = workspace.CreateDirectory("root");

        Assert.False(await SkillFileInstaller.Instance.InstallAsync(root, RelativeSkillDirectory, SkillName, [], TestContext.Current.CancellationToken));

        Assert.Empty(root.GetFileSystemInfos());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InstallAsync_BlockedDestinationPreservesOriginalFiles(bool blockedByDirectory)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var root = workspace.CreateDirectory("root");
        var skillDirectory = root.CreateSubdirectory(Path.Combine(RelativeSkillDirectory, SkillName));
        var originalPath = Path.Combine(skillDirectory.FullName, "SKILL.md");
        var blockedPath = Path.Combine(skillDirectory.FullName, "blocked");
        var cancellationToken = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(originalPath, "original", cancellationToken);
        if (blockedByDirectory)
        {
            Directory.CreateDirectory(blockedPath);
        }
        else
        {
            await File.WriteAllTextAsync(blockedPath, "blocking file", cancellationToken);
        }

        SkillAssetFile[] files =
        [
            new("SKILL.md", "replacement"),
            new(blockedByDirectory ? "blocked" : Path.Combine("blocked", "helper.py"), "new")
        ];

        await Assert.ThrowsAsync<IOException>(() =>
            SkillFileInstaller.Instance.InstallAsync(root, RelativeSkillDirectory, SkillName, files, cancellationToken));

        Assert.Equal("original", await File.ReadAllTextAsync(originalPath, cancellationToken));
        Assert.Equal(["SKILL.md", "blocked"], skillDirectory.GetFileSystemInfos().Select(entry => entry.Name).Order(StringComparer.Ordinal));
        if (!blockedByDirectory)
        {
            Assert.Equal("blocking file", await File.ReadAllTextAsync(blockedPath, cancellationToken));
        }

        AssertTransactionDirectoriesRemoved(root);
    }

    [Fact]
    public async Task InstallAsync_StagingWriteFailureDoesNotPublishEarlierFiles()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var root = workspace.CreateDirectory("root");
        var skillDirectory = root.CreateSubdirectory(Path.Combine(RelativeSkillDirectory, SkillName));
        var originalPath = Path.Combine(skillDirectory.FullName, "SKILL.md");
        var cancellationToken = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(originalPath, "original", cancellationToken);

        IEnumerable<SkillAssetFile> GetFiles()
        {
            yield return new("SKILL.md", "replacement");
            var stagingPath = Assert.Single(Directory.GetDirectories(
                Path.Combine(root.FullName, RelativeSkillDirectory), $".{SkillName}.staging.*"));
            Directory.CreateDirectory(Path.Combine(stagingPath, "helper.py"));
            yield return new("helper.py", "new");
        }

        var exception = await Record.ExceptionAsync(() =>
            SkillFileInstaller.Instance.InstallAsync(root, RelativeSkillDirectory, SkillName, GetFiles(), cancellationToken));

        Assert.True(exception is IOException or UnauthorizedAccessException, $"Expected an I/O failure, got {exception}");
        Assert.Equal("original", await File.ReadAllTextAsync(originalPath, cancellationToken));
        Assert.Equal(["SKILL.md"], skillDirectory.GetFileSystemInfos().Select(entry => entry.Name));
        AssertTransactionDirectoriesRemoved(root);
    }

    [Fact]
    public async Task InstallAsync_PreCanceledDoesNotCreateDirectories()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var root = workspace.CreateDirectory("root");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        SkillAssetFile[] files = [new("SKILL.md", "new")];

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            SkillFileInstaller.Instance.InstallAsync(root, RelativeSkillDirectory, SkillName, files, cancellation.Token));

        Assert.Empty(root.GetFileSystemInfos());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task InstallAsync_CancellationDuringPreparationDoesNotPublish(int preparedFiles)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var root = workspace.CreateDirectory("root");
        var skillDirectory = root.CreateSubdirectory(Path.Combine(RelativeSkillDirectory, SkillName));
        var originalPath = Path.Combine(skillDirectory.FullName, "SKILL.md");
        var cancellationToken = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(originalPath, "original", cancellationToken);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        SkillAssetFile[] files = [new("SKILL.md", "replacement"), new("helper.py", "new")];

        IEnumerable<SkillAssetFile> GetFiles()
        {
            for (var i = 0; i < files.Length; i++)
            {
                if (i == preparedFiles)
                {
                    cancellation.Cancel();
                }

                yield return files[i];
            }

            // This also covers cancellation after the last staging write, before publication.
            cancellation.Cancel();
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            SkillFileInstaller.Instance.InstallAsync(root, RelativeSkillDirectory, SkillName, GetFiles(), cancellation.Token));

        Assert.Equal("original", await File.ReadAllTextAsync(originalPath, cancellationToken));
        Assert.Equal(["SKILL.md"], skillDirectory.GetFileSystemInfos().Select(entry => entry.Name));
        AssertTransactionDirectoriesRemoved(root);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InstallAsync_CancellationDoesNotInterruptPublicationOrRollback(bool failPublication)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var root = workspace.CreateDirectory("root");
        var skillDirectory = root.CreateSubdirectory(Path.Combine(RelativeSkillDirectory, SkillName));
        var originalPath = Path.Combine(skillDirectory.FullName, "SKILL.md");
        var newPath = Path.Combine(skillDirectory.FullName, "helper.py");
        var cancellationToken = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(originalPath, "original", cancellationToken);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var installer = new SkillFileInstaller((source, destination) =>
        {
            cancellation.Cancel();
            if (failPublication && destination == newPath)
            {
                throw new IOException("Publication failed.");
            }

            File.Move(source, destination);
        });
        SkillAssetFile[] files = [new("SKILL.md", "replacement"), new("helper.py", "new")];

        if (failPublication)
        {
            await Assert.ThrowsAsync<IOException>(() =>
                installer.InstallAsync(root, RelativeSkillDirectory, SkillName, files, cancellation.Token));
            Assert.Equal("original", await File.ReadAllTextAsync(originalPath, cancellationToken));
            Assert.Equal(["SKILL.md"], skillDirectory.GetFiles().Select(file => file.Name));
        }
        else
        {
            Assert.True(await installer.InstallAsync(root, RelativeSkillDirectory, SkillName, files, cancellation.Token));
            Assert.Equal("replacement", await File.ReadAllTextAsync(originalPath, cancellationToken));
            Assert.Equal("new", await File.ReadAllTextAsync(newPath, cancellationToken));
        }

        Assert.True(cancellation.IsCancellationRequested);
        AssertTransactionDirectoriesRemoved(root);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InstallAsync_PublicationFailureRestoresReplacedAndNewFiles(bool failMovingOriginal)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var root = workspace.CreateDirectory("root");
        var skillDirectory = root.CreateSubdirectory(Path.Combine(RelativeSkillDirectory, SkillName));
        var originalPath = Path.Combine(skillDirectory.FullName, "SKILL.md");
        var lastPath = Path.Combine(skillDirectory.FullName, "last.py");
        var cancellationToken = TestContext.Current.CancellationToken;
        var originalBytes = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("original\r\n")).ToArray();
        await File.WriteAllBytesAsync(originalPath, originalBytes, cancellationToken);
        await File.WriteAllTextAsync(lastPath, "last original", cancellationToken);
        var timestamp = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(originalPath, timestamp);
        var publicationFailed = false;
        var installer = new SkillFileInstaller((source, destination) =>
        {
            if (!publicationFailed && (failMovingOriginal ? source == lastPath : destination == lastPath))
            {
                publicationFailed = true;
                throw new IOException("Publication failed.");
            }

            File.Move(source, destination);
        });
        SkillAssetFile[] files = [new("SKILL.md", "replacement"), new("new.py", "new"), new("last.py", "replacement")];

        var exception = await Assert.ThrowsAsync<IOException>(() =>
            installer.InstallAsync(root, RelativeSkillDirectory, SkillName, files, cancellationToken));

        Assert.Equal("Publication failed.", exception.Message);
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(originalPath, cancellationToken));
        Assert.Equal(timestamp, File.GetLastWriteTimeUtc(originalPath));
        Assert.Equal("last original", await File.ReadAllTextAsync(lastPath, cancellationToken));
        Assert.Equal(["SKILL.md", "last.py"], skillDirectory.GetFiles().Select(file => file.Name).Order(StringComparer.Ordinal));
        AssertTransactionDirectoriesRemoved(root);

        Assert.True(await SkillFileInstaller.Instance.InstallAsync(root, RelativeSkillDirectory, SkillName, files, cancellationToken));
        Assert.False(await SkillFileInstaller.Instance.InstallAsync(root, RelativeSkillDirectory, SkillName, files, cancellationToken));
        AssertTransactionDirectoriesRemoved(root);
    }

    [Fact]
    public async Task InstallAsync_MissingRenamePermissionRestoresEarlierFiles()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "This test validates Windows delete-sharing behavior.");
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var root = workspace.CreateDirectory("root");
        var skillDirectory = root.CreateSubdirectory(Path.Combine(RelativeSkillDirectory, SkillName));
        var originalPath = Path.Combine(skillDirectory.FullName, "SKILL.md");
        var lockedPath = Path.Combine(skillDirectory.FullName, "locked.py");
        var cancellationToken = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(originalPath, "original", cancellationToken);
        await File.WriteAllTextAsync(lockedPath, "locked", cancellationToken);
        SkillAssetFile[] files = [new("SKILL.md", "replacement"), new("new.py", "new"), new("locked.py", "replacement")];

        // In-place writes and staging reads are allowed, but transactional publication also
        // requires delete sharing so the original can be renamed into the rollback directory.
        using (File.Open(lockedPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            await Assert.ThrowsAsync<IOException>(() =>
                SkillFileInstaller.Instance.InstallAsync(root, RelativeSkillDirectory, SkillName, files, cancellationToken));
        }

        Assert.Equal("original", await File.ReadAllTextAsync(originalPath, cancellationToken));
        Assert.Equal("locked", await File.ReadAllTextAsync(lockedPath, cancellationToken));
        Assert.Equal(["SKILL.md", "locked.py"], skillDirectory.GetFiles().Select(file => file.Name).Order(StringComparer.Ordinal));
        AssertTransactionDirectoriesRemoved(root);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task InstallAsync_IncompleteRollbackPreservesRecoverableOriginals(bool failStagingCleanup, bool nestedOriginal)
    {
        Assert.SkipUnless(!failStagingCleanup || OperatingSystem.IsWindows(), "This case validates Windows delete-sharing behavior.");
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var root = workspace.CreateDirectory("root");
        var skillDirectory = root.CreateSubdirectory(Path.Combine(RelativeSkillDirectory, SkillName));
        var originalRelativePath = nestedOriginal ? Path.Combine("references", "original.txt") : "SKILL.md";
        var originalPath = Path.Combine(skillDirectory.FullName, originalRelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(originalPath)!);
        var lastPath = Path.Combine(skillDirectory.FullName, "last.py");
        var cancellationToken = TestContext.Current.CancellationToken;
        var originalBytes = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("original")).ToArray();
        await File.WriteAllBytesAsync(originalPath, originalBytes, cancellationToken);
        await File.WriteAllTextAsync(lastPath, "last original", cancellationToken);
        var publicationFailed = false;
        FileStream? stagingLock = null;
        var installer = new SkillFileInstaller((source, destination) =>
        {
            if (!publicationFailed && destination == lastPath)
            {
                publicationFailed = true;
                if (failStagingCleanup)
                {
                    stagingLock = File.Open(source, FileMode.Open, FileAccess.Read, FileShare.Read);
                }

                throw new IOException("Publication failed.");
            }

            if (publicationFailed && destination == originalPath)
            {
                throw new IOException("Rollback failed.");
            }

            File.Move(source, destination);
        });
        SkillAssetFile[] files = [new(originalRelativePath, "replacement"), new("new.py", "new"), new("last.py", "replacement")];

        try
        {
            var exception = await Assert.ThrowsAsync<IOException>(() =>
                installer.InstallAsync(root, RelativeSkillDirectory, SkillName, files, cancellationToken));

            var backupPath = Assert.Single(Directory.GetDirectories(
                Path.Combine(root.FullName, RelativeSkillDirectory), $".{SkillName}.rollback.*"));
            Assert.Contains($"Original files were preserved under '{backupPath}'.", exception.Message, StringComparison.Ordinal);
            var failures = Assert.IsType<AggregateException>(exception.InnerException);
            if (failStagingCleanup)
            {
                var installationFailure = Assert.IsType<IOException>(failures.InnerExceptions[0]);
                failures = Assert.IsType<AggregateException>(installationFailure.InnerException);
            }

            Assert.Collection(
                failures.InnerExceptions,
                failure => Assert.Equal("Publication failed.", failure.Message),
                failure => Assert.Equal("Rollback failed.", failure.Message));
            var backupFile = Assert.Single(Directory.GetFiles(backupPath, "*", SearchOption.AllDirectories));
            Assert.Equal(originalRelativePath, Path.GetRelativePath(backupPath, backupFile));
            Assert.Equal(originalBytes, await File.ReadAllBytesAsync(backupFile, cancellationToken));
            Assert.Equal("last original", await File.ReadAllTextAsync(lastPath, cancellationToken));
            Assert.Equal(["last.py"], skillDirectory.GetFiles().Select(file => file.Name));
            Assert.Equal(
                failStagingCleanup ? 1 : 0,
                Directory.GetDirectories(Path.Combine(root.FullName, RelativeSkillDirectory), $".{SkillName}.staging.*").Length);

            // The preserved original remains usable for explicit recovery after the error.
            File.Move(backupFile, originalPath);
            Assert.Equal(originalBytes, await File.ReadAllBytesAsync(originalPath, cancellationToken));
        }
        finally
        {
            stagingLock?.Dispose();
        }
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData(@"..\outside.txt")]
    [InlineData("/outside.txt")]
    [InlineData(@"C:\outside.txt")]
    [InlineData("nested/../../outside.txt")]
    [InlineData("SKILL.md:stream")]
    public async Task InstallAsync_RejectsEscapingFilePathsWithoutChangingOriginals(string relativePath)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var root = workspace.CreateDirectory("root");
        var skillDirectory = root.CreateSubdirectory(Path.Combine(RelativeSkillDirectory, SkillName));
        var originalPath = Path.Combine(skillDirectory.FullName, "SKILL.md");
        var outsidePath = Path.Combine(root.FullName, "outside.txt");
        var cancellationToken = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(originalPath, "original", cancellationToken);
        await File.WriteAllTextAsync(outsidePath, "outside", cancellationToken);
        SkillAssetFile[] files = [new("SKILL.md", "replacement"), new(relativePath, "invalid")];

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SkillFileInstaller.Instance.InstallAsync(root, RelativeSkillDirectory, SkillName, files, cancellationToken));

        Assert.Equal("original", await File.ReadAllTextAsync(originalPath, cancellationToken));
        Assert.Equal("outside", await File.ReadAllTextAsync(outsidePath, cancellationToken));
        Assert.Equal(["SKILL.md"], skillDirectory.GetFiles().Select(file => file.Name));
        AssertTransactionDirectoriesRemoved(root);
    }

    [Theory]
    [InlineData("../outside", "example")]
    [InlineData("skills", "../outside")]
    [InlineData("skills", "nested/example")]
    public async Task InstallAsync_RejectsEscapingSkillDirectories(string relativeDirectory, string skillName)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var root = workspace.CreateDirectory("root");
        SkillAssetFile[] files = [new("SKILL.md", "new")];

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SkillFileInstaller.Instance.InstallAsync(root, relativeDirectory, skillName, files, TestContext.Current.CancellationToken));

        Assert.Empty(root.GetFileSystemInfos());
    }

    [Fact]
    public async Task InstallAsync_RejectsDuplicateDestinationsBeforePublication()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var root = workspace.CreateDirectory("root");
        var skillDirectory = root.CreateSubdirectory(Path.Combine(RelativeSkillDirectory, SkillName));
        var originalPath = Path.Combine(skillDirectory.FullName, "SKILL.md");
        var cancellationToken = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(originalPath, "original", cancellationToken);
        SkillAssetFile[] files = [new("SKILL.md", "replacement"), new("SKILL.md", "duplicate")];

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SkillFileInstaller.Instance.InstallAsync(root, RelativeSkillDirectory, SkillName, files, cancellationToken));

        Assert.Equal("original", await File.ReadAllTextAsync(originalPath, cancellationToken));
        AssertTransactionDirectoriesRemoved(root);
    }

    [Fact]
    public async Task InstallAsync_RetainsExistingBundlePathNormalization()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var root = workspace.CreateDirectory("root");
        SkillAssetFile[] files = [new(@"scripts//nested\helper.py", "text")];

        Assert.True(await SkillFileInstaller.Instance.InstallAsync(root, RelativeSkillDirectory, SkillName, files, TestContext.Current.CancellationToken));

        Assert.Equal("text", await File.ReadAllTextAsync(
            Path.Combine(root.FullName, RelativeSkillDirectory, SkillName, "scripts", "nested", "helper.py"), TestContext.Current.CancellationToken));
        AssertTransactionDirectoriesRemoved(root);
    }

    [Theory]
    [InlineData("con")]
    [InlineData("trailing.")]
    [InlineData("trailing ")]
    public async Task InstallAsync_DoesNotAddPortableFilenameRestrictions(string relativePath)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "These names are supported on Unix filesystems.");
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var root = workspace.CreateDirectory("root");
        SkillAssetFile[] files = [new(relativePath, "text")];
        var cancellationToken = TestContext.Current.CancellationToken;

        Assert.True(await SkillFileInstaller.Instance.InstallAsync(root, RelativeSkillDirectory, SkillName, files, cancellationToken));

        Assert.Equal("text", await File.ReadAllTextAsync(Path.Combine(root.FullName, RelativeSkillDirectory, SkillName, relativePath), cancellationToken));
        AssertTransactionDirectoriesRemoved(root);
    }

    [Theory]
    [InlineData("root")]
    [InlineData("root-with-trailing-separator")]
    [InlineData("parent")]
    [InlineData("skill")]
    [InlineData("directory")]
    [InlineData("file")]
    [InlineData("dangling-file")]
    [InlineData("dangling-directory")]
    public async Task InstallAsync_RejectsSymbolicLinksWithoutChangingTheirTargets(string linkKind)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var root = workspace.CreateDirectory("root");
        var outside = workspace.CreateDirectory("outside");
        var outsidePath = Path.Combine(outside.FullName, "helper.py");
        var skillDirectory = root.CreateSubdirectory(Path.Combine(RelativeSkillDirectory, SkillName));
        var originalPath = Path.Combine(skillDirectory.FullName, "SKILL.md");
        var cancellationToken = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(outsidePath, "outside", cancellationToken);
        await File.WriteAllTextAsync(originalPath, "original", cancellationToken);
        var linkPath = linkKind switch
        {
            "root" or "root-with-trailing-separator" => Path.Combine(workspace.Path, "linked-root"),
            "parent" => Path.Combine(root.FullName, "linked-skills"),
            "skill" => Path.Combine(root.FullName, RelativeSkillDirectory, "linked-skill"),
            "directory" or "dangling-directory" => Path.Combine(skillDirectory.FullName, "linked"),
            _ => Path.Combine(skillDirectory.FullName, "helper.py"),
        };
        var directoryLink = linkKind is not ("file" or "dangling-file");
        var targetPath = linkKind is "dangling-file" or "dangling-directory"
            ? Path.Combine(outside.FullName, "missing")
            : directoryLink ? outside.FullName : outsidePath;

        try
        {
            TestSymlinkHelper.TryCreateSymlink(linkPath, targetPath, directoryLink);
            SkillAssetFile[] files =
            [
                new("SKILL.md", "replacement"),
                new(linkKind is "directory" or "dangling-directory" ? Path.Combine("linked", "helper.py") : "helper.py", "replacement")
            ];
            var installRoot = linkKind switch
            {
                "root" => new DirectoryInfo(linkPath),
                "root-with-trailing-separator" => new DirectoryInfo(linkPath + Path.DirectorySeparatorChar),
                _ => root,
            };

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                SkillFileInstaller.Instance.InstallAsync(
                    installRoot,
                    linkKind == "parent" ? "linked-skills" : RelativeSkillDirectory,
                    linkKind == "skill" ? "linked-skill" : SkillName,
                    files,
                    cancellationToken));

            Assert.Contains(linkPath, exception.Message, StringComparison.Ordinal);
            Assert.Equal("outside", await File.ReadAllTextAsync(outsidePath, cancellationToken));
            Assert.Equal("original", await File.ReadAllTextAsync(originalPath, cancellationToken));
            Assert.Equal(["helper.py"], outside.GetFileSystemInfos().Select(entry => entry.Name));
            AssertTransactionDirectoriesRemoved(root);
        }
        finally
        {
            // Remove links explicitly before TemporaryWorkspace's recursive cleanup.
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InstallAsync_RequiresDirectoryWritePermissionOnlyForChanges(bool restrictSkillDirectory)
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Unix directory modes are required for this test.");
            return;
        }

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var root = workspace.CreateDirectory("root");
        var skillDirectory = root.CreateSubdirectory(Path.Combine(RelativeSkillDirectory, SkillName));
        var originalPath = Path.Combine(skillDirectory.FullName, "SKILL.md");
        var restrictedPath = restrictSkillDirectory ? skillDirectory.FullName : skillDirectory.Parent!.FullName;
        var originalMode = File.GetUnixFileMode(restrictedPath);
        var cancellationToken = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(originalPath, "original", cancellationToken);

        try
        {
            File.SetUnixFileMode(restrictedPath, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            var permissionEnforced = false;
            var probePath = Path.Combine(restrictedPath, "write-probe");
            try
            {
                Directory.CreateDirectory(probePath);
                Directory.Delete(probePath);
            }
            catch (UnauthorizedAccessException)
            {
                permissionEnforced = true;
            }

            Assert.SkipUnless(permissionEnforced, "This environment bypasses Unix directory write permissions.");

            // The old in-place write was permitted. Only changed transactional installations
            // require new sibling directories and permission to rename the destination entry.
            await File.WriteAllTextAsync(originalPath, "original", cancellationToken);
            SkillAssetFile[] unchangedFiles = [new("SKILL.md", "original")];
            Assert.False(await SkillFileInstaller.Instance.InstallAsync(root, RelativeSkillDirectory, SkillName, unchangedFiles, cancellationToken));
            SkillAssetFile[] changedFiles = [new("SKILL.md", "replacement")];
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                SkillFileInstaller.Instance.InstallAsync(root, RelativeSkillDirectory, SkillName, changedFiles, cancellationToken));

            Assert.Equal("original", await File.ReadAllTextAsync(originalPath, cancellationToken));
            AssertTransactionDirectoriesRemoved(root);
        }
        finally
        {
            File.SetUnixFileMode(restrictedPath, originalMode);
        }
    }

    private static void AssertTransactionDirectoriesRemoved(DirectoryInfo root)
        => Assert.Empty(Directory.GetDirectories(Path.Combine(root.FullName, RelativeSkillDirectory), $".{SkillName}.*"));
}
