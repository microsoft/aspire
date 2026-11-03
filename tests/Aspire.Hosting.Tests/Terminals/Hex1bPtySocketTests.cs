// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Aspire.Hosting.Utils;
using Aspire.Shared;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.DotNet.RemoteExecutor;

#pragma warning disable ASPIRETERMINAL001 // Test consumer of the experimental AppHost terminal API.

namespace Aspire.Hosting.Tests.Terminals;

[Trait("Partition", "2")]
public class Hex1bPtySocketTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CreateSocketPath_OnUnix_LeavesEnvironmentAndFilesystemUnchanged(bool hasOverride)
    {
        Assert.SkipUnless(!OperatingSystem.IsWindows(), "Unix PTYs do not use the Windows proxy socket.");

        RemoteExecutor.Invoke(static hasOverrideValue =>
        {
            var root = CreateSocketTestDirectory();
            try
            {
                var directory = Path.Combine(root.FullName, "custom");
                var value = bool.Parse(hasOverrideValue) ? directory : null;
                Environment.SetEnvironmentVariable(Hex1bPtySocketHelper.SocketDirectoryEnvironmentVariable, value);

                Assert.Null(Hex1bPtySocketHelper.CreateSocketPath());

                Assert.Equal(value, Environment.GetEnvironmentVariable(Hex1bPtySocketHelper.SocketDirectoryEnvironmentVariable));
                Assert.False(Directory.Exists(directory));
            }
            finally
            {
                root.Delete(recursive: true);
            }
        }, hasOverride.ToString()).Dispose();
    }

    [Theory]
    [InlineData(TerminalPlacement.Dock, false)]
    [InlineData(TerminalPlacement.Dock, true)]
    [InlineData(TerminalPlacement.Dialog, false)]
    [InlineData(TerminalPlacement.Dialog, true)]
    [SupportedOSPlatform("windows")]
    public void CreateTerminal_SecuresDirectoryBeforeDeferredStartup(TerminalPlacement placement, bool existingDirectory)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows PTY socket permissions.");

        RemoteExecutor.Invoke(static async (placementValue, existingDirectoryValue) =>
        {
            var root = CreateSocketTestDirectory();
            try
            {
                var directory = Path.Combine(root.FullName, "custom");
                if (bool.Parse(existingDirectoryValue))
                {
                    SocketPermissionHelper.CreateDirectory(directory, repairExisting: false);
                }

                // Normalize the socket path, not the caller's environment override.
                var originalOverride = Path.Combine(directory, "..", "custom");
                Environment.SetEnvironmentVariable(Hex1bPtySocketHelper.SocketDirectoryEnvironmentVariable, originalOverride);
                await using (var service = TestTerminalService.Create())
                {
                    await using var terminal = service.CreateTerminal(new TerminalLaunchOptions
                    {
                        Title = "Deferred terminal",
                        Executable = "not-started",
                        Placement = Enum.Parse<TerminalPlacement>(placementValue),
                        EnvironmentVariables = { [Hex1bPtySocketHelper.SocketDirectoryEnvironmentVariable] = Path.Combine(root.FullName, "child") }
                    });

                    Assert.Equal(originalOverride, Environment.GetEnvironmentVariable(Hex1bPtySocketHelper.SocketDirectoryEnvironmentVariable));
                    var security = new DirectoryInfo(directory).GetAccessControl();
                    Assert.True(security.AreAccessRulesProtected);
                    using var identity = WindowsIdentity.GetCurrent();
                    Assert.Equal(identity.User, security.GetOwner(typeof(SecurityIdentifier)));
                    var rule = Assert.Single(security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>());
                    Assert.Equal(identity.User, rule.IdentityReference);
                    Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
                    Assert.Equal(FileSystemRights.FullControl, rule.FileSystemRights);
                    Assert.Equal(InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, rule.InheritanceFlags);
                    Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(directory, "proxy")));
                }

                Assert.Equal(originalOverride, Environment.GetEnvironmentVariable(Hex1bPtySocketHelper.SocketDirectoryEnvironmentVariable));
                Assert.True(Directory.Exists(directory));
            }
            finally
            {
                root.Delete(recursive: true);
            }
        }, placement.ToString(), existingDirectory.ToString()).Dispose();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CreateTerminal_InvalidDirectory_FailsBeforeRegistration(bool sharedRoot)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows PTY socket permissions.");

        RemoteExecutor.Invoke(static async sharedRootValue =>
        {
            var root = CreateSocketTestDirectory();
            try
            {
                Directory.CreateDirectory(Path.Combine(root.FullName, ".aspire"));
                var directory = Path.Combine(root.FullName, ".aspire", "pty");
                File.WriteAllText(directory, "not a directory");
                var value = bool.Parse(sharedRootValue) ? Path.GetTempPath() : directory;
                Environment.SetEnvironmentVariable(Hex1bPtySocketHelper.SocketDirectoryEnvironmentVariable, value);
                await using var service = TestTerminalService.Create();

                Assert.ThrowsAny<IOException>(() => service.CreateTerminal(new TerminalLaunchOptions
                {
                    Title = "Rejected terminal",
                    Executable = "not-started"
                }));

                Assert.Empty(service.ListAll());
                Assert.Equal(value, Environment.GetEnvironmentVariable(Hex1bPtySocketHelper.SocketDirectoryEnvironmentVariable));
                Assert.Equal("not a directory", File.ReadAllText(directory));
            }
            finally
            {
                root.Delete(recursive: true);
            }
        }, sharedRoot.ToString()).Dispose();
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void CreateTerminal_PermissiveOverride_FailsWithoutChangingPermissions()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows PTY socket permissions.");

        RemoteExecutor.Invoke(static async () =>
        {
            var root = CreateSocketTestDirectory();
            try
            {
                var directory = Directory.CreateDirectory(Path.Combine(root.FullName, "custom"));
                var security = directory.GetAccessControl();
                security.AddAccessRule(new FileSystemAccessRule(
                    new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                    FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None,
                    AccessControlType.Allow));
                directory.SetAccessControl(security);
                var originalPermissions = directory.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.All);
                Environment.SetEnvironmentVariable(Hex1bPtySocketHelper.SocketDirectoryEnvironmentVariable, directory.FullName);
                await using var service = TestTerminalService.Create();

                Assert.Throws<IOException>(() => service.CreateTerminal(new TerminalLaunchOptions
                {
                    Title = "Rejected terminal",
                    Executable = "not-started"
                }));

                Assert.Empty(service.ListAll());
                Assert.Equal(originalPermissions, directory.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.All));
                Assert.Empty(directory.EnumerateFileSystemInfos());
            }
            finally
            {
                root.Delete(recursive: true);
            }
        }).Dispose();
    }

    [Fact]
    public void CreateSocketPath_Default_DoesNotSetEnvironment()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows PTY socket paths.");

        RemoteExecutor.Invoke(static () =>
        {
            Environment.SetEnvironmentVariable(Hex1bPtySocketHelper.SocketDirectoryEnvironmentVariable, null);
            var socketPath = Hex1bPtySocketHelper.CreateSocketPath();

            Assert.NotNull(socketPath);
            Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".aspire", "pty", "proxy"),
                Path.GetDirectoryName(socketPath));
            Assert.False(Path.Exists(socketPath));
            Assert.Null(Environment.GetEnvironmentVariable(Hex1bPtySocketHelper.SocketDirectoryEnvironmentVariable));
        }).Dispose();
    }

    [Fact]
    public void CreateTerminal_OverlongSocketPath_FailsBeforeCreatingDirectory()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows PTY socket paths.");

        RemoteExecutor.Invoke(static async () =>
        {
            var root = CreateSocketTestDirectory();
            try
            {
                var directory = Path.Combine(root.FullName, new string('a', 108));
                Environment.SetEnvironmentVariable(Hex1bPtySocketHelper.SocketDirectoryEnvironmentVariable, directory);
                await using var service = TestTerminalService.Create();

                Assert.Throws<ArgumentOutOfRangeException>(() => service.CreateTerminal(new TerminalLaunchOptions
                {
                    Title = "Rejected terminal",
                    Executable = "not-started"
                }));

                Assert.Empty(service.ListAll());
                Assert.False(Directory.Exists(directory));
                Assert.Equal(directory, Environment.GetEnvironmentVariable(Hex1bPtySocketHelper.SocketDirectoryEnvironmentVariable));
            }
            finally
            {
                root.Delete(recursive: true);
            }
        }).Dispose();
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void Start_ConcurrentDeferredTerminals_UsePrivateSockets()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Requires the Windows PTY proxy.");

        RemoteExecutor.Invoke(static async () =>
        {
            var root = CreateSocketTestDirectory();
            try
            {
                var directory = Path.Combine(root.FullName, ".aspire", "pty");
                _ = new UnixDomainSocketEndPoint(Path.Combine(directory, $"hex1bpty-{new string('0', 32)}.socket"));
                var overrideDirectory = SocketPermissionHelper.CreateDirectory(directory, repairExisting: false);
                var overrideSecurity = overrideDirectory.GetAccessControl();
                overrideSecurity.AddAccessRule(new FileSystemAccessRule(
                    new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                    FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None,
                    AccessControlType.Allow));
                overrideDirectory.SetAccessControl(overrideSecurity);
                var originalPermissions = overrideDirectory.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.All);
                Environment.SetEnvironmentVariable(Hex1bPtySocketHelper.SocketDirectoryEnvironmentVariable, directory);
                await using (var service = TestTerminalService.Create())
                {
                    await using var dock = service.CreateTerminal(new TerminalLaunchOptions
                    {
                        Title = "Dock",
                        Executable = "cmd.exe",
                        Arguments = ["/d", "/q", "/k", "echo dock-ready"],
                        EnvironmentVariables = { [Hex1bPtySocketHelper.SocketDirectoryEnvironmentVariable] = Path.Combine(root.FullName, "child") },
                        Placement = TerminalPlacement.Dock
                    });
                    await using var prompt = service.CreateTerminal(new TerminalLaunchOptions
                    {
                        Title = "Prompt",
                        Executable = "cmd.exe",
                        Arguments = ["/d", "/q", "/k", "echo prompt-ready"],
                        Placement = TerminalPlacement.Dialog
                    });

                    Assert.Equal(directory, Environment.GetEnvironmentVariable(Hex1bPtySocketHelper.SocketDirectoryEnvironmentVariable));
                    // Deferred startup must use the snapshotted paths, not reread the environment.
                    var laterDirectory = Path.Combine(root.FullName, "later");
                    Environment.SetEnvironmentVariable(Hex1bPtySocketHelper.SocketDirectoryEnvironmentVariable, laterDirectory);
                    Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(directory, "proxy")));
                    await Task.WhenAll(
                        dock.WaitForTextAsync("dock-ready"),
                        prompt.WaitForTextAsync("prompt-ready")).DefaultTimeout();

                    var sockets = Directory.GetFiles(Path.Combine(directory, "proxy"), "*.socket");
                    Assert.Equal(2, sockets.Length);
                    Assert.False(Directory.Exists(laterDirectory));
                    Assert.False(Directory.Exists(Path.Combine(root.FullName, "child")));
                    Assert.Equal(originalPermissions, overrideDirectory.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.All));
                    using var identity = WindowsIdentity.GetCurrent();
                    var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
                    foreach (var socket in sockets)
                    {
                        // The helper must never grant other ordinary users access.
                        var rules = new FileInfo(socket).GetAccessControl()
                            .GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();
                        Assert.Contains(rules, rule => rule.IdentityReference.Equals(identity.User));
                        Assert.All(rules, rule =>
                        {
                            Assert.True(rule.IdentityReference.Equals(identity.User) || rule.IdentityReference.Equals(system));
                            Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
                            Assert.Equal(FileSystemRights.FullControl, rule.FileSystemRights);
                        });
                    }
                }

                Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(directory, "proxy")));
                Assert.Equal(Path.Combine(root.FullName, "later"), Environment.GetEnvironmentVariable(Hex1bPtySocketHelper.SocketDirectoryEnvironmentVariable));
            }
            finally
            {
                root.Delete(recursive: true);
            }
        }).Dispose();
    }

    private static DirectoryInfo CreateSocketTestDirectory()
    {
        var directory = Directory.CreateTempSubdirectory();
        if (OperatingSystem.IsWindows())
        {
            // Leave room for "hex1bpty-{32 hex digits}.socket" within sockaddr_un.
            // MoveTo fails rather than reusing an existing directory.
            directory.MoveTo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), directory.Name));
        }

        return directory;
    }
}
