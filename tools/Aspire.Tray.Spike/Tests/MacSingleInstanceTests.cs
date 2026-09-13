// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.Versioning;

namespace Aspire.Tray.Spike.Tests;

public class MacSingleInstanceTests
{
    public static bool SupportsMac => OperatingSystem.IsMacOS();

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
