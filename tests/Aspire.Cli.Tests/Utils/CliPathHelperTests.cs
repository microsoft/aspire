// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Utils;
using Microsoft.Extensions.Time.Testing;

namespace Aspire.Cli.Tests.Utils;

public class CliPathHelperTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public void CreateGuestAppHostSocketPath_UsesRandomizedIdentifier()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var socketPath1 = CliPathHelper.CreateGuestAppHostSocketPath("apphost.sock");
        var socketPath2 = CliPathHelper.CreateGuestAppHostSocketPath("apphost.sock");

        Assert.NotEqual(socketPath1, socketPath2);

        if (OperatingSystem.IsWindows())
        {
            Assert.Matches("^apphost\\.sock\\.[a-f0-9]{12}$", socketPath1);
            Assert.Matches("^apphost\\.sock\\.[a-f0-9]{12}$", socketPath2);
        }
        else
        {
            var expectedDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".aspire", "cli", "runtime", "sockets");
            Assert.Equal(expectedDirectory, Path.GetDirectoryName(socketPath1));
            Assert.Equal(expectedDirectory, Path.GetDirectoryName(socketPath2));
            Assert.Matches("^apphost\\.sock\\.[a-f0-9]{12}$", Path.GetFileName(socketPath1));
            Assert.Matches("^apphost\\.sock\\.[a-f0-9]{12}$", Path.GetFileName(socketPath2));
        }
    }

    [Fact]
    public void CreateUnixDomainSocketPath_UsesRandomizedIdentifier()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var socketPath1 = CliPathHelper.CreateUnixDomainSocketPath("apphost.sock");
        var socketPath2 = CliPathHelper.CreateUnixDomainSocketPath("apphost.sock");

        Assert.NotEqual(socketPath1, socketPath2);

        var expectedDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".aspire", "cli", "runtime", "sockets");
        Assert.Equal(expectedDirectory, Path.GetDirectoryName(socketPath1));
        Assert.Equal(expectedDirectory, Path.GetDirectoryName(socketPath2));
        Assert.Matches("^apphost\\.sock\\.[a-f0-9]{12}$", Path.GetFileName(socketPath1));
        Assert.Matches("^apphost\\.sock\\.[a-f0-9]{12}$", Path.GetFileName(socketPath2));
    }

    [Fact]
    public void CleanupStaleCliSockets_DeletesFilesOlderThanThreshold()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "aspire-cli-sockets-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var staleFile = Path.Combine(tempDir, "cli.sock.stale");
            File.WriteAllText(staleFile, string.Empty);
            var freshFile = Path.Combine(tempDir, "cli.sock.fresh");
            File.WriteAllText(freshFile, string.Empty);

            var fakeTime = new FakeTimeProvider(DateTimeOffset.UtcNow);
            File.SetLastWriteTimeUtc(staleFile, fakeTime.GetUtcNow().UtcDateTime - TimeSpan.FromHours(48));
            File.SetLastWriteTimeUtc(freshFile, fakeTime.GetUtcNow().UtcDateTime - TimeSpan.FromMinutes(5));

            var deleted = CliPathHelper.CleanupStaleCliSockets(tempDir, TimeSpan.FromHours(24), fakeTime);

            Assert.Equal(1, deleted);
            Assert.False(File.Exists(staleFile));
            Assert.True(File.Exists(freshFile));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void CleanupStaleCliSockets_OnlyMatchesCliSockPrefix()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "aspire-cli-sockets-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var matching = Path.Combine(tempDir, "cli.sock.abc123");
            File.WriteAllText(matching, string.Empty);
            var unrelated = Path.Combine(tempDir, "apphost.sock.xyz");
            File.WriteAllText(unrelated, string.Empty);

            var fakeTime = new FakeTimeProvider(DateTimeOffset.UtcNow);
            File.SetLastWriteTimeUtc(matching, fakeTime.GetUtcNow().UtcDateTime - TimeSpan.FromHours(48));
            File.SetLastWriteTimeUtc(unrelated, fakeTime.GetUtcNow().UtcDateTime - TimeSpan.FromHours(48));

            var deleted = CliPathHelper.CleanupStaleCliSockets(tempDir, TimeSpan.FromHours(24), fakeTime);

            Assert.Equal(1, deleted);
            Assert.False(File.Exists(matching));
            Assert.True(File.Exists(unrelated));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void CleanupStaleCliSockets_MissingDirectoryIsNoOp()
    {
        var missingDir = Path.Combine(Path.GetTempPath(), "aspire-cli-sockets-missing-" + Guid.NewGuid().ToString("N"));

        var deleted = CliPathHelper.CleanupStaleCliSockets(missingDir, TimeSpan.FromHours(24));

        Assert.Equal(0, deleted);
    }

    [Fact]
    public void CleanupStaleCliSockets_EmptyDirectoryReturnsZero()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "aspire-cli-sockets-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var deleted = CliPathHelper.CleanupStaleCliSockets(tempDir, TimeSpan.FromHours(24));

            Assert.Equal(0, deleted);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}
