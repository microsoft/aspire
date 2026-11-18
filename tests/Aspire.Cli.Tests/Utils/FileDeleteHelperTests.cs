// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Time.Testing;

namespace Aspire.Cli.Tests.Utils;

public class FileDeleteHelperTests(ITestOutputHelper outputHelper)
{
    [Fact]
    [SkipOnPlatform(TestPlatforms.Linux | TestPlatforms.OSX | TestPlatforms.FreeBSD, "Windows file sharing semantics are required.")]
    public async Task DeleteDirectoryWithRetryAsync_DeletesDirectoryOnceTransientLockIsReleased()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var directory = workspace.CreateDirectory("server").FullName;
        var filePath = Path.Combine(directory, "Aspire.Hosting.dll");
        await File.WriteAllTextAsync(filePath, "assembly");
        var timeProvider = new FakeTimeProvider();

        using var lockingStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.None);
        var deleteTask = FileDeleteHelper.DeleteDirectoryWithRetryAsync(directory, timeProvider, TestContext.Current.CancellationToken);

        Assert.False(deleteTask.IsCompleted);
        Assert.True(Directory.Exists(directory));

        lockingStream.Dispose();
        timeProvider.Advance(TimeSpan.FromSeconds(1));
        await deleteTask.DefaultTimeout();

        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    [SkipOnPlatform(TestPlatforms.Linux | TestPlatforms.OSX | TestPlatforms.FreeBSD, "Windows file sharing semantics are required.")]
    public async Task DeleteDirectoryWithRetryAsync_ThrowsWhenLockOutlastsRetryBudget()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var directory = workspace.CreateDirectory("server").FullName;
        var filePath = Path.Combine(directory, "Aspire.Hosting.dll");
        await File.WriteAllTextAsync(filePath, "assembly");
        var timeProvider = new FakeTimeProvider();

        using (new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var deleteTask = FileDeleteHelper.DeleteDirectoryWithRetryAsync(directory, timeProvider, TestContext.Current.CancellationToken);

            timeProvider.Advance(TimeSpan.FromSeconds(2));
            var exception = await Assert.ThrowsAnyAsync<Exception>(() => deleteTask).DefaultTimeout();
            Assert.True(exception is IOException or UnauthorizedAccessException, exception.ToString());
        }

        Assert.Equal("assembly", await File.ReadAllTextAsync(filePath));
    }
}
