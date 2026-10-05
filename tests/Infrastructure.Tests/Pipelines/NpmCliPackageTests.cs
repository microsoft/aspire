// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using System.Xml.Linq;
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
        AssertJsonEqual(CreateExpectedPointerPackageJson(), packageJson);

        var packageMap = ReadJsonObject(Path.Combine(package.PointerPackageRoot, "bin", "aspire-package-map.json"));
        AssertJsonEqual(
            new JsonObject(s_supportedRids.Select(rid =>
                KeyValuePair.Create<string, JsonNode?>(rid.Rid, $"{PackageName}-{rid.Rid}"))),
            packageMap);

        var readme = await File.ReadAllTextAsync(Path.Combine(package.PointerPackageRoot, "README.md"));
        Assert.Equal(
            await RenderTemplateAsync(
                "eng/scripts/pack-cli-npm-package.pointer.README.md",
                ("PACKAGE_NAME", PackageName),
                ("VERSION", PackageVersion)),
            readme);
    }

    [Fact]
    public async Task PointerPackageReadmeSupportedPlatformTextMatchesSupportedRidMatrix()
    {
        var readme = await RenderTemplateAsync(
            "eng/scripts/pack-cli-npm-package.pointer.README.md",
            ("PACKAGE_NAME", PackageName),
            ("VERSION", PackageVersion));
        var supportedPlatformText = GetExpectedSupportedPlatformText();

        Assert.Contains($"Supported platforms: {supportedPlatformText}.", readme);
        Assert.Contains($"The npm package currently ships native binaries for {supportedPlatformText}. Other platforms are not supported by this package.", readme);
    }

    [Theory]
    [MemberData(nameof(GetSupportedRidData))]
    [RequiresTools(["pwsh", "npm"])]
    public async Task PackScriptGeneratesRidPackageMetadataAndReadme(RidPackageExpectation expectation)
    {
        var package = await PackCliNpmPackageAsync(expectation.Rid);

        var packageJson = ReadJsonObject(Path.Combine(package.RidPackageRoot, "package.json"));
        AssertJsonEqual(CreateExpectedRidPackageJson(expectation), packageJson);

        Assert.True(File.Exists(Path.Combine(package.RidPackageRoot, "bin", expectation.BinaryName)));

        var readme = await File.ReadAllTextAsync(Path.Combine(package.RidPackageRoot, "README.md"));
        Assert.Equal(
            await RenderTemplateAsync(
                "eng/scripts/pack-cli-npm-package.rid.README.md",
                ("RID_PACKAGE_NAME", $"{PackageName}-{expectation.Rid}"),
                ("RID", expectation.Rid),
                ("PACKAGE_NAME", PackageName)),
            readme);
    }

    [Fact]
    public void NpmInstallValidationArtifactsAreWiredFromBuildJobsIntoReleasePreparation()
    {
        var commonVariables = AzurePipelinesYaml.Load("eng/pipelines/common-variables.yml");
        var artifactVariables = new Dictionary<string, string?>
        {
            ["NPM_VALIDATION_SUMMARY_WIN_X64_ARTIFACT"] = "npm-validation-summary-win-x64",
            ["NPM_VALIDATION_SUMMARY_LINUX_X64_ARTIFACT"] = "npm-validation-summary-linux-x64",
            ["NPM_VALIDATION_SUMMARY_OSX_ARTIFACT"] = "npm-validation-summary-osx"
        };
        foreach (var (name, value) in artifactVariables)
        {
            Assert.Equal(value, AzurePipelinesYaml.Scalar(AzurePipelinesYaml.Variable(commonVariables, name), "value"));
        }

        var buildPipeline = AzurePipelinesYaml.Load("eng/pipelines/azure-pipelines.yml");
        var prepareInstallers = AzurePipelinesYaml.Stage(buildPipeline, "prepare_installers");
        var buildJobs = new Dictionary<string, (string Rid, string ArtifactVariable)>
        {
            ["NpmInstall_Windows_x64"] = ("win-x64", "$(NPM_VALIDATION_SUMMARY_WIN_X64_ARTIFACT)"),
            ["NpmInstall_Linux_x64"] = ("linux-x64", "$(NPM_VALIDATION_SUMMARY_LINUX_X64_ARTIFACT)"),
            ["NpmInstall_macOS"] = ("$(NpmValidationRid)", "$(NPM_VALIDATION_SUMMARY_OSX_ARTIFACT)")
        };

        foreach (var (jobName, expectation) in buildJobs)
        {
            var job = AzurePipelinesYaml.Job(prepareInstallers, jobName);
            var templateStep = Assert.Single(
                AzurePipelinesYaml.Steps(job),
                step => AzurePipelinesYaml.Scalar(step, "template") == "/eng/pipelines/templates/npm-cli-install-validation-steps.yml@self");
            var parameters = AzurePipelinesYaml.Mapping(templateStep, "parameters");
            Assert.Equal(expectation.Rid, AzurePipelinesYaml.Scalar(parameters, "rid"));
            Assert.Equal(expectation.ArtifactVariable, AzurePipelinesYaml.Scalar(parameters, "validationSummaryArtifactName"));
        }

        var releasePipeline = AzurePipelinesYaml.Load("eng/pipelines/release-publish-nuget.yml");
        var prepareJob = AzurePipelinesYaml.Job(AzurePipelinesYaml.Stage(releasePipeline, "PrepareArtifacts"), "PrepareJob");
        var downloadSteps = AzurePipelinesYaml.StepsRecursively(prepareJob)
            .Where(step => AzurePipelinesYaml.Scalar(step, "task") == "DownloadBuildArtifacts@0")
            .ToArray();
        Assert.Equal(3, downloadSteps.Length);
        Assert.Equal(
            artifactVariables.Keys.Select(name => $"$({name})").Order(StringComparer.Ordinal),
            downloadSteps
                .Select(step => AzurePipelinesYaml.Scalar(AzurePipelinesYaml.Mapping(step, "inputs"), "artifactName"))
                .Order(StringComparer.Ordinal));
        Assert.All(downloadSteps, step =>
        {
            var inputs = AzurePipelinesYaml.Mapping(step, "inputs");
            Assert.Equal("$(SourceBuildPipeline)", AzurePipelinesYaml.Scalar(inputs, "pipeline"));
            Assert.Equal("$(SourceBuildId)", AzurePipelinesYaml.Scalar(inputs, "buildId"));
            Assert.Equal("$(Pipeline.Workspace)/aspire-build", AzurePipelinesYaml.Scalar(inputs, "downloadPath"));
            Assert.Equal("true", AzurePipelinesYaml.Scalar(inputs, "checkDownloadedFiles"));
        });
    }

    [Fact]
    public async Task PolyglotTypeScriptToolchainUsesInternalNpmRegistry()
    {
        var dockerfile = await ReadRepoFileAsync(".github/workflows/polyglot-validation/Dockerfile.typescript");

        Assert.Contains("ARG NPM_REGISTRY=https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-public-npm/npm/registry/", dockerfile);
        Assert.Contains("npm install --global --force --registry \"${NPM_REGISTRY}\"", dockerfile);
        Assert.Contains("pnpm@10.0.0", dockerfile);
        Assert.Contains("@yarnpkg/cli-dist@4.14.1", dockerfile);
        Assert.DoesNotContain("corepack prepare", dockerfile);
    }

    [Fact]
    public async Task RunTestsInstallsAzureFunctionsCoreToolsFromPinnedGitHubRelease()
    {
        var workflow = await ReadRepoFileAsync(".github/workflows/run-tests.yml");

        Assert.DoesNotContain("npm i -g azure-functions-core-tools@4", workflow);
        Assert.Contains("core_tools_version='4.12.1'", workflow);
        Assert.Contains("https://github.com/Azure/azure-functions-core-tools/releases/download/${core_tools_version}/Azure.Functions.Cli.linux-x64.${core_tools_version}.zip", workflow);
        Assert.Contains("sha256sum --check -", workflow);
        Assert.Contains("func --version", workflow);
    }

    [Fact]
    public async Task AspireCliUsesMicrosoftCertificate()
    {
        var signingProps = XDocument.Parse(await ReadRepoFileAsync("eng/Signing.props"));

        AssertSigningRule(
            signingProps,
            "FileExtensionSignInfo",
            ".msi",
            "Microsoft400",
            collisionPriorityId: null,
            condition: "!@(FileExtensionSignInfo->AnyHaveMetadataValue('Identity', '.msi'))");
        AssertSigningRule(
            signingProps,
            "FileExtensionSignInfo",
            ".cat",
            "Microsoft400",
            collisionPriorityId: null,
            condition: null);
        AssertSigningRule(
            signingProps,
            "FileSignInfo",
            "aspire.exe",
            "Microsoft400",
            collisionPriorityId: null,
            condition: "$([System.OperatingSystem]::IsWindows())");
        AssertSigningRule(
            signingProps,
            "FileSignInfo",
            "aspire-managed.exe",
            "Microsoft400",
            collisionPriorityId: null,
            condition: "$([System.OperatingSystem]::IsWindows())");
        AssertSigningRule(
            signingProps,
            "FileSignInfo",
            "aspire",
            "Microsoft400",
            collisionPriorityId: null,
            condition: "$([System.OperatingSystem]::IsLinux())");
        AssertSigningRule(
            signingProps,
            "FileSignInfo",
            "aspire-managed",
            "Microsoft400",
            collisionPriorityId: null,
            condition: "$([System.OperatingSystem]::IsLinux())");
        AssertSigningRule(
            signingProps,
            "FileSignInfo",
            "get-aspire-cli.ps1",
            "Microsoft400",
            collisionPriorityId: null,
            condition: "$([System.OperatingSystem]::IsWindows())");
        AssertSigningRule(
            signingProps,
            "FileSignInfo",
            "manifest.cat",
            "Microsoft400",
            collisionPriorityId: null,
            condition: "$([System.OperatingSystem]::IsWindows())");
        AssertSigningRule(
            signingProps,
            "FileSignInfo",
            "aspire.js",
            "Microsoft400",
            collisionPriorityId: "AspireCliNpmPackage",
            condition: null);
    }

    [Fact]
    public async Task NpmSigningScopeCoversNestedTarballPayloads()
    {
        var signingProps = XDocument.Parse(await ReadRepoFileAsync("eng/Signing.props"));

        AssertSigningRule(signingProps, "FileExtensionSignInfo", ".tgz", "LinuxSign500180PGP", "AspireCliNpmPackage", condition: null);
        AssertSigningRule(signingProps, "FileSignInfo", "aspire.js", "Microsoft400", "AspireCliNpmPackage", condition: null);

        // The native npm packages are built from already-signed native archives.
        // The main Windows build should only produce the detached npm tarball
        // signature; it must still provide scoped rules for nested native
        // executables because Arcade resolves nested file certificates inside
        // the ItemsToSign collision scope.
        AssertSigningRule(signingProps, "FileSignInfo", "aspire.exe", "None", "AspireCliNpmPackage", condition: null);
        AssertSigningRule(signingProps, "FileSignInfo", "aspire", "None", "AspireCliNpmPackage", condition: null);
    }

    [Fact]
    public async Task ReleasePipelinePreflightsScheduledNpmPackagesBeforePublishing()
    {
        var releasePipeline = await ReadRepoFileAsync("eng/pipelines/release-publish-nuget.yml");

        var preflightIndex = releasePipeline.IndexOf("Verify npm Packages Are Not Already Published", System.StringComparison.Ordinal);
        var publishIndex = releasePipeline.IndexOf("template: MicroBuild.Publish.yml@MicroBuildTemplate", System.StringComparison.Ordinal);

        Assert.True(preflightIndex >= 0, "Expected an already-published npm package preflight.");
        Assert.True(publishIndex >= 0, "Expected npm MicroBuild publish template usage.");
        Assert.True(preflightIndex < publishIndex, "Already-published npm package preflight must run before MicroBuild publish.");
        Assert.Contains("SkipNpmRidPublish", releasePipeline);
        Assert.Contains("SkipNpmPointerPublish", releasePipeline);
        Assert.Contains("npm view $packageSpec version", releasePipeline);
        Assert.Contains("already exists on npm", releasePipeline);
        Assert.Contains("Set SkipNpmRidPublish=true", releasePipeline);
        Assert.Contains("Set SkipNpmPointerPublish=true", releasePipeline);
    }

    [Fact]
    public async Task ReleasePipelineGuardsNpmLatestDistTagAgainstServicingDowngrade()
    {
        var releasePipeline = await ReadRepoFileAsync("eng/pipelines/release-publish-nuget.yml");

        Assert.DoesNotContain("AllowNpmLatestDistTagMove", releasePipeline);
        Assert.Contains("npm view @microsoft/aspire-cli@latest version", releasePipeline);
        Assert.Contains("would move the npm latest dist-tag backward", releasePipeline);
        Assert.Contains("Set SkipNpmRidPublish=true and SkipNpmPointerPublish=true for older servicing releases", releasePipeline);
    }

    [Fact]
    public async Task ReleasePipelineUsesEffectiveNpmOwnersAndApproversFromSingleSource()
    {
        var commonVariables = await ReadRepoFileAsync("eng/pipelines/common-variables.yml");
        var releasePipeline = await ReadRepoFileAsync("eng/pipelines/release-publish-nuget.yml");

        Assert.DoesNotContain("NPM_PUBLISH_REQUIRED_OWNERS", commonVariables);
        Assert.Contains("NPM_PUBLISH_REQUIRED_OWNERS", releasePipeline);
        Assert.DoesNotContain("NPM_PUBLISH_REQUIRED_APPROVERS", commonVariables);
        Assert.DoesNotContain("NPM_PUBLISH_REQUIRED_APPROVERS", releasePipeline);
        Assert.DoesNotContain("requiredNpmApprovers", releasePipeline);
        Assert.Contains("default: 'joperezr'", releasePipeline);
        Assert.Contains("default: 'adamratzman'", releasePipeline);
        Assert.Contains("NpmPublishOwnersEffective", releasePipeline);
        Assert.Contains("NpmPublishApproversEffective", releasePipeline);
        Assert.Contains("owners: '$(NpmPublishOwnersEffective)'", releasePipeline);
        Assert.Contains("approvers: '$(NpmPublishApproversEffective)'", releasePipeline);
        Assert.DoesNotContain("$requiredNpmOwners = @('joperezr', 'ankj')", releasePipeline);
        Assert.DoesNotContain("owners: '${{ parameters.NpmPublishOwners }}'", releasePipeline);
        Assert.DoesNotContain("approvers: '${{ parameters.NpmPublishApprovers }}'", releasePipeline);
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

    private static string GetExpectedSupportedPlatformText()
    {
        var platformGroups = new[]
        {
            new PlatformDescription("win32", null, "Windows", null),
            new PlatformDescription("darwin", null, "macOS", null),
            new PlatformDescription("linux", "glibc", "Linux", "with glibc"),
            new PlatformDescription("linux", "musl", "Linux", "with musl/Alpine")
        };

        var describedRids = new HashSet<string>(StringComparer.Ordinal);
        var descriptions = new List<string>();

        foreach (var group in platformGroups)
        {
            var matchingRids = s_supportedRids
                .Where(rid => rid.Os.Contains(group.Os, StringComparer.Ordinal) && HasLibc(rid, group.Libc))
                .ToArray();

            if (matchingRids.Length == 0)
            {
                continue;
            }

            foreach (var rid in matchingRids)
            {
                describedRids.Add(rid.Rid);
            }

            var cpuText = string.Join(
                '/',
                matchingRids
                    .SelectMany(rid => rid.Cpu)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(GetCpuDisplayOrder)
                    .Select(FormatCpuName));
            descriptions.Add(group.LibcDescription is null ? $"{group.Name} {cpuText}" : $"{group.Name} {cpuText} {group.LibcDescription}");
        }

        Assert.Equal(
            s_supportedRids.Select(rid => rid.Rid).Order(StringComparer.Ordinal),
            describedRids.Order(StringComparer.Ordinal));

        return JoinDescriptions(descriptions);
    }

    private static bool HasLibc(RidPackageExpectation rid, string? libc)
    {
        return libc is null
            ? rid.Libc is null
            : rid.Libc?.Contains(libc, StringComparer.Ordinal) == true;
    }

    private static string FormatCpuName(string cpu)
    {
        return cpu switch
        {
            "arm64" => "Arm64",
            _ => cpu
        };
    }

    private static int GetCpuDisplayOrder(string cpu)
    {
        return cpu switch
        {
            "x64" => 0,
            "arm64" => 1,
            _ => 2
        };
    }

    private static string JoinDescriptions(IReadOnlyList<string> descriptions)
    {
        return descriptions.Count switch
        {
            0 => throw new InvalidOperationException("Expected at least one supported npm CLI platform."),
            1 => descriptions[0],
            2 => $"{descriptions[0]} and {descriptions[1]}",
            _ => $"{string.Join(", ", descriptions.Take(descriptions.Count - 1))}, and {descriptions[^1]}"
        };
    }

    private static JsonObject ReadJsonObject(string path)
    {
        var json = File.ReadAllText(path);
        return JsonNode.Parse(json)?.AsObject()
            ?? throw new InvalidOperationException($"Failed to parse JSON object from {path}");
    }

    private static JsonObject CreateExpectedPointerPackageJson()
    {
        var optionalDependencies = new JsonObject();
        foreach (var rid in s_supportedRids)
        {
            optionalDependencies[$"{PackageName}-{rid.Rid}"] = PackageVersion;
        }

        return new JsonObject
        {
            ["name"] = PackageName,
            ["version"] = PackageVersion,
            ["description"] = "The Aspire CLI lets you build, run, manage, and deploy distributed applications in a terminal.",
            ["license"] = "MIT",
            ["keywords"] = new JsonArray(
                "aspire", "typescript", "dotnet", "apphost", "polyglot", "distributed-applications",
                "code-first", "orchestration", "observability", "opentelemetry", "local-development"),
            ["homepage"] = "https://aspire.dev",
            ["repository"] = new JsonObject
            {
                ["type"] = "git",
                ["url"] = "git+https://github.com/microsoft/aspire.git"
            },
            ["bugs"] = new JsonObject
            {
                ["url"] = "https://github.com/microsoft/aspire/issues"
            },
            ["bin"] = new JsonObject
            {
                ["aspire"] = "bin/aspire.js"
            },
            ["scripts"] = new JsonObject
            {
                ["postinstall"] = "node bin/aspire.js --npm-postinstall-check"
            },
            ["engines"] = new JsonObject
            {
                ["node"] = ">=20"
            },
            ["optionalDependencies"] = optionalDependencies,
            ["files"] = new JsonArray("bin", "README.md")
        };
    }

    private static JsonObject CreateExpectedRidPackageJson(RidPackageExpectation expectation)
    {
        var packageJson = new JsonObject
        {
            ["name"] = $"{PackageName}-{expectation.Rid}",
            ["version"] = PackageVersion,
            ["description"] = $"Native Aspire CLI binary for {expectation.Rid}.",
            ["license"] = "MIT",
            ["repository"] = new JsonObject
            {
                ["type"] = "git",
                ["url"] = "git+https://github.com/microsoft/aspire.git"
            },
            ["bugs"] = new JsonObject
            {
                ["url"] = "https://github.com/microsoft/aspire/issues"
            },
            ["os"] = new JsonArray(expectation.Os.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()),
            ["cpu"] = new JsonArray(expectation.Cpu.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()),
            ["files"] = new JsonArray("bin", "README.md")
        };

        if (expectation.Libc is not null)
        {
            packageJson["libc"] = new JsonArray(expectation.Libc.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
        }

        return packageJson;
    }

    private static void AssertJsonEqual(JsonNode expected, JsonNode actual)
    {
        Assert.True(
            JsonNode.DeepEquals(expected, actual),
            $"Expected JSON:{Environment.NewLine}{expected.ToJsonString()}{Environment.NewLine}Actual JSON:{Environment.NewLine}{actual.ToJsonString()}");
    }

    private static void AssertSigningRule(
        XDocument document,
        string elementName,
        string include,
        string certificateName,
        string? collisionPriorityId,
        string? condition)
    {
        var matchingRules = document
            .Descendants(elementName)
            .Where(element =>
                (string?)element.Attribute("CollisionPriorityId") == collisionPriorityId &&
                ((string?)element.Attribute("Include") == include || (string?)element.Attribute("Update") == include) &&
                (string?)element.Attribute("CertificateName") == certificateName &&
                (string?)element.Attribute("Condition") == condition)
            .ToArray();

        Assert.True(
            matchingRules.Length == 1,
            $"Expected exactly one {elementName} for '{include}' using '{certificateName}', collision scope '{collisionPriorityId}', and condition '{condition}', but found {matchingRules.Length}.");
    }

    public sealed record RidPackageExpectation(string Rid, string BinaryName, string[] Os, string[] Cpu, string[]? Libc);

    private sealed record PlatformDescription(string Os, string? Libc, string Name, string? LibcDescription);

    private sealed record PackedNpmPackage(string RidPackageRoot, string PointerPackageRoot);
}