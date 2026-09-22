// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Sockets;
using System.Security.AccessControl;
using System.Security.Principal;
using Aspire.Shared;

namespace Aspire.Hosting.Tests.Utils;

[Trait("Partition", "4")]
public sealed class SocketPermissionHelperTests
{
    private const UnixFileMode DirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode SocketMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Bind_ProtectsDirectoryAndSocket_AndAllowsOwnerConnection(bool existingDirectory)
    {
        var root = Directory.CreateTempSubdirectory();
        try
        {
            var directory = Path.Combine(root.FullName, "s");
            if (existingDirectory)
            {
                MakePermissiveDirectory(directory);
            }

            var socketPath = Path.Combine(directory, "s.sock");
            using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            SocketPermissionHelper.Bind(listener, socketPath);

            AssertDirectoryPermissions(directory);
            if (OperatingSystem.IsWindows())
            {
                using var identity = WindowsIdentity.GetCurrent();
                var security = new FileInfo(socketPath).GetAccessControl();
                var rule = Assert.Single(security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>());
                Assert.Equal(identity.User, rule.IdentityReference);
                Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
                Assert.Equal(FileSystemRights.FullControl, rule.FileSystemRights);
            }
            else
            {
                Assert.Equal(SocketMode, File.GetUnixFileMode(socketPath));
            }

            listener.Listen();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await client.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), timeout.Token);
            using var accepted = await listener.AcceptAsync(timeout.Token);
            Assert.True(accepted.Connected);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void Bind_RepairsDirectoryBeforeAttemptingToBind()
    {
        var root = Directory.CreateTempSubdirectory();
        try
        {
            var directory = Path.Combine(root.FullName, "s");
            MakePermissiveDirectory(directory);
            var socketPath = Path.Combine(directory, "occupied");
            File.WriteAllText(socketPath, "existing file");

            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            Assert.Throws<SocketException>(() => SocketPermissionHelper.Bind(socket, socketPath));

            AssertDirectoryPermissions(directory);
            Assert.Null(socket.LocalEndPoint);
            Assert.Equal("existing file", File.ReadAllText(socketPath));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void Bind_DirectoryCreationFailure_DoesNotBind()
    {
        var root = Directory.CreateTempSubdirectory();
        try
        {
            var directory = Path.Combine(root.FullName, "file");
            File.WriteAllText(directory, "not a directory");

            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            Assert.ThrowsAny<IOException>(() => SocketPermissionHelper.Bind(socket, Path.Combine(directory, "s.sock")));

            Assert.Null(socket.LocalEndPoint);
            Assert.Equal("not a directory", File.ReadAllText(directory));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void CreateDirectory_RejectsSymbolicLinkWithoutChangingTarget()
    {
        if (OperatingSystem.IsWindows())
        {
            // Unprivileged symlink creation is not available on every Windows test agent.
            return;
        }

        var root = Directory.CreateTempSubdirectory();
        try
        {
            var target = Path.Combine(root.FullName, "target");
            MakePermissiveDirectory(target);
            var originalMode = File.GetUnixFileMode(target);
            var link = Path.Combine(root.FullName, "link");
            Directory.CreateSymbolicLink(link, target);

            Assert.Throws<IOException>(() => SocketPermissionHelper.CreateDirectory(link));

            Assert.Equal(originalMode, File.GetUnixFileMode(target));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void CreateDirectory_RejectsSharedRoots()
    {
        Assert.Throws<IOException>(() => SocketPermissionHelper.CreateDirectory(Path.GetTempPath()));
        Assert.Throws<IOException>(() => SocketPermissionHelper.CreateDirectory(Path.GetPathRoot(Path.GetTempPath())!));
    }

    [Fact]
    public void CreateDirectory_RejectsSharedStickyDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Directory.CreateTempSubdirectory();
        try
        {
            var originalMode = DirectoryMode | UnixFileMode.StickyBit | UnixFileMode.OtherRead |
                UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
            File.SetUnixFileMode(root.FullName, originalMode);

            Assert.Throws<IOException>(() => SocketPermissionHelper.CreateDirectory(root.FullName));

            Assert.Equal(originalMode, File.GetUnixFileMode(root.FullName));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    private static void MakePermissiveDirectory(string path)
    {
        var directory = Directory.CreateDirectory(path);
        if (OperatingSystem.IsWindows())
        {
            var security = directory.GetAccessControl();
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
            directory.SetAccessControl(security);
        }
        else
        {
            File.SetUnixFileMode(path, DirectoryMode |
                UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute);
        }
    }

    private static void AssertDirectoryPermissions(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            using var identity = WindowsIdentity.GetCurrent();
            var security = new DirectoryInfo(path).GetAccessControl();
            Assert.True(security.AreAccessRulesProtected);
            Assert.Equal(identity.User, security.GetOwner(typeof(SecurityIdentifier)));
            var rule = Assert.Single(security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>());
            Assert.Equal(identity.User, rule.IdentityReference);
            Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
            Assert.Equal(FileSystemRights.FullControl, rule.FileSystemRights);
            Assert.Equal(InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, rule.InheritanceFlags);
        }
        else
        {
            Assert.Equal(DirectoryMode, File.GetUnixFileMode(path));
        }
    }
}
