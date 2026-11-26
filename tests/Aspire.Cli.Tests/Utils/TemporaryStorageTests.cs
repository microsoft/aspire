// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Security.AccessControl;
using System.Security.Principal;
using Aspire.Shared;
using Microsoft.Extensions.Configuration;

namespace Aspire.Cli.Tests.Utils;

public class TemporaryStorageTests(ITestOutputHelper output)
{
    [Fact]
    public void AllocationsUseConfiguredHomeAndPrivateModes()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(output);
        var home = Path.Combine(workspace.Path, ".aspire");
        using var storage = CreateStorage(home);
        var directory = storage.CreateTempSubdirectory("fixture-");
        var file = storage.GetTempFileName();
        var session = Path.TrimEndingDirectorySeparator(storage.GetTempPath());

        Assert.Equal(Path.Combine(home, "tmp"), Path.GetDirectoryName(session));
        Assert.Matches("^[a-z0-9]{8}\\.[a-z0-9]{3}$", Path.GetFileName(session));
        Assert.Equal(session, directory.Parent!.FullName);
        Assert.StartsWith("fixture-", directory.Name);
        Assert.Equal(session, Path.GetDirectoryName(file));
        Assert.Equal(0, new FileInfo(file).Length);
        Assert.Null(FileLock.TryAcquire(session + ".lock"));
        if (!OperatingSystem.IsWindows())
        {
            var directoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            Assert.Equal(directoryMode, File.GetUnixFileMode(Path.Combine(home, "tmp")));
            Assert.Equal(directoryMode, File.GetUnixFileMode(session));
            Assert.Equal(directoryMode, File.GetUnixFileMode(directory.FullName));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(session + ".lock"));
        }

        storage.Dispose();
        using var lease = FileLock.TryAcquire(session + ".lock");
        Assert.NotNull(lease);
        Assert.True(File.Exists(file));
        Assert.True(Directory.Exists(directory.FullName));
        Assert.Throws<ObjectDisposedException>(storage.GetTempPath);
    }

    [Fact]
    public void StorageInstancesHaveIndependentSessions()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(output);
        var home = Path.Combine(workspace.Path, ".aspire");
        using var first = CreateStorage(home);
        using var second = CreateStorage(home);
        Assert.NotEqual(first.GetTempPath(), second.GetTempPath());
        Assert.Equal(first.GetTempPath(), first.GetTempPath());
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("false", false)]
    [InlineData("0", false)]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("1", true)]
    public void PreserveMarkerRequiresTruthyValue(string? value, bool expected)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(output);
        using var storage = CreateStorage(Path.Combine(workspace.Path, ".aspire"), value);
        Assert.Equal(expected, File.Exists(Path.Combine(storage.GetTempPath(), ".preserve")));
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("nested/child")]
    public void DirectoryPrefixesCannotEscapeSession(string prefix)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(output);
        using var storage = CreateStorage(Path.Combine(workspace.Path, ".aspire"));
        Assert.Throws<ArgumentException>(() => storage.CreateTempSubdirectory(prefix));
    }

    [Fact]
    public void MissingHomeFailsWithoutFallback()
    {
        Assert.Throws<IOException>(() => PrivateTemporaryStorage.GetRoot(".aspire"));
        using var workspace = TemporaryWorkspace.CreateForCli(output);
        var home = Path.Combine(workspace.Path, "missing", ".aspire");
        using var storage = CreateStorage(home);
        Assert.Throws<IOException>(storage.GetTempPath);
        Assert.False(Directory.Exists(home));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [PlatformSpecific(TestPlatforms.Windows)]
    public void ExistingWindowsHomePermissionsArePreservedButTmpIsValidated(bool configureTmp)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = TemporaryWorkspace.CreateForCli(output);
        var home = Directory.CreateDirectory(Path.Combine(workspace.Path, ".aspire"));
        var directory = configureTmp ? Directory.CreateDirectory(Path.Combine(home.FullName, "tmp")) : home;
        var security = directory.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.ReadAndExecute,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        directory.SetAccessControl(security);
        var before = directory.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.All);
        using var storage = CreateStorage(home.FullName);

        if (configureTmp)
        {
            Assert.Throws<IOException>(storage.GetTempPath);
        }
        else
        {
            Assert.Equal(Path.Combine(home.FullName, "tmp"), Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(storage.GetTempPath())));
        }

        Assert.Equal(before, directory.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.All));
    }

    [Fact]
    [PlatformSpecific(TestPlatforms.Windows)]
    public void AdministratorOwnedWindowsTmpIsAccepted()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
        {
            Assert.Skip("Setting the Administrators owner requires an elevated process.");
        }

        using var workspace = TemporaryWorkspace.CreateForCli(output);
        var home = Path.Combine(workspace.Path, ".aspire");
        var root = new DirectoryInfo(PrivateTemporaryStorage.GetRoot(home));
        var security = root.GetAccessControl();
        security.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        root.SetAccessControl(security);
        using var storage = CreateStorage(home);

        Assert.Equal(root.FullName, Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(storage.GetTempPath())));
    }

    [Fact]
    public void WritableRootsAndLinksAreRejectedWithoutRepair()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = TemporaryWorkspace.CreateForCli(output);
        var home = Path.Combine(workspace.Path, ".aspire");
        var root = PrivateTemporaryStorage.GetRoot(home);
        var writableMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupWrite;
        File.SetUnixFileMode(root, writableMode);
        Assert.Throws<IOException>(() => PrivateTemporaryStorage.GetRoot(home));
        Assert.Equal(writableMode, File.GetUnixFileMode(root));
        Directory.Delete(root);
        Directory.CreateSymbolicLink(root, workspace.Path);
        try
        {
            Assert.Throws<IOException>(() => PrivateTemporaryStorage.GetRoot(home));
        }
        finally
        {
            Directory.Delete(root);
        }
    }

    private static PrivateTemporaryStorage CreateStorage(string home, string? preserve = null)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [AspireHomeDirectory.EnvironmentVariable] = home,
            ["ASPIRE_PRESERVE_TEMP_FILES"] = preserve
        }).Build();

        return new PrivateTemporaryStorage(configuration);
    }
}
