// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Aspire.TestUtilities;
using Microsoft.AspNetCore.InternalTesting;

namespace Aspire.Cli.Tests.Npm;

[RequiresTools(["node"])]
public class AspireJsLauncherTests
{
    [Fact]
    public void LauncherFailsWhenRidPackageVersionMismatchesPointerPackageVersion()
    {
        using var testRoot = new TestTempDirectory();
        var pointerVersion = "1.2.3";
        var ridPackageVersion = "1.2.4";

        var rid = GetCurrentRid();
        var ridPackageName = $"@microsoft/aspire-cli-{rid}";
        var layout = CreateFakeNpmLayout(testRoot.Path, pointerVersion, rid, ridPackageName, ridPackageVersion);

        var result = RunLauncher(layout.LauncherScript, layout.CacheDir, [layout.ProbeScript, "--version"]);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("version", result.StdErr, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(ridPackageName, result.StdErr);
        Assert.Contains(pointerVersion, result.StdErr);
        Assert.Contains(ridPackageVersion, result.StdErr);
    }

    [Fact]
    public void LauncherFailsWithRepairGuidanceWhenRidPackageJsonIsInvalid()
    {
        using var testRoot = new TestTempDirectory();
        var pointerVersion = "1.2.3";
        var rid = GetCurrentRid();
        var ridPackageName = $"@microsoft/aspire-cli-{rid}";
        var layout = CreateFakeNpmLayout(testRoot.Path, pointerVersion, rid, ridPackageName, pointerVersion);
        File.WriteAllText(layout.RidPackageJsonPath, "{ not valid json");

        var result = RunLauncher(layout.LauncherScript, layout.CacheDir, [layout.ProbeScript, "--version"]);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Aspire CLI installation is corrupted", result.StdErr);
        Assert.Contains(ridPackageName, result.StdErr);
        Assert.Contains("package.json", result.StdErr);
        Assert.Contains("Reinstall @microsoft/aspire-cli", result.StdErr);
    }

    [Fact]
    public void LauncherCleansUpTempFileWhenChmodFails()
    {
        Assert.SkipUnless(!RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "chmod is POSIX-only");

        using var testRoot = new TestTempDirectory();
        var pointerVersion = "1.0.0";
        var rid = GetCurrentRid();
        var ridPackageName = $"@microsoft/aspire-cli-{rid}";
        var layout = CreateFakeNpmLayout(testRoot.Path, pointerVersion, rid, ridPackageName, pointerVersion);

        var monkeyPatchScript = Path.Combine(testRoot.Path, "patch-chmod.js");
        File.WriteAllText(monkeyPatchScript, """
            const fs = require('fs');
            const originalChmodSync = fs.chmodSync;
            fs.chmodSync = function(path, mode) {
                if (path.includes('.tmp')) {
                    throw new Error('Simulated chmod failure');
                }
                return originalChmodSync.apply(this, arguments);
            };
            """);

        var result = RunLauncher(layout.LauncherScript, layout.CacheDir, [layout.ProbeScript, "--version"], requiredScript: monkeyPatchScript);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Simulated chmod failure", result.StdErr);
        var tmpFiles = Directory.GetFiles(layout.CacheDir, "*.tmp", SearchOption.AllDirectories);
        Assert.Empty(tmpFiles);
    }

    [Fact]
    public void LauncherCleansUpTempFileWhenCopyFailsAfterCreatingTempFile()
    {
        using var testRoot = new TestTempDirectory();
        var pointerVersion = "1.0.0";
        var rid = GetCurrentRid();
        var ridPackageName = $"@microsoft/aspire-cli-{rid}";
        var layout = CreateFakeNpmLayout(testRoot.Path, pointerVersion, rid, ridPackageName, pointerVersion);

        var monkeyPatchScript = Path.Combine(testRoot.Path, "patch-copy.js");
        File.WriteAllText(monkeyPatchScript, """
            const fs = require('fs');
            const originalCopyFileSync = fs.copyFileSync;
            fs.copyFileSync = function(source, destination, mode) {
                originalCopyFileSync.apply(this, arguments);
                if (String(destination).includes('.tmp')) {
                    throw new Error('Simulated copy failure after temp file creation');
                }
            };
            """);

        var result = RunLauncher(layout.LauncherScript, layout.CacheDir, [layout.ProbeScript, "--version"], requiredScript: monkeyPatchScript);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Simulated copy failure", result.StdErr);
        var tmpFiles = Directory.GetFiles(layout.CacheDir, "*.tmp", SearchOption.AllDirectories);
        Assert.Empty(tmpFiles);
    }

    [Fact]
    public void LauncherCopiesBinaryToCacheAndSetsEnvironmentVariables()
    {
        using var testRoot = new TestTempDirectory();
        var pointerVersion = "2.0.0";
        var rid = GetCurrentRid();
        var ridPackageName = $"@microsoft/aspire-cli-{rid}";
        var layout = CreateFakeNpmLayout(testRoot.Path, pointerVersion, rid, ridPackageName, pointerVersion);

        var result = RunLauncher(layout.LauncherScript, layout.CacheDir, [layout.ProbeScript, "--test-env"]);

        Assert.Equal(0, result.ExitCode);

        var output = JsonDocument.Parse(result.StdOut);
        Assert.Equal("@microsoft/aspire-cli", output.RootElement.GetProperty("ASPIRE_NPM_PACKAGE").GetString());
        Assert.Equal(pointerVersion, output.RootElement.GetProperty("ASPIRE_NPM_PACKAGE_VERSION").GetString());
        Assert.Equal(rid, output.RootElement.GetProperty("ASPIRE_NPM_PACKAGE_RID").GetString());

        var args = output.RootElement.GetProperty("args");
        Assert.Equal(1, args.GetArrayLength());
        Assert.Equal("--test-env", args[0].GetString());

        var binaryName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "aspire.exe" : "aspire";
        var cachedBinaryPath = Path.Combine(layout.CacheDir, pointerVersion, rid, "bin", binaryName);
        Assert.True(File.Exists(cachedBinaryPath), $"Expected cached binary at {cachedBinaryPath}");
    }

    [Fact]
    public void LauncherSupportsAllRidsDefinedInPackScript()
    {
        var scriptPath = Path.Combine(GetRepoRoot(), "eng", "scripts", "pack-cli-npm-package.ps1");
        var supportedRids = GetSupportedRidsFromPackScript(scriptPath);
        var launcherScriptPath = Path.Combine(GetRepoRoot(), "eng", "clipack", "npm", "aspire.js");

        using var testRoot = new TestTempDirectory();
        var probeScript = Path.Combine(testRoot.Path, "detect-rids.js");
        File.WriteAllText(probeScript, $$"""
            const launcher = require({{JsonSerializer.Serialize(launcherScriptPath)}});
            const cases = {{JsonSerializer.Serialize(supportedRids.Select(CreateRidProbeCase).ToArray())}};
            const detected = cases.map(testCase => launcher.__testing.detectRid(testCase.Platform, testCase.Arch, testCase.Musl));
            console.log(JSON.stringify(detected));
            """);

        var result = RunNodeScript(probeScript);

        Assert.Equal(0, result.ExitCode);
        var detectedRids = JsonSerializer.Deserialize<string[]>(result.StdOut) ?? [];
        Assert.Equal(supportedRids.Order(StringComparer.Ordinal).ToArray(), detectedRids.Order(StringComparer.Ordinal).ToArray());
    }

    private static string GetCurrentRid()
    {
        using var testRoot = new TestTempDirectory();
        var launcherScriptPath = Path.Combine(GetRepoRoot(), "eng", "clipack", "npm", "aspire.js");
        var probeScript = Path.Combine(testRoot.Path, "detect-current-rid.js");

        // Derive the fixture RID from the Node launcher itself. The Node runtime
        // architecture and libc detection can differ from the .NET test host.
        File.WriteAllText(probeScript, $$"""
            const launcher = require({{JsonSerializer.Serialize(launcherScriptPath)}});
            console.log(launcher.__testing.detectRid());
            """);

        var result = RunNodeScript(probeScript);

        Assert.Equal(0, result.ExitCode);
        return result.StdOut.Trim();
    }

    private static string[] GetSupportedRidsFromPackScript(string scriptPath)
    {
        var scriptContent = File.ReadAllText(scriptPath);
        var supportedRidsMatch = Regex.Match(scriptContent, @"\$supportedRids\s*=\s*@\((?<rids>[^)]*)\)", RegexOptions.Singleline);
        Assert.True(supportedRidsMatch.Success, "Could not find $supportedRids in pack-cli-npm-package.ps1.");

        return Regex.Matches(supportedRidsMatch.Groups["rids"].Value, @"'(?<rid>[^']+)'")
            .Select(match => match.Groups["rid"].Value)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static RidProbeCase CreateRidProbeCase(string rid)
    {
        if (rid.StartsWith("win-", StringComparison.Ordinal))
        {
            return new RidProbeCase("win32", rid["win-".Length..], false);
        }

        if (rid.StartsWith("osx-", StringComparison.Ordinal))
        {
            return new RidProbeCase("darwin", rid["osx-".Length..], false);
        }

        if (rid.StartsWith("linux-musl-", StringComparison.Ordinal))
        {
            return new RidProbeCase("linux", rid["linux-musl-".Length..], true);
        }

        if (rid.StartsWith("linux-", StringComparison.Ordinal))
        {
            return new RidProbeCase("linux", rid["linux-".Length..], false);
        }

        throw new InvalidOperationException($"Unsupported RID shape in pack script: {rid}");
    }

    private static FakeNpmLayout CreateFakeNpmLayout(
        string rootPath,
        string pointerVersion,
        string rid,
        string ridPackageName,
        string ridPackageVersion)
    {
        var pointerDir = Path.Combine(rootPath, "pointer");
        var pointerBinDir = Path.Combine(pointerDir, "bin");
        var nodeModulesDir = Path.Combine(rootPath, "node_modules");
        var ridPackageDir = Path.Combine(nodeModulesDir, ridPackageName.Replace("/", Path.DirectorySeparatorChar.ToString()));
        var ridBinDir = Path.Combine(ridPackageDir, "bin");
        var cacheDir = Path.Combine(rootPath, "cache");

        Directory.CreateDirectory(pointerBinDir);
        Directory.CreateDirectory(ridBinDir);
        Directory.CreateDirectory(cacheDir);

        // Create pointer package.json
        var pointerPackageJson = new
        {
            name = "@microsoft/aspire-cli",
            version = pointerVersion
        };
        File.WriteAllText(
            Path.Combine(pointerDir, "package.json"),
            JsonSerializer.Serialize(pointerPackageJson, new JsonSerializerOptions { WriteIndented = true }));

        // Copy the real launcher script
        var realLauncherPath = Path.Combine(GetRepoRoot(), "eng", "clipack", "npm", "aspire.js");
        var launcherScript = Path.Combine(pointerBinDir, "aspire.js");
        File.Copy(realLauncherPath, launcherScript);

        // Create aspire-package-map.json
        var packageMap = new Dictionary<string, string> { [rid] = ridPackageName };
        File.WriteAllText(
            Path.Combine(pointerBinDir, "aspire-package-map.json"),
            JsonSerializer.Serialize(packageMap));

        // Create RID package.json
        var ridPackageJson = new
        {
            name = ridPackageName,
            version = ridPackageVersion
        };
        File.WriteAllText(
            Path.Combine(ridPackageDir, "package.json"),
            JsonSerializer.Serialize(ridPackageJson, new JsonSerializerOptions { WriteIndented = true }));

        var binaryName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "aspire.exe" : "aspire";
        var binaryPath = Path.Combine(ridBinDir, binaryName);
        CreateFakeNativeBinary(binaryPath);

        var probeScript = Path.Combine(rootPath, "probe-native.js");
        File.WriteAllText(probeScript, """
            const output = {
                ASPIRE_NPM_PACKAGE: process.env.ASPIRE_NPM_PACKAGE,
                ASPIRE_NPM_PACKAGE_VERSION: process.env.ASPIRE_NPM_PACKAGE_VERSION,
                ASPIRE_NPM_PACKAGE_RID: process.env.ASPIRE_NPM_PACKAGE_RID,
                args: process.argv.slice(2)
            };
            console.log(JSON.stringify(output));
            """);

        return new FakeNpmLayout(launcherScript, cacheDir, probeScript, Path.Combine(ridPackageDir, "package.json"));
    }

    private static void CreateFakeNativeBinary(string binaryPath)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            File.Copy(GetNodeExecutablePath(), binaryPath);
            return;
        }

        // Keep the fake native binary as a small script on Unix. Some macOS CI
        // Node builds abort when copied and executed from a different path/name,
        // which obscures what this fixture actually needs to validate: launcher
        // copy/cache behavior plus environment and argv forwarding.
        File.WriteAllText(binaryPath, """
            #!/bin/sh
            exec node "$@"
            """);

        var result = RunProcess(new ProcessStartInfo
        {
            FileName = "chmod",
            ArgumentList = { "+x", binaryPath },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        });

        Assert.Equal(0, result.ExitCode);
    }

    private static ProcessResult RunLauncher(string launcherScript, string cacheDir, string[] args, string? requiredScript = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "node",
            WorkingDirectory = Path.GetDirectoryName(launcherScript),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        if (requiredScript is not null)
        {
            psi.ArgumentList.Add("--require");
            psi.ArgumentList.Add(requiredScript);
        }
        psi.ArgumentList.Add(launcherScript);
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }
        psi.Environment["ASPIRE_NPM_CACHE_DIR"] = cacheDir;

        return RunProcess(psi);
    }

    private static ProcessResult RunNodeScript(string scriptPath)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "node",
            WorkingDirectory = Path.GetDirectoryName(scriptPath),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        psi.ArgumentList.Add(scriptPath);

        return RunProcess(psi);
    }

    private static string GetNodeExecutablePath()
    {
        var psi = new ProcessStartInfo
        {
            FileName = "node",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        psi.ArgumentList.Add("-p");
        psi.ArgumentList.Add("process.execPath");

        var result = RunProcess(psi);

        Assert.True(result.ExitCode == 0, result.StdErr);
        return result.StdOut.Trim();
    }

    private static ProcessResult RunProcess(ProcessStartInfo psi)
    {
        using var process = Process.Start(psi)!;
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        using var timeout = AsyncTestHelpers.CreateDefaultTimeoutTokenSource(TestConstants.LongTimeoutDuration);
        try
        {
            process.WaitForExitAsync(timeout.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            throw new TimeoutException($"Process '{psi.FileName}' did not exit within the timeout.");
        }

        var stdout = stdoutTask.GetAwaiter().GetResult();
        var stderr = stderrTask.GetAwaiter().GetResult();

        return new ProcessResult(process.ExitCode, stdout, stderr);
    }

    private static string GetRepoRoot()
    {
        var current = Directory.GetCurrentDirectory();
        while (current is not null && !File.Exists(Path.Combine(current, "Aspire.slnx")))
        {
            current = Directory.GetParent(current)?.FullName;
        }
        return current ?? throw new InvalidOperationException("Could not find repository root");
    }

    private sealed record FakeNpmLayout(string LauncherScript, string CacheDir, string ProbeScript, string RidPackageJsonPath);
    private sealed record RidProbeCase(string Platform, string Arch, bool Musl);
    private sealed record ProcessResult(int ExitCode, string StdOut, string StdErr);
}
