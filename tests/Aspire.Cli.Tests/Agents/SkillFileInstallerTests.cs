// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using Aspire.Cli.Agents;
using Aspire.Cli.Tests.TestServices;
using Aspire.Hosting.Utils;
using Microsoft.DotNet.RemoteExecutor;

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
        AssertDestinationLeaseReleased(Path.Combine(root.FullName, RelativeSkillDirectory, SkillName));
    }

    [Fact]
    public async Task InstallAsync_WaitsForAnotherProcessBeforeEnumeratingOrComparingFiles()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var root = workspace.CreateDirectory("root");
        var skillPath = Path.Combine(root.FullName, RelativeSkillDirectory, SkillName);
        SkillAssetFile[] files = [new("SKILL.md", "requested revision"), new("helper.py", "requested revision")];
        var cancellationToken = TestContext.Current.CancellationToken;
        var leasePath = SkillFileInstaller.GetDestinationLeasePath(skillPath);
        Assert.True(await SkillFileInstaller.Instance.InstallAsync(root, RelativeSkillDirectory, SkillName, files, cancellationToken));
        Assert.Equal(leasePath, SkillFileInstaller.GetDestinationLeasePath(skillPath));
        var readyPath = Path.Combine(workspace.Path, "ready");
        var releasePath = Path.Combine(workspace.Path, "release");

        using var process = RemoteExecutor.Invoke(static (skillPath, readyPath, releasePath) =>
        {
            using var lease = HoldDestinationLease(skillPath);
            File.WriteAllText(readyPath, string.Empty);
            WaitForSignal(releasePath, CancellationToken.None);
            File.WriteAllText(Path.Combine(skillPath, "SKILL.md"), "competing revision");
            File.WriteAllText(Path.Combine(skillPath, "helper.py"), "competing revision");
            File.WriteAllText(Path.Combine(skillPath, "user.txt"), "user file");
        }, skillPath, readyPath, releasePath);

        using var waitingCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var enumerations = 0;
        IEnumerable<SkillAssetFile> GetFiles()
        {
            Interlocked.Increment(ref enumerations);
            foreach (var file in files)
            {
                yield return file;
            }
        }

        Task<bool>? installation = null;
        try
        {
            WaitForSignal(readyPath, cancellationToken);
            waitingCancellation.CancelAfter(TimeSpan.FromSeconds(30));

            // Both files initially match. The other process changes them only after this
            // invocation starts, so pre-lease comparisons would incorrectly skip the update.
            installation = SkillFileInstaller.Instance.InstallAsync(
                root, RelativeSkillDirectory, SkillName, GetFiles(), waitingCancellation.Token);
            Assert.False(installation.IsCompleted);
            Assert.Equal(0, Volatile.Read(ref enumerations));
            File.WriteAllText(releasePath, string.Empty);
            Assert.True(await installation.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken));
            Assert.Equal(1, Volatile.Read(ref enumerations));

            foreach (var file in files)
            {
                Assert.Equal(file.Content, await File.ReadAllTextAsync(Path.Combine(skillPath, file.RelativePath), cancellationToken));
            }

            Assert.Equal("user file", await File.ReadAllTextAsync(Path.Combine(skillPath, "user.txt"), cancellationToken));
            Assert.Equal(["SKILL.md", "helper.py", "user.txt"], Directory.GetFiles(skillPath).Select(Path.GetFileName).Order(StringComparer.Ordinal));

            var timestamp = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            foreach (var file in files)
            {
                File.SetLastWriteTimeUtc(Path.Combine(skillPath, file.RelativePath), timestamp);
            }

            for (var attempt = 0; attempt < 3; attempt++)
            {
                Assert.False(await SkillFileInstaller.Instance.InstallAsync(root, RelativeSkillDirectory, SkillName, files, cancellationToken));
                foreach (var file in files)
                {
                    Assert.Equal(timestamp, File.GetLastWriteTimeUtc(Path.Combine(skillPath, file.RelativePath)));
                }
            }

            AssertDestinationLeaseReleased(skillPath);
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
    [InlineData(false)]
    [InlineData(true)]
    public async Task InstallAsync_CancellationWhileWaitingDoesNotReadOrMutateDestination(bool skillExists)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var root = workspace.CreateDirectory("root");
        var skillPath = Path.Combine(root.FullName, RelativeSkillDirectory, SkillName);
        var originalPath = Path.Combine(skillPath, "SKILL.md");
        var cancellationToken = TestContext.Current.CancellationToken;
        if (skillExists)
        {
            Directory.CreateDirectory(skillPath);
            await File.WriteAllTextAsync(originalPath, "original", cancellationToken);
        }

        SkillAssetFile[] files = [new("SKILL.md", "replacement"), new("helper.py", "new file")];
        var enumerations = 0;
        IEnumerable<SkillAssetFile> GetFiles()
        {
            Interlocked.Increment(ref enumerations);
            foreach (var file in files)
            {
                yield return file;
            }
        }

        using (HoldDestinationLease(skillPath))
        // A pre-lease comparison must fail immediately, not depend on async read timing.
        using (var original = skillExists ? File.Open(originalPath, FileMode.Open, FileAccess.Read, FileShare.None) : null)
        {
            using var waitingCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var installation = SkillFileInstaller.Instance.InstallAsync(
                root, RelativeSkillDirectory, SkillName, GetFiles(), waitingCancellation.Token);
            try
            {
                Assert.False(installation.IsCompleted);
                Assert.Equal(0, Volatile.Read(ref enumerations));
                Assert.Equal(skillExists ? [RelativeSkillDirectory] : Array.Empty<string>(), root.GetFileSystemInfos().Select(entry => entry.Name));
                await waitingCancellation.CancelAsync();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => installation);
                Assert.Equal(0, Volatile.Read(ref enumerations));
            }
            finally
            {
                await waitingCancellation.CancelAsync();
                await ((Task)installation).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
            }
        }

        if (skillExists)
        {
            Assert.Equal("original", await File.ReadAllTextAsync(originalPath, cancellationToken));
            Assert.Equal(["SKILL.md"], Directory.GetFiles(skillPath).Select(Path.GetFileName));
            AssertTransactionDirectoriesRemoved(root);
        }
        else
        {
            Assert.Empty(root.GetFileSystemInfos());
        }

        AssertDestinationLeaseReleased(skillPath);
        Assert.True(await SkillFileInstaller.Instance.InstallAsync(root, RelativeSkillDirectory, SkillName, files, cancellationToken));
    }

    [Theory]
    [InlineData("root")]
    [InlineData("parent")]
    [InlineData("skill")]
    public async Task InstallAsync_DoesNotBlockUnrelatedSkillDestinations(string differentComponent)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var root = workspace.CreateDirectory("root");
        var skillPath = Path.Combine(root.FullName, RelativeSkillDirectory, SkillName);
        using var lease = HoldDestinationLease(skillPath);
        var cancellationToken = TestContext.Current.CancellationToken;
        using var waitingCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        SkillAssetFile[] files = [new("SKILL.md", "new file")];
        var waitingInstallation = SkillFileInstaller.Instance.InstallAsync(root, RelativeSkillDirectory, SkillName, files, waitingCancellation.Token);
        Task<bool>? otherInstallation = null;
        try
        {
            Assert.False(waitingInstallation.IsCompleted);
            var otherRoot = differentComponent == "root" ? workspace.CreateDirectory("other-root") : root;
            var otherParent = differentComponent == "parent" ? "other-skills" : RelativeSkillDirectory;
            var otherName = differentComponent == "skill" ? "other" : SkillName;

            otherInstallation = SkillFileInstaller.Instance.InstallAsync(otherRoot, otherParent, otherName, files, waitingCancellation.Token);
            Assert.True(await otherInstallation.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken));
            Assert.Equal("new file", await File.ReadAllTextAsync(Path.Combine(otherRoot.FullName, otherParent, otherName, "SKILL.md"), cancellationToken));
            Assert.False(waitingInstallation.IsCompleted);
            Assert.False(Directory.Exists(skillPath));
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
        var skillPath = Path.Combine(root.FullName, "missing", RelativeSkillDirectory, SkillName);
        var leasePath = SkillFileInstaller.GetDestinationLeasePath(skillPath);

        Assert.Equal(leasePath, SkillFileInstaller.GetDestinationLeasePath(skillPath + Path.DirectorySeparatorChar));
        Assert.Equal(leasePath, SkillFileInstaller.GetDestinationLeasePath(Path.Combine(root.FullName, "unused", "..", "missing", RelativeSkillDirectory, SkillName)));
        Assert.Equal(leasePath, SkillFileInstaller.GetDestinationLeasePath(Path.Combine(root.FullName, "MISSING", "SKILLS", SkillName.ToUpperInvariant())));
        Assert.Equal(
            SkillFileInstaller.GetDestinationLeasePath(Path.Combine(root.FullName, RelativeSkillDirectory, "caf\u00e9")),
            SkillFileInstaller.GetDestinationLeasePath(Path.Combine(root.FullName, RelativeSkillDirectory, "cafe\u0301")));
        Assert.Empty(root.GetFileSystemInfos());

        Directory.CreateDirectory(skillPath);
        Assert.Equal(leasePath, SkillFileInstaller.GetDestinationLeasePath(skillPath));
    }

    [Fact]
    public void GetDestinationLeasePath_UsesUserProfileIndependentlyOfAspireHome()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var skillPath = Path.Combine(workspace.Path, RelativeSkillDirectory, SkillName);
        var leasePath = SkillFileInstaller.GetDestinationLeasePath(skillPath);
        var homePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.Equal(
            Path.Combine(PathNormalizer.ResolveSymlinks(homePath), ".aspire", "cli", "agent-asset-locks"),
            Path.GetDirectoryName(leasePath));
        var alternateHome = workspace.CreateDirectory("alternate-home");
        var options = new RemoteInvokeOptions();
        options.StartInfo.Environment["ASPIRE_HOME"] = alternateHome.FullName;

        using (RemoteExecutor.Invoke(static (skillPath, expectedLeasePath) =>
        {
            Assert.Equal(expectedLeasePath, SkillFileInstaller.GetDestinationLeasePath(skillPath));
        }, skillPath, leasePath, options))
        {
        }

        Assert.Empty(alternateHome.GetFileSystemInfos());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InstallAsync_AncestorAliasesShareLeaseAndCannotRedirectWaitingInstallation(bool rootExists)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var parent = workspace.CreateDirectory("parent");
        var root = new DirectoryInfo(Path.Combine(parent.FullName, "root"));
        if (rootExists)
        {
            root.Create();
        }

        var otherParent = workspace.CreateDirectory("other-parent");
        var alias = Path.Combine(workspace.Path, "alias");
        var skillPath = Path.Combine(root.FullName, RelativeSkillDirectory, SkillName);
        using var waitingCancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        Task<bool>? installation = null;
        try
        {
            TestSymlinkHelper.TryCreateSymlink(alias, parent.FullName);
            var aliasedRoot = new DirectoryInfo(Path.Combine(alias, "root"));
            Assert.Equal(
                SkillFileInstaller.GetDestinationLeasePath(skillPath),
                SkillFileInstaller.GetDestinationLeasePath(Path.Combine(aliasedRoot.FullName, RelativeSkillDirectory, SkillName)));

            SkillAssetFile[] files = [new("SKILL.md", "new file")];
            using (HoldDestinationLease(skillPath))
            {
                installation = SkillFileInstaller.Instance.InstallAsync(aliasedRoot, RelativeSkillDirectory, SkillName, files, waitingCancellation.Token);
                Assert.False(installation.IsCompleted);
                Assert.False(Directory.Exists(skillPath));

                Directory.Delete(alias);
                TestSymlinkHelper.TryCreateSymlink(alias, otherParent.FullName);
            }

            Assert.True(await installation.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
            Assert.Equal("new file", await File.ReadAllTextAsync(Path.Combine(skillPath, "SKILL.md"), TestContext.Current.CancellationToken));
            Assert.Empty(otherParent.GetFileSystemInfos());
            AssertDestinationLeaseReleased(skillPath);
            AssertTransactionDirectoriesRemoved(root);
        }
        finally
        {
            await waitingCancellation.CancelAsync();
            if (installation is not null)
            {
                await ((Task)installation).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
            }

            if (new DirectoryInfo(alias).LinkTarget is not null)
            {
                Directory.Delete(alias);
            }
        }
    }

    [Theory]
    [InlineData("root")]
    [InlineData("parent")]
    [InlineData("skill")]
    public async Task InstallAsync_RejectsDestinationLinksIntroducedWhileWaiting(string linkKind)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var root = workspace.CreateDirectory("root");
        var outside = workspace.CreateDirectory("outside");
        var skillPath = Path.Combine(root.FullName, RelativeSkillDirectory, SkillName);
        var linkPath = linkKind switch
        {
            "root" => root.FullName,
            "parent" => Path.Combine(root.FullName, RelativeSkillDirectory),
            _ => skillPath
        };
        using var waitingCancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        Task<bool>? installation = null;
        try
        {
            using (HoldDestinationLease(skillPath))
            {
                SkillAssetFile[] files = [new("SKILL.md", "new file")];
                installation = SkillFileInstaller.Instance.InstallAsync(root, RelativeSkillDirectory, SkillName, files, waitingCancellation.Token);
                Assert.False(installation.IsCompleted);
                if (linkKind == "root")
                {
                    root.Delete();
                }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(linkPath)!);
                }

                TestSymlinkHelper.TryCreateSymlink(linkPath, outside.FullName);
            }

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                installation.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
            Assert.Empty(outside.GetFileSystemInfos());
        }
        finally
        {
            await waitingCancellation.CancelAsync();
            if (installation is not null)
            {
                await ((Task)installation).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
            }

            if (new DirectoryInfo(linkPath).LinkTarget is not null)
            {
                Directory.Delete(linkPath);
            }
        }

        AssertDestinationLeaseReleased(skillPath);
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative-profile")]
    [InlineData("missing")]
    [InlineData("file")]
    public void GetDestinationLeasePath_RejectsMissingOrInvalidUserProfiles(string profileKind)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var profilePath = profileKind is "missing" or "file" ? Path.Combine(workspace.Path, profileKind) : profileKind;
        if (profileKind == "file")
        {
            File.WriteAllText(profilePath, "not a directory");
        }

        Assert.Throws<IOException>(() =>
            SkillFileInstaller.GetDestinationLeasePath(Path.Combine(workspace.Path, RelativeSkillDirectory, SkillName), profilePath));
        Assert.Equal(profileKind == "file" ? ["file"] : Array.Empty<string>(), workspace.WorkspaceRoot.GetFileSystemInfos().Select(entry => entry.Name));
    }

    [Theory]
    [InlineData(".aspire")]
    [InlineData(".aspire/cli")]
    [InlineData(".aspire/cli/agent-asset-locks")]
    public void GetDestinationLeasePath_RejectsInvalidLeaseDirectories(string relativePath)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var profile = workspace.CreateDirectory("profile");
        var blockedPath = Path.Combine(profile.FullName, Path.Combine(relativePath.Split('/')));
        Directory.CreateDirectory(Path.GetDirectoryName(blockedPath)!);
        File.WriteAllText(blockedPath, "not a directory");

        Assert.Throws<IOException>(() =>
            SkillFileInstaller.GetDestinationLeasePath(Path.Combine(workspace.Path, RelativeSkillDirectory, SkillName), profile.FullName));

        Assert.Equal("not a directory", File.ReadAllText(blockedPath));
        Assert.Equal([blockedPath], Directory.GetFiles(profile.FullName, "*", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData(".aspire", false)]
    [InlineData(".aspire/cli", false)]
    [InlineData(".aspire/cli/agent-asset-locks", false)]
    [InlineData(".aspire", true)]
    [InlineData(".aspire/cli", true)]
    [InlineData(".aspire/cli/agent-asset-locks", true)]
    public void GetDestinationLeasePath_RejectsLinkedLeaseDirectories(string relativePath, bool dangling)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var profile = workspace.CreateDirectory("profile");
        var outside = workspace.CreateDirectory("outside");
        var outsidePath = Path.Combine(outside.FullName, "keep.txt");
        File.WriteAllText(outsidePath, "outside");
        var linkPath = Path.Combine(profile.FullName, Path.Combine(relativePath.Split('/')));
        Directory.CreateDirectory(Path.GetDirectoryName(linkPath)!);
        try
        {
            TestSymlinkHelper.TryCreateSymlink(linkPath, dangling ? Path.Combine(outside.FullName, "missing") : outside.FullName);

            Assert.Throws<InvalidOperationException>(() =>
                SkillFileInstaller.GetDestinationLeasePath(Path.Combine(workspace.Path, RelativeSkillDirectory, SkillName), profile.FullName));

            Assert.Equal("outside", File.ReadAllText(outsidePath));
            Assert.Equal(["keep.txt"], outside.GetFileSystemInfos().Select(entry => entry.Name));
        }
        finally
        {
            if (new DirectoryInfo(linkPath).LinkTarget is not null)
            {
                Directory.Delete(linkPath);
            }
        }
    }

    [Theory]
    [InlineData("lease")]
    [InlineData("cli")]
    [InlineData("profile")]
    [InlineData("volume")]
    public void GetDestinationLeasePath_RejectsDestinationsContainingLeaseDirectory(string destinationKind)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var profile = workspace.CreateDirectory("profile");
        var leaseDirectory = Path.GetDirectoryName(SkillFileInstaller.GetDestinationLeasePath(
            Path.Combine(workspace.Path, RelativeSkillDirectory, SkillName), profile.FullName))!;
        var destinationPath = destinationKind switch
        {
            "lease" => leaseDirectory,
            "cli" => Path.GetDirectoryName(leaseDirectory)!,
            "profile" => profile.FullName,
            _ => Path.GetPathRoot(profile.FullName)!
        };

        Assert.Throws<InvalidOperationException>(() => SkillFileInstaller.GetDestinationLeasePath(destinationPath, profile.FullName));
        Assert.Equal(leaseDirectory, Path.GetDirectoryName(SkillFileInstaller.GetDestinationLeasePath(leaseDirectory + "-other", profile.FullName)));
        Assert.Empty(profile.GetFileSystemInfos());
    }

    [Fact]
    public async Task InstallAsync_InvalidLeaseFileReportsIoErrorWithoutMutatingDestination()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var root = workspace.CreateDirectory("root");
        var leasePath = SkillFileInstaller.GetDestinationLeasePath(Path.Combine(root.FullName, RelativeSkillDirectory, SkillName));
        Directory.CreateDirectory(leasePath);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            SkillAssetFile[] files = [new("SKILL.md", "new file")];
            await Assert.ThrowsAsync<IOException>(() =>
                SkillFileInstaller.Instance.InstallAsync(root, RelativeSkillDirectory, SkillName, files, cancellation.Token));
            Assert.Empty(root.GetFileSystemInfos());
        }
        finally
        {
            Directory.Delete(leasePath);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InstallAsync_RejectsSymbolicLinkLeaseWithoutChangingTarget(bool dangling)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var root = workspace.CreateDirectory("root");
        var outsidePath = Path.Combine(workspace.Path, "outside");
        var cancellationToken = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(outsidePath, "outside", cancellationToken);
        var leasePath = SkillFileInstaller.GetDestinationLeasePath(Path.Combine(root.FullName, RelativeSkillDirectory, SkillName));
        Directory.CreateDirectory(Path.GetDirectoryName(leasePath)!);
        try
        {
            TestSymlinkHelper.TryCreateSymlink(leasePath, dangling ? outsidePath + "-missing" : outsidePath, isDirectory: false);
            SkillAssetFile[] files = [new("SKILL.md", "new file")];
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                SkillFileInstaller.Instance.InstallAsync(root, RelativeSkillDirectory, SkillName, files, cancellationToken));

            Assert.Empty(root.GetFileSystemInfos());
            Assert.Equal("outside", await File.ReadAllTextAsync(outsidePath, cancellationToken));
            Assert.False(File.Exists(outsidePath + "-missing"));
        }
        finally
        {
            if (new FileInfo(leasePath).LinkTarget is not null)
            {
                File.Delete(leasePath);
            }
        }
    }

    [Fact]
    public async Task InstallAsync_InaccessibleLeaseReportsAccessErrorWithoutMutatingDestination()
    {
        Assert.SkipWhen(!OperatingSystem.IsWindows() && Environment.IsPrivilegedProcess, "Requires enforced Unix file permissions.");
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var root = workspace.CreateDirectory("root");
        var skillPath = Path.Combine(root.FullName, RelativeSkillDirectory, SkillName);
        var leasePath = SkillFileInstaller.GetDestinationLeasePath(skillPath);
        using (HoldDestinationLease(skillPath))
        {
        }

        var originalAttributes = File.GetAttributes(leasePath);
        var originalMode = OperatingSystem.IsWindows() ? default : File.GetUnixFileMode(leasePath);
        try
        {
            if (OperatingSystem.IsWindows())
            {
                File.SetAttributes(leasePath, originalAttributes | FileAttributes.ReadOnly);
            }
            else
            {
                File.SetUnixFileMode(leasePath, UnixFileMode.UserRead);
            }

            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            cancellation.CancelAfter(TimeSpan.FromSeconds(30));
            SkillAssetFile[] files = [new("SKILL.md", "new file")];
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                SkillFileInstaller.Instance.InstallAsync(root, RelativeSkillDirectory, SkillName, files, cancellation.Token));

            Assert.Empty(root.GetFileSystemInfos());
        }
        finally
        {
            if (OperatingSystem.IsWindows())
            {
                File.SetAttributes(leasePath, originalAttributes);
            }
            else
            {
                File.SetUnixFileMode(leasePath, originalMode);
            }
        }

        AssertDestinationLeaseReleased(skillPath);
    }

    [Fact]
    public async Task InstallAsync_RejectsDisabledUnixFileLockingWithoutMutatingDestination()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The .NET file-locking switch only affects Unix.");
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var root = workspace.CreateDirectory("root");
        var options = new RemoteInvokeOptions();
        options.StartInfo.Environment["DOTNET_SYSTEM_IO_DISABLEFILELOCKING"] = "1";

        using (RemoteExecutor.Invoke(static async rootPath =>
        {
            SkillAssetFile[] files = [new("SKILL.md", "new file")];
            var exception = await Assert.ThrowsAsync<IOException>(() => SkillFileInstaller.Instance
                .InstallAsync(new DirectoryInfo(rootPath), RelativeSkillDirectory, SkillName, files, CancellationToken.None));
            var leasePath = SkillFileInstaller.GetDestinationLeasePath(Path.Combine(rootPath, RelativeSkillDirectory, SkillName));
            Assert.Equal($"Exclusive file locking is required for skill installation lease '{leasePath}'.", exception.Message);
            Assert.Empty(Directory.GetFileSystemEntries(rootPath));
        }, root.FullName, options))
        {
        }

        AssertDestinationLeaseReleased(Path.Combine(root.FullName, RelativeSkillDirectory, SkillName));
        Assert.True(await SkillFileInstaller.Instance.InstallAsync(
            root, RelativeSkillDirectory, SkillName, [new("SKILL.md", "new file")], TestContext.Current.CancellationToken));
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
        AssertDestinationLeaseReleased(skillDirectory.FullName);
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
        AssertDestinationLeaseReleased(skillDirectory.FullName);
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
            AssertDestinationLeaseHeld(skillDirectory.FullName);
            cancellation.Cancel();
            if (failPublication && destination == PathNormalizer.ResolveSymlinks(newPath))
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
        AssertDestinationLeaseReleased(skillDirectory.FullName);
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
            AssertDestinationLeaseHeld(skillDirectory.FullName);
            var resolvedLastPath = PathNormalizer.ResolveSymlinks(lastPath);
            if (!publicationFailed && (failMovingOriginal ? source == resolvedLastPath : destination == resolvedLastPath))
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
        AssertDestinationLeaseReleased(skillDirectory.FullName);

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
        AssertDestinationLeaseReleased(skillDirectory.FullName);
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
            AssertDestinationLeaseHeld(skillDirectory.FullName);
            if (!publicationFailed && destination == PathNormalizer.ResolveSymlinks(lastPath))
            {
                publicationFailed = true;
                if (failStagingCleanup)
                {
                    stagingLock = File.Open(source, FileMode.Open, FileAccess.Read, FileShare.Read);
                }

                throw new IOException("Publication failed.");
            }

            if (publicationFailed && destination == PathNormalizer.ResolveSymlinks(originalPath))
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

            var backupPath = PathNormalizer.ResolveSymlinks(Assert.Single(Directory.GetDirectories(
                Path.Combine(root.FullName, RelativeSkillDirectory), $".{SkillName}.rollback.*")));
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
            AssertDestinationLeaseReleased(skillDirectory.FullName);
        }
        finally
        {
            stagingLock?.Dispose();
        }
    }

    [Fact]
    public async Task InstallAsync_CleanupFailureReleasesDestinationLease()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "This test validates Windows delete-sharing behavior.");
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var root = workspace.CreateDirectory("root");
        var skillPath = Path.Combine(root.FullName, RelativeSkillDirectory, SkillName);
        FileStream? cleanupLock = null;
        string? stagingPath = null;
        var installer = new SkillFileInstaller((source, destination) =>
        {
            AssertDestinationLeaseHeld(skillPath);
            File.Move(source, destination);
            stagingPath = Path.GetDirectoryName(source)!;
            var blockedPath = Path.Combine(stagingPath, "cleanup-blocker");
            File.WriteAllText(blockedPath, "prevent staging cleanup");
            cleanupLock = File.Open(blockedPath, FileMode.Open, FileAccess.Read, FileShare.None);
        });
        try
        {
            var exception = await Assert.ThrowsAsync<IOException>(() => installer.InstallAsync(
                root, RelativeSkillDirectory, SkillName, [new("SKILL.md", "new file")], TestContext.Current.CancellationToken));

            Assert.Contains("Skill transaction cleanup failed.", exception.Message, StringComparison.Ordinal);
            Assert.Equal("new file", await File.ReadAllTextAsync(Path.Combine(skillPath, "SKILL.md"), TestContext.Current.CancellationToken));
            AssertDestinationLeaseReleased(skillPath);
        }
        finally
        {
            cleanupLock?.Dispose();
            if (stagingPath is not null && Directory.Exists(stagingPath))
            {
                Directory.Delete(stagingPath, recursive: true);
            }
        }

        AssertTransactionDirectoriesRemoved(root);
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

    private static FileStream HoldDestinationLease(string skillPath)
    {
        var leasePath = SkillFileInstaller.GetDestinationLeasePath(skillPath);
        Directory.CreateDirectory(Path.GetDirectoryName(leasePath)!);
        return new FileStream(leasePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, bufferSize: 1);
    }

    private static void AssertDestinationLeaseHeld(string skillPath)
        => Assert.Throws<IOException>(() =>
        {
            using var unexpectedLease = HoldDestinationLease(skillPath);
        });

    private static void AssertDestinationLeaseReleased(string skillPath)
    {
        Assert.True(File.Exists(SkillFileInstaller.GetDestinationLeasePath(skillPath)));
        using var lease = HoldDestinationLease(skillPath);
        Assert.Equal(0, lease.Length);
    }

    private static void WaitForSignal(string path, CancellationToken cancellationToken)
    {
        Assert.True(SpinWait.SpinUntil(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return File.Exists(path);
        }, TimeSpan.FromSeconds(30)), $"Timed out waiting for signal '{path}'.");
    }
}
