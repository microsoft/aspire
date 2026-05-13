// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Utils;

namespace Aspire.Cli.Tests.Utils;

public class FileLockTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task AcquireAsync_Succeeds_InWritableDirectory()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var lockPath = Path.Combine(workspace.WorkspaceRoot.FullName, ".test-lock");

        using var fileLock = await FileLock.AcquireAsync(lockPath);

        Assert.NotNull(fileLock);
    }

    [Fact]
    public async Task AcquireAsync_ThrowsUnauthorizedAccessException_WhenDirectoryNotWritable()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("This test is not applicable on Windows due to differences in file system permissions.");
            return;
        }

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var lockedDir = Path.Combine(workspace.WorkspaceRoot.FullName, "readonly");
        Directory.CreateDirectory(lockedDir);

        var originalMode = File.GetUnixFileMode(lockedDir);
        File.SetUnixFileMode(lockedDir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(
                () => FileLock.AcquireAsync(Path.Combine(lockedDir, ".test-lock"), timeout: TimeSpan.FromSeconds(1)));
        }
        finally
        {
            File.SetUnixFileMode(lockedDir, originalMode);
        }
    }
}
