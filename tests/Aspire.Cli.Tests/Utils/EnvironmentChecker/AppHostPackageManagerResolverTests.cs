// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Utils.EnvironmentChecker;

namespace Aspire.Cli.Tests.Utils.EnvironmentChecks;

public class AppHostPackageManagerResolverTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public void Resolve_DefaultsToNpm_WhenNoMarkersPresent()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var appHostDir = workspace.CreateDirectory("apphost");

        var result = AppHostPackageManagerResolver.Resolve(appHostDir);

        Assert.Equal(AppHostPackageManager.Npm, result.PackageManager);
        Assert.Equal("default", result.Source);
        Assert.Null(result.DeclaredVersion);
    }

    [Theory]
    [InlineData("npm@10.2.0", nameof(AppHostPackageManager.Npm), "10.2.0")]
    [InlineData("pnpm@8.6.0", nameof(AppHostPackageManager.Pnpm), "8.6.0")]
    [InlineData("bun@1.1.30", nameof(AppHostPackageManager.Bun), "1.1.30")]
    [InlineData("yarn@4.1.0", nameof(AppHostPackageManager.YarnBerry), "4.1.0")]
    [InlineData("yarn@1.22.22", nameof(AppHostPackageManager.YarnClassic), "1.22.22")]
    public void Resolve_ReadsPackageManagerField(string fieldValue, string expectedName, string expectedVersion)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var appHostDir = workspace.CreateDirectory("apphost");
        WritePackageJson(appHostDir, $$"""{ "packageManager": "{{fieldValue}}" }""");

        var result = AppHostPackageManagerResolver.Resolve(appHostDir);

        Assert.Equal(Enum.Parse<AppHostPackageManager>(expectedName), result.PackageManager);
        Assert.Equal(expectedVersion, result.DeclaredVersion);
        Assert.Contains("packageManager", result.Source);
    }

    [Fact]
    public void Resolve_StripsIntegrityHashFromPackageManagerField()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var appHostDir = workspace.CreateDirectory("apphost");
        WritePackageJson(appHostDir, """{ "packageManager": "pnpm@8.6.0+sha256:abcd1234" }""");

        var result = AppHostPackageManagerResolver.Resolve(appHostDir);

        Assert.Equal(AppHostPackageManager.Pnpm, result.PackageManager);
        Assert.Equal("8.6.0", result.DeclaredVersion);
    }

    [Fact]
    public void Resolve_FallsBackToImmediateParentForPackageManagerField()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        WritePackageJson(workspace.WorkspaceRoot, """{ "packageManager": "pnpm@8.6.0" }""");

        var appHostDir = workspace.CreateDirectory("apphost");

        var result = AppHostPackageManagerResolver.Resolve(appHostDir);

        Assert.Equal(AppHostPackageManager.Pnpm, result.PackageManager);
    }

    [Theory]
    [InlineData("pnpm-lock.yaml", nameof(AppHostPackageManager.Pnpm))]
    [InlineData("bun.lock", nameof(AppHostPackageManager.Bun))]
    [InlineData("bun.lockb", nameof(AppHostPackageManager.Bun))]
    [InlineData("package-lock.json", nameof(AppHostPackageManager.Npm))]
    public void Resolve_DetectsLockfileInAppHostDirectory(string lockfileName, string expectedName)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var appHostDir = workspace.CreateDirectory("apphost");
        File.WriteAllText(Path.Combine(appHostDir.FullName, lockfileName), string.Empty);

        var result = AppHostPackageManagerResolver.Resolve(appHostDir);

        Assert.Equal(Enum.Parse<AppHostPackageManager>(expectedName), result.PackageManager);
        Assert.Equal(Path.Combine(appHostDir.FullName, lockfileName), result.Source);
    }

    [Fact]
    public void Resolve_DetectsYarnClassicFromLockfileAlone()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var appHostDir = workspace.CreateDirectory("apphost");
        File.WriteAllText(Path.Combine(appHostDir.FullName, "yarn.lock"), string.Empty);

        var result = AppHostPackageManagerResolver.Resolve(appHostDir);

        Assert.Equal(AppHostPackageManager.YarnClassic, result.PackageManager);
    }

    [Fact]
    public void Resolve_DetectsYarnBerryWhenYarnrcYmlPresent()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var appHostDir = workspace.CreateDirectory("apphost");
        File.WriteAllText(Path.Combine(appHostDir.FullName, "yarn.lock"), string.Empty);
        File.WriteAllText(Path.Combine(appHostDir.FullName, ".yarnrc.yml"), string.Empty);

        var result = AppHostPackageManagerResolver.Resolve(appHostDir);

        Assert.Equal(AppHostPackageManager.YarnBerry, result.PackageManager);
    }

    [Fact]
    public void Resolve_DetectsYarnBerryWhenYarnFolderPresent()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var appHostDir = workspace.CreateDirectory("apphost");
        File.WriteAllText(Path.Combine(appHostDir.FullName, "yarn.lock"), string.Empty);
        Directory.CreateDirectory(Path.Combine(appHostDir.FullName, ".yarn"));

        var result = AppHostPackageManagerResolver.Resolve(appHostDir);

        Assert.Equal(AppHostPackageManager.YarnBerry, result.PackageManager);
    }

    [Fact]
    public void Resolve_FallsBackToImmediateParentForLockfiles()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        File.WriteAllText(Path.Combine(workspace.WorkspaceRoot.FullName, "pnpm-lock.yaml"), string.Empty);

        var appHostDir = workspace.CreateDirectory("apphost");

        var result = AppHostPackageManagerResolver.Resolve(appHostDir);

        Assert.Equal(AppHostPackageManager.Pnpm, result.PackageManager);
    }

    [Fact]
    public void Resolve_IgnoresLockfilesAboveImmediateParent()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        File.WriteAllText(Path.Combine(workspace.WorkspaceRoot.FullName, "pnpm-lock.yaml"), string.Empty);

        var parent = workspace.CreateDirectory("workspace");
        var appHostDir = parent.CreateSubdirectory("apphost");

        var result = AppHostPackageManagerResolver.Resolve(appHostDir);

        Assert.Equal(AppHostPackageManager.Npm, result.PackageManager);
        Assert.Equal("default", result.Source);
    }

    [Fact]
    public void Resolve_PrefersPackageManagerFieldOverLockfile()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var appHostDir = workspace.CreateDirectory("apphost");
        WritePackageJson(appHostDir, """{ "packageManager": "pnpm@8.6.0" }""");
        File.WriteAllText(Path.Combine(appHostDir.FullName, "package-lock.json"), "{}");

        var result = AppHostPackageManagerResolver.Resolve(appHostDir);

        Assert.Equal(AppHostPackageManager.Pnpm, result.PackageManager);
    }

    [Fact]
    public void Resolve_IgnoresInvalidPackageManagerField()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var appHostDir = workspace.CreateDirectory("apphost");
        WritePackageJson(appHostDir, """{ "packageManager": "unknown@1.0.0" }""");

        var result = AppHostPackageManagerResolver.Resolve(appHostDir);

        Assert.Equal(AppHostPackageManager.Npm, result.PackageManager);
        Assert.Equal("default", result.Source);
    }

    [Fact]
    public void Resolve_IgnoresMalformedPackageJson()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var appHostDir = workspace.CreateDirectory("apphost");
        File.WriteAllText(Path.Combine(appHostDir.FullName, "package.json"), "{ not valid json");

        var result = AppHostPackageManagerResolver.Resolve(appHostDir);

        Assert.Equal(AppHostPackageManager.Npm, result.PackageManager);
    }

    [Theory]
    [InlineData(nameof(AppHostPackageManager.Npm), "npm")]
    [InlineData(nameof(AppHostPackageManager.Pnpm), "pnpm")]
    [InlineData(nameof(AppHostPackageManager.Bun), "bun")]
    [InlineData(nameof(AppHostPackageManager.YarnBerry), "yarn")]
    [InlineData(nameof(AppHostPackageManager.YarnClassic), "yarn")]
    public void GetExecutableName_ReturnsExpected(string packageManagerName, string expected)
    {
        var packageManager = Enum.Parse<AppHostPackageManager>(packageManagerName);
        Assert.Equal(expected, AppHostPackageManagerResolver.GetExecutableName(packageManager));
    }

    private static void WritePackageJson(DirectoryInfo directory, string contents)
    {
        File.WriteAllText(Path.Combine(directory.FullName, "package.json"), contents);
    }
}
