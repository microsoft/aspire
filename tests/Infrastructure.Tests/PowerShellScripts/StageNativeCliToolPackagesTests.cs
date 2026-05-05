// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.TestUtilities;
using Xunit;

namespace Infrastructure.Tests;

public sealed class StageNativeCliToolPackagesTests : IDisposable
{
    private const string PackageVersion = "13.4.0-preview.1.26229.13";
    private const string DifferentPackageVersion = "13.4.1-preview.1.26229.13";

    private static readonly string[] s_productionRids =
    [
        "linux-x64",
        "linux-arm64",
        "linux-musl-x64",
        "osx-x64",
        "osx-arm64",
        "win-x64",
        "win-arm64"
    ];

    private readonly TestTempDirectory _tempDir = new();
    private readonly string _scriptPath;
    private readonly ITestOutputHelper _output;

    public StageNativeCliToolPackagesTests(ITestOutputHelper output)
    {
        _output = output;
        _scriptPath = Path.Combine(FindRepoRoot(), "eng", "scripts", "stage-native-cli-tool-packages.ps1");
    }

    public void Dispose() => _tempDir.Dispose();

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task StagesCanonicalPointerAndRidPackages()
    {
        var downloadRoot = CreateDownloadRoot();
        var shippingDir = CreateShippingDir();

        foreach (var rid in s_productionRids)
        {
            CreateNativeArchivePackages(downloadRoot, rid);
        }
        CreatePackage(downloadRoot, "unrelated", "Release", "Shipping", $"Aspire.Cli.{PackageVersion}.nupkg");

        var result = await RunScript(downloadRoot, shippingDir);

        result.EnsureSuccessful();

        var stagedPackageNames = GetStagedPackageNames(shippingDir);
        Assert.Equal(
            [
                $"Aspire.Cli.{PackageVersion}.nupkg",
                $"Aspire.Cli.linux-arm64.{PackageVersion}.nupkg",
                $"Aspire.Cli.linux-musl-x64.{PackageVersion}.nupkg",
                $"Aspire.Cli.linux-x64.{PackageVersion}.nupkg",
                $"Aspire.Cli.osx-arm64.{PackageVersion}.nupkg",
                $"Aspire.Cli.osx-x64.{PackageVersion}.nupkg",
                $"Aspire.Cli.win-arm64.{PackageVersion}.nupkg",
                $"Aspire.Cli.win-x64.{PackageVersion}.nupkg"
            ],
            stagedPackageNames);
        Assert.Contains("Skipping non-canonical Aspire.Cli pointer package", result.Output);
        Assert.Contains("Skipping Aspire.Cli package outside native archive artifacts", result.Output);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task FailsWhenCanonicalPointerPackageIsMissing()
    {
        var downloadRoot = CreateDownloadRoot();
        var shippingDir = CreateShippingDir();

        CreateNativeArchivePackages(downloadRoot, "linux-x64");
        CreateNativeArchivePackage(downloadRoot, "win-x64", $"Aspire.Cli.win-x64.{PackageVersion}.nupkg");

        var result = await RunScript(downloadRoot, shippingDir);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Expected exactly one canonical Aspire.Cli pointer package", result.Output);
        Assert.Contains("native_archives_win_x64", result.Output);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task FailsWhenDuplicateCanonicalPointerPackagesAreDownloaded()
    {
        var downloadRoot = CreateDownloadRoot();
        var shippingDir = CreateShippingDir();

        CreateNativeArchivePackages(downloadRoot, "win-x64");
        CreatePackage(downloadRoot, "native_archives_win_x64", "Duplicate", "Shipping", $"Aspire.Cli.{PackageVersion}.nupkg");

        var result = await RunScript(downloadRoot, shippingDir);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Expected exactly one canonical Aspire.Cli pointer package", result.Output);
        Assert.Contains("but found 2", result.Output);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task FailsWhenRidPackageDoesNotMatchNativeArchiveRoot()
    {
        var downloadRoot = CreateDownloadRoot();
        var shippingDir = CreateShippingDir();

        CreateNativeArchivePackages(downloadRoot, "win-x64");
        CreateNativeArchivePackage(downloadRoot, "linux-x64", $"Aspire.Cli.osx-x64.{PackageVersion}.nupkg");

        var result = await RunScript(downloadRoot, shippingDir);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Unexpected Aspire.Cli package", result.Output);
        Assert.Contains("Aspire.Cli.linux-x64 RID-specific package", result.Output);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task FailsWhenRidPackageNameIsMalformed()
    {
        var downloadRoot = CreateDownloadRoot();
        var shippingDir = CreateShippingDir();

        CreateNativeArchivePackages(downloadRoot, "win-x64");
        CreateNativeArchivePackage(downloadRoot, "linux-x64", "Aspire.Cli.linux-x64.13x.nupkg");

        var result = await RunScript(downloadRoot, shippingDir);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Unexpected Aspire.Cli package", result.Output);
        Assert.Contains("Aspire.Cli.linux-x64 RID-specific package", result.Output);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task FailsWhenDuplicateRidPackagesAreDownloaded()
    {
        var downloadRoot = CreateDownloadRoot();
        var shippingDir = CreateShippingDir();

        CreateNativeArchivePackages(downloadRoot, "win-x64");
        CreateNativeArchivePackage(downloadRoot, "linux-x64", $"Aspire.Cli.linux-x64.{PackageVersion}.nupkg");
        CreatePackage(downloadRoot, "native_archives_linux_x64", "Duplicate", "Shipping", $"Aspire.Cli.linux-x64.{PackageVersion}.nupkg");

        var result = await RunScript(downloadRoot, shippingDir);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Expected exactly one Aspire.Cli RID-specific package per RID", result.Output);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task FailsWhenDuplicateRidPackagesHaveDifferentVersions()
    {
        var downloadRoot = CreateDownloadRoot();
        var shippingDir = CreateShippingDir();

        CreateNativeArchivePackages(downloadRoot, "win-x64");
        CreateNativeArchivePackage(downloadRoot, "linux-x64", $"Aspire.Cli.linux-x64.{PackageVersion}.nupkg");
        CreateNativeArchivePackage(downloadRoot, "linux-x64", $"Aspire.Cli.linux-x64.{DifferentPackageVersion}.nupkg");

        var result = await RunScript(downloadRoot, shippingDir);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Expected exactly one Aspire.Cli RID-specific package per RID", result.Output);
        Assert.Contains($"Aspire.Cli.linux-x64.{PackageVersion}.nupkg", result.Output);
        Assert.Contains($"Aspire.Cli.linux-x64.{DifferentPackageVersion}.nupkg", result.Output);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task FailsWhenRidPackageVersionDoesNotMatchCanonicalPointerPackage()
    {
        var downloadRoot = CreateDownloadRoot();
        var shippingDir = CreateShippingDir();

        CreateNativeArchivePackages(downloadRoot, "win-x64");
        CreateNativeArchivePackage(downloadRoot, "linux-x64", $"Aspire.Cli.linux-x64.{DifferentPackageVersion}.nupkg");

        var result = await RunScript(downloadRoot, shippingDir);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("must match canonical pointer", result.Output);
        Assert.Contains(PackageVersion, result.Output);
        Assert.Contains($"Aspire.Cli.linux-x64.{DifferentPackageVersion}.nupkg", result.Output);
        Assert.Contains(DifferentPackageVersion, result.Output);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task FailsWhenNoPackagesAreUnderNativeArchiveArtifacts()
    {
        var downloadRoot = CreateDownloadRoot();
        var shippingDir = CreateShippingDir();

        CreatePackage(downloadRoot, "unrelated", "Release", "Shipping", $"Aspire.Cli.{PackageVersion}.nupkg");
        CreatePackage(downloadRoot, "also-unrelated", "Release", "Shipping", $"Aspire.Cli.linux-x64.{PackageVersion}.nupkg");

        var result = await RunScript(downloadRoot, shippingDir);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("No native CLI tool packages were found", result.Output);
        Assert.Contains("native_archives_<rid>", result.Output);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task UsesConfiguredCanonicalPointerArtifactName()
    {
        var downloadRoot = CreateDownloadRoot();
        var shippingDir = CreateShippingDir();

        CreateNativeArchivePackages(downloadRoot, "linux-x64");
        CreateNativeArchivePackage(downloadRoot, "win-x64", $"Aspire.Cli.win-x64.{PackageVersion}.nupkg");

        var result = await RunScript(
            downloadRoot,
            shippingDir,
            "-CanonicalPointerArtifactName", "native_archives_linux_x64");

        result.EnsureSuccessful();

        var stagedPackageNames = GetStagedPackageNames(shippingDir);
        Assert.Equal(
            [
                $"Aspire.Cli.{PackageVersion}.nupkg",
                $"Aspire.Cli.linux-x64.{PackageVersion}.nupkg",
                $"Aspire.Cli.win-x64.{PackageVersion}.nupkg"
            ],
            stagedPackageNames);
    }

    private async Task<CommandResult> RunScript(string downloadRoot, string shippingDir, params string[] additionalArgs)
    {
        using var cmd = new PowerShellCommand(_scriptPath, _output)
            .WithTimeout(TimeSpan.FromMinutes(2));

        var args = new List<string>
        {
            "-DownloadRoot", $"\"{downloadRoot}\"",
            "-ShippingDir", $"\"{shippingDir}\""
        };
        args.AddRange(additionalArgs);

        return await cmd.ExecuteAsync([.. args]);
    }

    private string CreateDownloadRoot()
    {
        var path = Path.Combine(_tempDir.Path, Path.GetRandomFileName(), "download");
        Directory.CreateDirectory(path);
        return path;
    }

    private string CreateShippingDir()
    {
        return Path.Combine(_tempDir.Path, Path.GetRandomFileName(), "shipping");
    }

    private static void CreateNativeArchivePackages(string downloadRoot, string rid)
    {
        CreateNativeArchivePackage(downloadRoot, rid, $"Aspire.Cli.{PackageVersion}.nupkg");
        CreateNativeArchivePackage(downloadRoot, rid, $"Aspire.Cli.{rid}.{PackageVersion}.nupkg");
    }

    private static void CreateNativeArchivePackage(string downloadRoot, string rid, string packageName)
    {
        CreatePackage(downloadRoot, $"native_archives_{rid.Replace('-', '_')}", "Release", "Shipping", packageName);
    }

    private static void CreatePackage(string downloadRoot, params string[] pathParts)
    {
        var path = Path.Combine([downloadRoot, .. pathParts]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "package");
    }

    private static string[] GetStagedPackageNames(string shippingDir)
    {
        return Directory.GetFiles(shippingDir, "Aspire.Cli*.nupkg")
            .Select(Path.GetFileName)
            .Order(StringComparer.Ordinal)
            .ToArray()!;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Aspire.slnx")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Could not find repository root");
    }
}
