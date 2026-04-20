// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.EndToEnd.Tests.Helpers;
using Aspire.Cli.Resources;
using Aspire.Cli.Tests.Utils;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

/// <summary>
/// End-to-end tests for the Aspire CLI installed as a .NET dotnet tool.
/// Verifies that the NativeAOT-based tool package can be installed via <c>dotnet tool install</c>,
/// that the installed binary works correctly, and that the self-update detection correctly
/// identifies the dotnet tool installation method.
/// </summary>
public sealed class DotnetToolE2ETests(ITestOutputHelper output)
{
    private const string ToolPath = "/tmp/aspire-tool";

    /// <summary>
    /// Verifies that the Aspire CLI can be installed as a .NET tool inside a Docker container
    /// and that <c>aspire --version</c> and <c>aspire --help</c> produce expected output.
    /// Also asserts that the <c>.store</c> directory (NativeAOT tool marker) exists beside the binary.
    /// </summary>
    [Fact]
    public async Task DotnetToolInstall_VersionAndHelp()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var (packagesDir, version) = FindToolPackageInfo(repoRoot);

        var workspace = TemporaryWorkspace.Create(output);
        var containerPackagesPath = CopyPackagesAndWriteNuGetConfig(packagesDir, workspace);

        var strategy = CliInstallStrategy.LatestGa();
        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, workspace: workspace);

        var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);

        await InstallCliAsDotnetToolAsync(auto, counter, containerPackagesPath, version);

        // Verify aspire --version succeeds
        await auto.ClearScreenAsync(counter);
        await auto.TypeAsync("aspire --version");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Verify aspire --help succeeds and shows usage text
        await auto.ClearScreenAsync(counter);
        await auto.TypeAsync("aspire --help");
        await auto.EnterAsync();
        await auto.WaitUntilAsync(
            s => s.ContainsText("aspire"),
            timeout: TimeSpan.FromSeconds(30),
            description: "waiting for aspire --help output");
        await auto.WaitForSuccessPromptAsync(counter);

        // Verify that `command -v aspire` points to the tool path
        await auto.ClearScreenAsync(counter);
        await auto.TypeAsync($"command -v aspire | grep -q '{ToolPath}' && echo TOOL_PATH_OK");
        await auto.EnterAsync();
        await auto.WaitUntilAsync(
            s => s.ContainsText("TOOL_PATH_OK"),
            timeout: TimeSpan.FromSeconds(10),
            description: "waiting for aspire to be on the expected tool path");
        await auto.WaitForSuccessPromptAsync(counter);

        // Verify the .store directory exists (NativeAOT dotnet tool marker)
        await auto.ClearScreenAsync(counter);
        await auto.TypeAsync($"test -d {ToolPath}/.store && echo STORE_DIR_OK");
        await auto.EnterAsync();
        await auto.WaitUntilAsync(
            s => s.ContainsText("STORE_DIR_OK"),
            timeout: TimeSpan.FromSeconds(10),
            description: "waiting for .store directory to exist beside the tool binary");
        await auto.WaitForSuccessPromptAsync(counter);

        // Exit the shell
        await auto.TypeAsync("exit");
        await auto.EnterAsync();

        await pendingRun;
    }

    /// <summary>
    /// Verifies that <c>aspire update --self</c> detects the dotnet tool installation and
    /// shows the appropriate message directing users to use <c>dotnet tool update</c> instead
    /// of the built-in self-update mechanism.
    /// </summary>
    [Fact]
    public async Task DotnetToolInstall_UpdateSelf_ShowsDotnetToolMessage()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var (packagesDir, version) = FindToolPackageInfo(repoRoot);

        var workspace = TemporaryWorkspace.Create(output);
        var containerPackagesPath = CopyPackagesAndWriteNuGetConfig(packagesDir, workspace);

        var strategy = CliInstallStrategy.LatestGa();
        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, workspace: workspace);

        var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);

        await InstallCliAsDotnetToolAsync(auto, counter, containerPackagesPath, version);

        // Run aspire update --self and verify it shows the dotnet tool message
        await auto.ClearScreenAsync(counter);
        await auto.TypeAsync("aspire update --self");
        await auto.EnterAsync();
        await auto.WaitUntilAsync(
            s => s.ContainsText(UpdateCommandStrings.DotNetToolSelfUpdateMessage)
                 && s.ContainsText("dotnet tool update"),
            timeout: TimeSpan.FromSeconds(30),
            description: "waiting for dotnet tool self-update message");
        await auto.WaitForSuccessPromptAsync(counter);

        // Exit the shell
        await auto.TypeAsync("exit");
        await auto.EnterAsync();

        await pendingRun;
    }

    /// <summary>
    /// Finds the directory containing built NuGet packages and the tool package version.
    /// Skips gracefully in local dev when packages aren't available; fails in CI.
    /// </summary>
    private static (string PackagesDir, string Version) FindToolPackageInfo(string repoRoot)
    {
        var packagesDir = FindNugetPackagesDirectory(repoRoot);
        var (_, version) = FindToolPackage(packagesDir);

        // Verify the linux-x64 RID package exists (required for Docker E2E tests)
        var ridPackagePattern = $"Aspire.Cli.linux-x64.{version}.nupkg";
        var ridPackages = Directory.GetFiles(packagesDir, ridPackagePattern);
        if (ridPackages.Length == 0)
        {
            var isCI = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CI")) ||
                       !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"));
            if (isCI)
            {
                throw new InvalidOperationException(
                    $"CI environment but linux-x64 RID package not found: {ridPackagePattern} in {packagesDir}. " +
                    "The E2E test project has RequiresNugets=true so built packages should be available.");
            }

            Assert.Skip($"linux-x64 RID package not found ({ridPackagePattern}). " +
                         "Build the tool packages first or run in CI where BUILT_NUGETS_PATH is set.");
        }

        return (packagesDir, version);
    }

    /// <summary>
    /// Copies Aspire.Cli*.nupkg files to the workspace and writes a NuGet.Config on the host
    /// that references the container-side packages path. Returns the container-side packages path.
    /// </summary>
    private static string CopyPackagesAndWriteNuGetConfig(string packagesDir, TemporaryWorkspace workspace)
    {
        var packagesSubDir = workspace.CreateDirectory("packages");
        var containerPackagesPath = $"/workspace/{workspace.WorkspaceRoot.Name}/packages";

        // Copy only the Aspire.Cli packages (pointer + RID-specific)
        foreach (var nupkg in Directory.GetFiles(packagesDir, "Aspire.Cli*.nupkg"))
        {
            // Skip symbol packages
            if (nupkg.EndsWith(".snupkg", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            File.Copy(nupkg, Path.Combine(packagesSubDir.FullName, Path.GetFileName(nupkg)));
        }

        // Write NuGet.Config with container-side paths
        var nugetConfigPath = Path.Combine(workspace.WorkspaceRoot.FullName, "NuGet.Config");
        File.WriteAllText(nugetConfigPath, $"""
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <fallbackPackageFolders>
                <clear />
              </fallbackPackageFolders>
              <packageSources>
                <clear />
                <add key="built-local" value="{containerPackagesPath}" />
                <add key="dotnet-public" value="https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-public/nuget/v3/index.json" />
                <add key="dotnet10" value="https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet10/nuget/v3/index.json" />
              </packageSources>
              <packageSourceMapping>
                <packageSource key="built-local">
                  <package pattern="Aspire*" />
                </packageSource>
                <packageSource key="dotnet-public">
                  <package pattern="*" />
                </packageSource>
                <packageSource key="dotnet10">
                  <package pattern="*" />
                </packageSource>
              </packageSourceMapping>
              <disabledPackageSources>
                <clear />
              </disabledPackageSources>
            </configuration>
            """);

        return containerPackagesPath;
    }

    /// <summary>
    /// Installs the Aspire CLI as a dotnet tool inside the Docker container, then adds
    /// the tool path to PATH.
    /// </summary>
    private static async Task InstallCliAsDotnetToolAsync(
        Hex1bTerminalAutomator auto,
        SequenceCounter counter,
        string containerPackagesPath,
        string version)
    {
        var containerNugetConfig = containerPackagesPath.Replace("/packages", "/NuGet.Config");

        // Install the tool using dotnet tool install
        var installCommand = $"dotnet tool install Aspire.Cli --tool-path {ToolPath} --configfile {containerNugetConfig} --version {version} --no-cache";
        await auto.TypeAsync(installCommand);
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromMinutes(5));

        // Add tool path to PATH
        await auto.TypeAsync($"export PATH={ToolPath}:$PATH");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);
    }

    private static string FindNugetPackagesDirectory(string repoRoot)
    {
        // CI sets BUILT_NUGETS_PATH
        var builtNugetsPath = Environment.GetEnvironmentVariable("BUILT_NUGETS_PATH");
        if (!string.IsNullOrEmpty(builtNugetsPath) && Directory.Exists(builtNugetsPath))
        {
            return builtNugetsPath;
        }

        // Local dev: check artifacts/packages/{Debug,Release}/Shipping relative to repo root
        foreach (var config in new[] { "Debug", "Release" })
        {
            var candidatePath = Path.Combine(repoRoot, "artifacts", "packages", config, "Shipping");
            if (Directory.Exists(candidatePath) &&
                Directory.GetFiles(candidatePath, "Aspire.Cli.*.nupkg").Length > 0)
            {
                return candidatePath;
            }
        }

        var isCI = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CI")) ||
                   !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"));
        if (isCI)
        {
            throw new InvalidOperationException(
                "CI environment but cannot find built NuGet packages. " +
                "BUILT_NUGETS_PATH is not set and artifacts/packages/*/Shipping contains no Aspire.Cli packages. " +
                "The E2E test project has RequiresNugets=true so packages should be available.");
        }

        Assert.Skip(
            "Cannot find built NuGet packages. Set BUILT_NUGETS_PATH or run './build.sh --pack' first.");
        return null!; // unreachable
    }

    private static (string PackageId, string Version) FindToolPackage(string nugetDir)
    {
        // Find Aspire.Cli.<version>.nupkg but NOT Aspire.Cli.<rid>.<version>.nupkg
        var nupkgs = Directory.GetFiles(nugetDir, "Aspire.Cli.*.nupkg")
            .Where(f =>
            {
                var name = Path.GetFileNameWithoutExtension(f);
                var remainder = name["Aspire.Cli.".Length..];
                return remainder.Length > 0 && char.IsDigit(remainder[0]);
            })
            .Where(f => !f.EndsWith(".snupkg", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (nupkgs.Length == 0)
        {
            throw new InvalidOperationException(
                $"No Aspire.Cli tool nupkg found in {nugetDir}. Available files: " +
                string.Join(", ", Directory.GetFiles(nugetDir, "Aspire.Cli.*").Select(Path.GetFileName)));
        }

        var fileName = Path.GetFileNameWithoutExtension(nupkgs[0]);
        var version = fileName["Aspire.Cli.".Length..];

        return ("Aspire.Cli", version);
    }
}
