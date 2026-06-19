// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Tests.Utils;

namespace Aspire.Cli.Tests;

public class CliExecutionContextTests(ITestOutputHelper outputHelper)
{
    private static CliExecutionContext CreateContext(string channel = "local")
    {
        var workingDir = new DirectoryInfo(AppContext.BaseDirectory);
        var hivesDir = new DirectoryInfo(Path.Combine(AppContext.BaseDirectory, "hives"));
        var cacheDir = new DirectoryInfo(Path.Combine(AppContext.BaseDirectory, "cache"));
        var sdksDir = new DirectoryInfo(Path.Combine(AppContext.BaseDirectory, "sdks"));
        var logsDir = new DirectoryInfo(Path.Combine(AppContext.BaseDirectory, "logs"));
        return new CliExecutionContext(workingDir, hivesDir, cacheDir, sdksDir, logsDir, "test.log", identityChannel: channel);
    }

    private static CliExecutionContext CreateContextWithHives(DirectoryInfo hivesDir)
    {
        var workingDir = hivesDir.Parent ?? new DirectoryInfo(AppContext.BaseDirectory);
        var cacheDir = new DirectoryInfo(Path.Combine(workingDir.FullName, "cache"));
        var sdksDir = new DirectoryInfo(Path.Combine(workingDir.FullName, "sdks"));
        var logsDir = new DirectoryInfo(Path.Combine(workingDir.FullName, "logs"));
        return new CliExecutionContext(workingDir, hivesDir, cacheDir, sdksDir, logsDir, "test.log", identityChannel: "local");
    }

    [Fact]
    public void Helper_DefaultsIdentityChannelToLocal_WhenNotSpecified()
    {
        // identityChannel is a required constructor parameter (a CLI build always has an
        // identity), so the "local" convenience default now lives in the test factory —
        // mirroring production, where Program.BuildCliExecutionContext always supplies it.
        var ctx = TestExecutionContextHelper.CreateExecutionContext(new DirectoryInfo(AppContext.BaseDirectory));

        Assert.Equal("local", ctx.IdentityChannel);
    }

    [Theory]
    [InlineData("stable")]
    [InlineData("staging")]
    [InlineData("daily")]
    [InlineData("local")]
    [InlineData("pr-1")]
    [InlineData("pr-16798")]
    public void Channel_Getter_ReturnsExactValuePassedToConstructor(string channel)
    {
        // CliExecutionContext is now a thin holder — the resolved hive label is baked
        // into the AspireCliChannel assembly metadata at build time (CI emits `pr-<N>`
        // directly for PR builds), so the context returns whatever string the caller
        // hands it. Validation of the channel SHAPE lives in IdentityChannelReader.
        var ctx = CreateContext(channel: channel);

        Assert.Equal(channel, ctx.IdentityChannel);
    }

    [Fact]
    public void GetHiveCount_ReturnsZero_WhenHivesDirectoryDoesNotExist()
    {
        // The hive count gates the channel picker in `aspire update`, the channel
        // sub-menu in `aspire add`, and the explicit-channel inclusion in
        // IntegrationPackageSearchService. A clean machine has no
        // ~/.aspire/hives directory at all; the count must be 0 in that case.
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var nonexistentHives = new DirectoryInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "no-hives-here"));
        Assert.False(nonexistentHives.Exists);

        var ctx = CreateContextWithHives(nonexistentHives);

        Assert.Equal(0, ctx.GetHiveCount());
    }

    [Fact]
    public void GetHiveCount_ReturnsZero_WhenHivesDirectoryEmpty()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var hivesDir = workspace.CreateDirectory("hives");

        var ctx = CreateContextWithHives(hivesDir);

        Assert.Equal(0, ctx.GetHiveCount());
    }

    [Fact]
    public void GetHiveCount_ReturnsSubdirectoryCount_WhenPopulated()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var hivesDir = workspace.CreateDirectory("hives");
        hivesDir.CreateSubdirectory("pr-1");
        hivesDir.CreateSubdirectory("pr-16820");
        hivesDir.CreateSubdirectory("local");

        var ctx = CreateContextWithHives(hivesDir);

        Assert.Equal(3, ctx.GetHiveCount());
    }

    [Fact]
    public void GetHiveCount_IgnoresFilesAtHivesRoot()
    {
        // Stray files (e.g. a README dropped in by a script or a leftover .DS_Store)
        // must not be counted as hives. Hives are directories produced by the
        // dogfood/PR install scripts; mistakenly counting a file would falsely
        // trigger the channel picker on machines that otherwise wouldn't see it.
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var hivesDir = workspace.CreateDirectory("hives");
        hivesDir.CreateSubdirectory("pr-1");
        File.WriteAllText(Path.Combine(hivesDir.FullName, "README.md"), "stray");
        File.WriteAllText(Path.Combine(hivesDir.FullName, ".DS_Store"), string.Empty);

        var ctx = CreateContextWithHives(hivesDir);

        Assert.Equal(1, ctx.GetHiveCount());
    }
}
