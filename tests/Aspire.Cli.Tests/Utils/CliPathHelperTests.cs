// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Utils;

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

        var expectedDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".aspire", "cli", "bch");
        Assert.Equal(expectedDirectory, Path.GetDirectoryName(socketPath1));
        Assert.Equal(expectedDirectory, Path.GetDirectoryName(socketPath2));
        Assert.Matches("^h[A-Za-z0-9_-]{8}$", Path.GetFileName(socketPath1));
        Assert.Matches("^h[A-Za-z0-9_-]{8}$", Path.GetFileName(socketPath2));
    }

    [Fact]
    public void CreateUnixDomainSocketPath_UsesRandomizedIdentifier()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var socketPath1 = CliPathHelper.CreateUnixDomainSocketPath("apphost.sock");
        var socketPath2 = CliPathHelper.CreateUnixDomainSocketPath("apphost.sock");

        Assert.NotEqual(socketPath1, socketPath2);

        var expectedDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".aspire", "cli", "bch");
        Assert.Equal(expectedDirectory, Path.GetDirectoryName(socketPath1));
        Assert.Equal(expectedDirectory, Path.GetDirectoryName(socketPath2));
        Assert.Matches("^h[A-Za-z0-9_-]{8}$", Path.GetFileName(socketPath1));
        Assert.Matches("^h[A-Za-z0-9_-]{8}$", Path.GetFileName(socketPath2));
    }
}
