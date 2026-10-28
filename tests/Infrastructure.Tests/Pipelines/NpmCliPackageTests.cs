// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using Aspire.TestUtilities;
using Xunit;

namespace Infrastructure.Tests;

public sealed class NpmCliPackageTests : IDisposable
{
    private const string PackageName = "@microsoft/aspire-cli";
    private const string PackageVersion = "13.4.0-test.1";

    private static readonly RidPackageExpectation[] s_supportedRids =
    [
        new("win-x64", "aspire.exe", ["win32"], ["x64"], null),
        new("win-arm64", "aspire.exe", ["win32"], ["arm64"], null),
        new("linux-x64", "aspire", ["linux"], ["x64"], ["glibc"]),
        new("linux-arm64", "aspire", ["linux"], ["arm64"], ["glibc"]),
        new("linux-musl-x64", "aspire", ["linux"], ["x64"], ["musl"]),
        new("osx-x64", "aspire", ["darwin"], ["x64"], null),
        new("osx-arm64", "aspire", ["darwin"], ["arm64"], null)
    ];

    private readonly TemporaryWorkspace _workspace;
    private readonly ITestOutputHelper _output;
    private readonly string _repoRoot = RepoRoot.Path;
    private readonly string _packScriptPath;

    public NpmCliPackageTests(ITestOutputHelper output)
    {
        _output = output;
        _workspace = TemporaryWorkspace.Create(output);
        _packScriptPath = Path.Combine(_repoRoot, "eng", "scripts", "pack-cli-npm-package.ps1");
    }

    public void Dispose() => _workspace.Dispose();

    [Fact]
    [RequiresTools(["node"])]
    public async Task LauncherPostinstallCheckSucceedsWhenNativePackageIsInstalled()
    {
        var pointerPackageRoot = await CreateFakeNpmInstallAsync(includeRidPackages: true);

        using var cmd = new NodeCommand(_output)
            .WithTimeout(TimeSpan.FromSeconds(30));

        var result = await cmd.ExecuteScriptAsync(
            Path.Combine(pointerPackageRoot, "bin", "aspire.js"),
            "--npm-postinstall-check");

        result.EnsureSuccessful();
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task LauncherPostinstallCheckExplainsDisabledOptionalDependenciesOnSupportedPlatform()
    {
        var pointerPackageRoot = await CreateFakeNpmInstallAsync(includeRidPackages: false);
        var testScriptPath = Path.Combine(_workspace.Path, $"{Path.GetRandomFileName()}.js");
        await File.WriteAllTextAsync(
            testScriptPath,
            """
            const launcher = require(process.argv[2]);

            try {
              launcher.__testing.runNpmPostinstallCheck(
                { name: process.argv[3], version: process.argv[4] },
                () => process.argv[5]);
              console.error('Expected postinstall check to fail.');
              process.exit(1);
            } catch (error) {
              console.log(error.message);
            }
            """);

        using var cmd = new NodeCommand(_output)
            .WithTimeout(TimeSpan.FromSeconds(30));

        var result = await cmd.ExecuteScriptAsync(
            testScriptPath,
            Path.Combine(pointerPackageRoot, "bin", "aspire.js"),
            PackageName,
            PackageVersion,
            "linux-x64");

        result.EnsureSuccessful();

        Assert.Contains("without disabling npm optional dependencies", result.Output);
        Assert.Contains("--omit=optional", result.Output);
        Assert.Contains("--no-optional", result.Output);
        Assert.Contains("npm_config_optional=false", result.Output);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task LauncherPostinstallCheckSkipsUnsupportedPlatform()
    {
        var pointerPackageRoot = await CreateFakeNpmInstallAsync(includeRidPackages: false);
        var testScriptPath = Path.Combine(_workspace.Path, $"{Path.GetRandomFileName()}.js");
        await File.WriteAllTextAsync(
            testScriptPath,
            """
            const launcher = require(process.argv[2]);

            launcher.__testing.runNpmPostinstallCheck(
              { name: process.argv[3], version: process.argv[4] },
              () => launcher.__testing.detectRid('linux', 'arm64', true));
            """);

        using var cmd = new NodeCommand(_output)
            .WithTimeout(TimeSpan.FromSeconds(30));

        var result = await cmd.ExecuteScriptAsync(
            testScriptPath,
            Path.Combine(pointerPackageRoot, "bin", "aspire.js"),
            PackageName,
            PackageVersion);

        result.EnsureSuccessful();
    }

    [Fact]
    [RequiresTools(["pwsh", "npm"])]
    public async Task PackScriptGeneratesPointerPackageMetadataMapAndReadme()
    {
        var package = await PackCliNpmPackageAsync("linux-musl-x64");

        var packageJson = ReadJsonObject(Path.Combine(package.PointerPackageRoot, "package.json"));

        Assert.Equal(PackageName, GetString(packageJson, "name"));
        Assert.Equal(PackageVersion, GetString(packageJson, "version"));
        Assert.Equal("The Aspire CLI lets you build, run, manage, and deploy distributed applications in a terminal.", GetString(packageJson, "description"));
        Assert.Equal("https://aspire.dev", GetString(packageJson, "homepage"));
        Assert.Equal(
            ["aspire", "typescript", "dotnet", "apphost", "polyglot", "distributed-applications", "code-first", "orchestration", "observability", "opentelemetry", "local-development"],
            GetStringArray(packageJson["keywords"]));
        Assert.Equal(">=20", GetString(GetObject(packageJson, "engines"), "node"));
        Assert.Equal("node bin/aspire.js --npm-postinstall-check", GetString(GetObject(packageJson, "scripts"), "postinstall"));
        Assert.Equal(
            s_supportedRids.ToDictionary(rid => $"{PackageName}-{rid.Rid}", _ => PackageVersion, StringComparer.Ordinal),
            GetStringMap(GetObject(packageJson, "optionalDependencies")));
        Assert.Equal(["bin", "README.md"], GetStringArray(packageJson["files"]));

        var packageMap = ReadJsonObject(Path.Combine(package.PointerPackageRoot, "bin", "aspire-package-map.json"));
        Assert.Equal(
            s_supportedRids.ToDictionary(rid => rid.Rid, rid => $"{PackageName}-{rid.Rid}", StringComparer.Ordinal),
            GetStringMap(packageMap));

        var readme = await File.ReadAllTextAsync(Path.Combine(package.PointerPackageRoot, "README.md"));
        Assert.Equal(
            await RenderTemplateAsync(
                "eng/scripts/pack-cli-npm-package.pointer.README.md",
                ("PACKAGE_NAME", PackageName),
                ("VERSION", PackageVersion)),
            readme);
        Assert.Contains("Use it to create, run, publish, and deploy Aspire AppHosts from a terminal.", readme);
        Assert.Contains("This package requires Node.js 20 or later.", readme);
        Assert.Contains("Supported platforms:", readme);
        Assert.Contains($"npm install -g {PackageName}", readme);
        Assert.Contains($"npm install -g {PackageName}@latest", readme);
        Assert.Contains("The native platform packages are installed through npm optional dependencies.", readme);
        Assert.Contains("installation fails because the launcher cannot find the native CLI binary", readme);
        Assert.Contains("If you run `aspire update --self` from an npm install, the CLI points you back to this npm update command.", readme);
        Assert.Contains($"npm uninstall -g {PackageName}", readme);
        Assert.Contains("## Troubleshooting", readme);
        Assert.Contains("Optional dependencies disabled", readme);
        Assert.Contains("If installation fails or the launcher says the native package was not installed", readme);
        Assert.Contains("the `npm_config_optional=false` environment variable", readme);
        Assert.Contains("New app:", readme);
        Assert.Contains("Existing repo:", readme);
        Assert.Contains("Dashboard only:", readme);
        Assert.Contains("PATH cannot find `aspire`", readme);
        Assert.Contains("Supported platforms and architectures", readme);
        Assert.Contains("## Useful commands", readme);
        Assert.Contains("`aspire deploy` deploys an AppHost to its supported deployment targets.", readme);
        Assert.Contains("Aspire CLI command reference", readme);
        Assert.Contains("TypeScript AppHost (`apphost.mts`)", readme);
        Assert.Contains("import { createBuilder } from './.aspire/modules/aspire.mjs';", readme);
        Assert.Contains("aspire dashboard run", readme);
        Assert.Contains("Browse Aspire samples", readme);
        Assert.Contains("## Release notes", readme);
        Assert.Contains($"This package contains Aspire CLI version `{PackageVersion}`.", readme);
        Assert.Contains("[Aspire releases](https://github.com/microsoft/aspire/releases)", readme);
        Assert.DoesNotContain("https://github.com/microsoft/aspire/releases/tag/v", readme);
        Assert.DoesNotContain("apphost.ts", readme);
        Assert.DoesNotContain("./.aspire/modules/aspire.js", readme);
        Assert.DoesNotContain("__PACKAGE_NAME__", readme);
        Assert.DoesNotContain("__VERSION__", readme);
        // The C# AppHost example was intentionally removed; the npm README is TypeScript-only.
        Assert.DoesNotContain("apphost.cs", readme);
        Assert.DoesNotContain("```csharp", readme);
    }

    [Theory]
    [MemberData(nameof(GetSupportedRidData))]
    [RequiresTools(["pwsh", "npm"])]
    public async Task PackScriptGeneratesRidPackageMetadataAndReadme(RidPackageExpectation expectation)
    {
        var package = await PackCliNpmPackageAsync(expectation.Rid);

        var packageJson = ReadJsonObject(Path.Combine(package.RidPackageRoot, "package.json"));

        Assert.Equal($"{PackageName}-{expectation.Rid}", GetString(packageJson, "name"));
        Assert.Equal(PackageVersion, GetString(packageJson, "version"));
        Assert.Equal($"Native Aspire CLI binary for {expectation.Rid}.", GetString(packageJson, "description"));
        Assert.Equal(expectation.Os, GetStringArray(packageJson["os"]));
        Assert.Equal(expectation.Cpu, GetStringArray(packageJson["cpu"]));
        Assert.Equal(["bin", "README.md"], GetStringArray(packageJson["files"]));

        if (expectation.Libc is null)
        {
            Assert.False(packageJson.ContainsKey("libc"));
        }
        else
        {
            Assert.Equal(expectation.Libc, GetStringArray(packageJson["libc"]));
        }

        Assert.True(File.Exists(Path.Combine(package.RidPackageRoot, "bin", expectation.BinaryName)));

        var readme = await File.ReadAllTextAsync(Path.Combine(package.RidPackageRoot, "README.md"));
        Assert.Equal(
            await RenderTemplateAsync(
                "eng/scripts/pack-cli-npm-package.rid.README.md",
                ("RID_PACKAGE_NAME", $"{PackageName}-{expectation.Rid}"),
                ("RID", expectation.Rid),
                ("PACKAGE_NAME", PackageName)),
            readme);
        Assert.Contains($"Native Aspire CLI binary for `{expectation.Rid}`.", readme);
        Assert.Contains($"This package is installed as an optional dependency of `{PackageName}`.", readme);
        Assert.DoesNotContain("__RID__", readme);
        Assert.DoesNotContain("__PACKAGE_NAME__", readme);
    }

    private Task<string> ReadRepoFileAsync(string relativePath)
        => File.ReadAllTextAsync(Path.Combine(_repoRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));

    private async Task<string> CreateFakeNpmInstallAsync(bool includeRidPackages)
    {
        var testRoot = Path.Combine(_workspace.Path, Path.GetRandomFileName());
        var nodeModulesRoot = Path.Combine(testRoot, "node_modules");
        var pointerPackageRoot = Path.Combine(nodeModulesRoot, "@microsoft", "aspire-cli");
        var pointerBinRoot = Path.Combine(pointerPackageRoot, "bin");
        Directory.CreateDirectory(pointerBinRoot);

        File.Copy(
            Path.Combine(_repoRoot, "eng", "clipack", "npm", "aspire.js"),
            Path.Combine(pointerBinRoot, "aspire.js"));

        await File.WriteAllTextAsync(
            Path.Combine(pointerPackageRoot, "package.json"),
            $$"""
            {
              "name": "{{PackageName}}",
              "version": "{{PackageVersion}}"
            }
            """);

        var packageMap = new JsonObject();
        foreach (var rid in s_supportedRids)
        {
            packageMap[rid.Rid] = $"{PackageName}-{rid.Rid}";
        }

        await File.WriteAllTextAsync(Path.Combine(pointerBinRoot, "aspire-package-map.json"), packageMap.ToJsonString());

        if (includeRidPackages)
        {
            foreach (var rid in s_supportedRids)
            {
                var ridPackageRoot = Path.Combine(nodeModulesRoot, "@microsoft", $"aspire-cli-{rid.Rid}");
                var ridBinRoot = Path.Combine(ridPackageRoot, "bin");
                Directory.CreateDirectory(ridBinRoot);

                await File.WriteAllTextAsync(
                    Path.Combine(ridPackageRoot, "package.json"),
                    $$"""
                    {
                      "name": "{{PackageName}}-{{rid.Rid}}",
                      "version": "{{PackageVersion}}"
                    }
                    """);

                await File.WriteAllTextAsync(Path.Combine(ridBinRoot, "aspire"), "native binary stub");
                await File.WriteAllTextAsync(Path.Combine(ridBinRoot, "aspire.exe"), "native binary stub");
            }
        }

        return pointerPackageRoot;
    }

    public static TheoryData<RidPackageExpectation> GetSupportedRidData()
    {
        var data = new TheoryData<RidPackageExpectation>();
        foreach (var rid in s_supportedRids)
        {
            data.Add(rid);
        }

        return data;
    }

    private async Task<PackedNpmPackage> PackCliNpmPackageAsync(string rid)
    {
        var testRoot = Path.Combine(_workspace.Path, Path.GetRandomFileName());
        var stagingRoot = Path.Combine(testRoot, "staging");
        var outputPath = Path.Combine(testRoot, "output");
        var nativeBinaryPath = Path.Combine(testRoot, "native-aspire-stub");

        Directory.CreateDirectory(testRoot);
        await File.WriteAllTextAsync(nativeBinaryPath, "native binary stub");

        using var cmd = new PowerShellCommand(_packScriptPath, _output)
            .WithTimeout(TimeSpan.FromMinutes(2));

        var result = await cmd.ExecuteAsync(
            "-Rid", rid,
            "-Version", PackageVersion,
            "-NativeBinaryPath", $"\"{nativeBinaryPath}\"",
            "-OutputPath", $"\"{outputPath}\"",
            "-StagingRoot", $"\"{stagingRoot}\"",
            "-PackageName", PackageName);

        result.EnsureSuccessful();

        Assert.Equal(2, Directory.GetFiles(outputPath, "*.tgz").Length);

        return new PackedNpmPackage(
            Path.Combine(stagingRoot, "rid"),
            Path.Combine(stagingRoot, "pointer"));
    }

    private async Task<string> RenderTemplateAsync(string templateRelativePath, params (string Name, string Value)[] values)
    {
        var template = await ReadRepoFileAsync(templateRelativePath);

        foreach (var (name, value) in values)
        {
            template = template.Replace($"__{name}__", value, System.StringComparison.Ordinal);
        }

        return template;
    }

    private static JsonObject ReadJsonObject(string path)
    {
        var json = File.ReadAllText(path);
        return JsonNode.Parse(json)?.AsObject()
            ?? throw new InvalidOperationException($"Failed to parse JSON object from {path}");
    }

    private static JsonObject GetObject(JsonObject jsonObject, string propertyName)
    {
        return jsonObject[propertyName]?.AsObject()
            ?? throw new InvalidOperationException($"Missing JSON object property '{propertyName}'.");
    }

    private static string GetString(JsonObject jsonObject, string propertyName)
    {
        return jsonObject[propertyName]?.GetValue<string>()
            ?? throw new InvalidOperationException($"Missing JSON string property '{propertyName}'.");
    }

    private static string[] GetStringArray(JsonNode? jsonNode)
    {
        return jsonNode?.AsArray().Select(value => value?.GetValue<string>() ?? throw new InvalidOperationException("JSON array contains a null value.")).ToArray()
            ?? throw new InvalidOperationException("Missing JSON string array.");
    }

    private static Dictionary<string, string> GetStringMap(JsonObject jsonObject)
    {
        return jsonObject.ToDictionary(
            property => property.Key,
            property => property.Value?.GetValue<string>() ?? throw new InvalidOperationException($"JSON property '{property.Key}' is not a string."),
            StringComparer.Ordinal);
    }

    public sealed record RidPackageExpectation(string Rid, string BinaryName, string[] Os, string[] Cpu, string[]? Libc);

    private sealed record PackedNpmPackage(string RidPackageRoot, string PointerPackageRoot);
}