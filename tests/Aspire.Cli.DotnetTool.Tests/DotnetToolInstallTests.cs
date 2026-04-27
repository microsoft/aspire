// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.InteropServices;
using Aspire.Tests.Shared;
using Aspire.Templates.Tests;
using Xunit;

namespace Aspire.Cli.DotnetTool.Tests;

/// <summary>
/// Validates the Aspire CLI dotnet tool can be installed from a NuGet package and invoked.
/// </summary>
public sealed class DotnetToolInstallTests(ITestOutputHelper output) : IAsyncLifetime
{
    private string? _toolPath;
    private string? _aspirePath;

    public async ValueTask InitializeAsync()
    {
        var nugetDir = FindNugetPackagesDirectory();
        var package = CliPackageDiscovery.FindAspireCliPointerPackage(nugetDir);

        _toolPath = Directory.CreateTempSubdirectory(".aspire-tool-test").FullName;

        // Write a NuGet.Config that clears all sources and only uses the local built packages
        // plus public feeds for transitive dependencies. Package source mapping ensures
        // Aspire.Cli resolves exclusively from the local feed.
        var nugetConfigPath = Path.Combine(_toolPath, "NuGet.Config");
        File.WriteAllText(nugetConfigPath, $"""
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <fallbackPackageFolders>
                <clear />
              </fallbackPackageFolders>
              <packageSources>
                <clear />
                <add key="built-local" value="{nugetDir}" />
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

        var installArgs = $"tool install {package.PackageId} --tool-path \"{_toolPath}\" --configfile \"{nugetConfigPath}\" --version {package.Version} --no-cache";
        var result = await new ToolCommand("dotnet", output, label: "install")
            .WithTimeout(TimeSpan.FromMinutes(5))
            .ExecuteAsync(installArgs);

        result.EnsureSuccessful("dotnet tool install failed");

        // NativeAOT tools use Runner="executable" in DotnetToolSettings.xml.
        // On Windows the SDK creates a .cmd shim; on Unix it creates a symlink.
        // Framework-dependent tools (Runner="dotnet") get an .exe shim on Windows.
        _aspirePath = FindToolShim(_toolPath);
        Assert.True(_aspirePath is not null, BuildShimNotFoundMessage(_toolPath));
        Assert.True(Directory.Exists(Path.Combine(_toolPath, ".store")), BuildStoreNotFoundMessage(_toolPath));
    }

    public ValueTask DisposeAsync()
    {
        if (_toolPath is not null)
        {
            try
            {
                Directory.Delete(_toolPath, recursive: true);
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task AspireVersion_ReturnsSuccessAndVersion()
    {
        var result = await new ToolCommand(_aspirePath!, output, label: "version")
            .WithTimeout(TimeSpan.FromSeconds(30))
            .ExecuteAsync("--version");

        result.EnsureSuccessful("aspire --version failed");
        Assert.False(string.IsNullOrWhiteSpace(result.Output), "aspire --version produced no output");
    }

    [Fact]
    public async Task AspireNewEmpty_CreatesAppHostFiles()
    {
        var outputDir = Directory.CreateTempSubdirectory("aspire-empty-tool-test");
        try
        {
            var result = await new ToolCommand(_aspirePath!, output, label: "new-empty")
                .WithTimeout(TimeSpan.FromMinutes(2))
                .ExecuteAsync($"new aspire-empty --name VerifyEmpty --output \"{outputDir.FullName}\" --non-interactive --nologo");

            result.EnsureSuccessful("aspire new aspire-empty failed");
            Assert.True(File.Exists(Path.Combine(outputDir.FullName, "apphost.cs")), "apphost.cs was not created.");
            Assert.True(File.Exists(Path.Combine(outputDir.FullName, "aspire.config.json")), "aspire.config.json was not created.");
        }
        finally
        {
            outputDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task AspireHelp_ReturnsSuccessAndUsageText()
    {
        var result = await new ToolCommand(_aspirePath!, output, label: "help")
            .WithTimeout(TimeSpan.FromSeconds(30))
            .ExecuteAsync("--help");

        result.EnsureSuccessful("aspire --help failed");
        Assert.Contains("aspire", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    private static string FindNugetPackagesDirectory()
    {
        // CI sets BUILT_NUGETS_PATH
        var builtNugetsPath = Environment.GetEnvironmentVariable("BUILT_NUGETS_PATH");
        if (!string.IsNullOrEmpty(builtNugetsPath) && Directory.Exists(builtNugetsPath))
        {
            return builtNugetsPath;
        }

        // Local dev: check artifacts/packages/{Debug,Release}/Shipping relative to repo root
        var repoRoot = FindRepoRoot();
        foreach (var config in new[] { "Debug", "Release" })
        {
            var candidatePath = Path.Combine(repoRoot, "artifacts", "packages", config, "Shipping");
            if (Directory.Exists(candidatePath) &&
                Directory.GetFiles(candidatePath, "Aspire.Cli.*.nupkg").Length > 0)
            {
                return candidatePath;
            }
        }

        throw new InvalidOperationException(
            "Cannot find built NuGet packages. Set BUILT_NUGETS_PATH or build the Aspire.Cli.Tool.csproj package first.");
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "global.json")) &&
                File.Exists(Path.Combine(dir, "Aspire.slnx")))
            {
                return dir;
            }
            dir = Path.GetDirectoryName(dir);
        }
        throw new InvalidOperationException("Cannot find repository root (no global.json + Aspire.slnx found).");
    }

    /// <summary>
    /// Finds the tool shim/wrapper in the tool-path directory.
    /// NativeAOT tools (Runner="executable") produce a .cmd shim on Windows and a symlink on Unix.
    /// Framework-dependent tools (Runner="dotnet") produce an .exe shim on Windows.
    /// </summary>
    private static string? FindToolShim(string toolPath)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // NativeAOT tools → aspire.cmd; framework-dependent tools → aspire.exe
            var cmdPath = Path.Combine(toolPath, "aspire.cmd");
            if (File.Exists(cmdPath))
            {
                return cmdPath;
            }

            var exePath = Path.Combine(toolPath, "aspire.exe");
            if (File.Exists(exePath))
            {
                return exePath;
            }

            return null;
        }

        // Unix: both tool types produce the same "aspire" file (symlink or executable)
        var unixPath = Path.Combine(toolPath, "aspire");
        return File.Exists(unixPath) ? unixPath : null;
    }

    private static string BuildShimNotFoundMessage(string toolPath)
    {
        var files = Directory.Exists(toolPath)
            ? string.Join(", ", Directory.GetFiles(toolPath).Select(Path.GetFileName))
            : "(directory does not exist)";
        return $"Tool shim not found in {toolPath}. Files present: [{files}]";
    }

    private static string BuildStoreNotFoundMessage(string toolPath)
    {
        var directories = Directory.Exists(toolPath)
            ? string.Join(", ", Directory.GetDirectories(toolPath).Select(Path.GetFileName))
            : "(directory does not exist)";
        return $"Tool store directory not found in {toolPath}. Directories present: [{directories}]";
    }
}
