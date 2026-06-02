// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Acquisition;
using Aspire.Cli.Tests.Utils;

namespace Aspire.Cli.Tests.Acquisition;

public class HiveEnumeratorTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public void HasHive_ReturnsTrueForExistingValidChannel()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var context = workspace.CreateExecutionContext();
        Directory.CreateDirectory(Path.Combine(context.HivesDirectory.FullName, "stable"));
        Directory.CreateDirectory(Path.Combine(context.HivesDirectory.FullName, "pr-17400"));
        var enumerator = new HiveEnumerator(context);

        Assert.True(enumerator.HasHive("stable"));
        Assert.True(enumerator.HasHive("pr-17400"));
        Assert.False(enumerator.HasHive("daily"));
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../escape")]
    [InlineData("pr-17400/../../etc")]
    [InlineData("/etc/passwd")]
    [InlineData("C:\\Windows")]
    [InlineData("pr-")]
    [InlineData("pr-abc")]
    [InlineData("Pr-17400")]
    [InlineData("preview")]
    [InlineData("")]
    [InlineData("\0invalid")]
    public void HasHive_RejectsChannelsOutsideIdentitySchema(string channel)
    {
        // HiveEnumerator must never feed a path-traversing, absolute, or
        // otherwise malformed channel string into Path.Combine — channels can
        // reach this surface via peer `--info --self` JSON, which is not
        // shape-validated at the JSON layer. Anything outside the build-time
        // identity-channel allow-list must be treated as "no hive".
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var context = workspace.CreateExecutionContext();
        var enumerator = new HiveEnumerator(context);

        Assert.False(enumerator.HasHive(channel));
        Assert.Null(enumerator.GetHivePath(channel));
    }

    [Fact]
    public void GetHivePath_ReturnsPathInsideHivesRootForValidChannel()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var context = workspace.CreateExecutionContext();
        var enumerator = new HiveEnumerator(context);

        var path = enumerator.GetHivePath("pr-17400");

        Assert.NotNull(path);
        Assert.StartsWith(context.HivesDirectory.FullName, path, StringComparison.Ordinal);
        Assert.EndsWith("pr-17400", path, StringComparison.Ordinal);
    }

    [Fact]
    public void GetHives_ReturnsEmptyWhenHivesRootMissing()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var context = workspace.CreateExecutionContext();
        var enumerator = new HiveEnumerator(context);

        Assert.False(context.HivesDirectory.Exists);
        Assert.Empty(enumerator.GetHives());
    }

    [Fact]
    public void GetHives_EnumeratesUnvalidatedDirectoryNamesUnderHivesRoot()
    {
        // GetHives() walks the on-disk directory itself and only reports what's
        // there — no channel-schema filtering. That's intentional: an orphan
        // hive directory left behind by an older install script or a future
        // channel name should still surface to the user via `aspire --info`.
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var context = workspace.CreateExecutionContext();
        Directory.CreateDirectory(Path.Combine(context.HivesDirectory.FullName, "pr-17400"));
        Directory.CreateDirectory(Path.Combine(context.HivesDirectory.FullName, "future-channel"));
        var enumerator = new HiveEnumerator(context);

        var names = enumerator.GetHives().Select(h => h.Name).ToArray();

        Assert.Contains("pr-17400", names);
        Assert.Contains("future-channel", names);
    }
}
