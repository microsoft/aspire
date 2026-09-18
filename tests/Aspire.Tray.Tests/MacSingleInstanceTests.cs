// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.Versioning;

namespace Aspire.Tray.Tests;

public class MacSingleInstanceTests
{
    public static bool SupportsMac => OperatingSystem.IsMacOS();

    [Fact(Skip = "The state directory is macOS-specific.", SkipUnless = nameof(SupportsMac))]
    [SupportedOSPlatform("macos")]
    public void ControlEndpointUsesTheUserProfileRuntimeDirectory()
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".aspire", "tray", "runtime");
        Assert.Equal(directory, SingleInstance.DirectoryPath);
        Assert.Equal(Path.Combine(directory, "control-v1.sock"), SingleInstance.ActivationPipeName);
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library", "Application Support", "Aspire", "Tray"), SingleInstance.LegacyStateDirectoryPath);
    }

    [Theory(Skip = "The lock uses Darwin flock.", SkipUnless = nameof(SupportsMac))]
    [InlineData(false)]
    [InlineData(true)]
    [SupportedOSPlatform("macos")]
    public void StateDirectoryIsOwnerOnlyBeforeAcquiringLock(bool directoryExists)
    {
        var parent = Directory.CreateTempSubdirectory("aspire-tray-permissions-");
        try
        {
            var directory = Path.Combine(parent.FullName, "state");
            if (directoryExists)
            {
                Directory.CreateDirectory(directory);
                File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                    | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }

            using var lease = SingleInstance.TryAcquire(directory);
            Assert.NotNull(lease);
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(directory));
        }
        finally
        {
            parent.Delete(recursive: true);
        }
    }
}
