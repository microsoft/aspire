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

    private readonly TestTempDirectory _tempDirectory = new();
    private readonly ITestOutputHelper _output;
    private readonly string _repoRoot = RepoRoot.Path;
    private readonly string _packScriptPath;

    public NpmCliPackageTests(ITestOutputHelper output)
    {
        _output = output;
        _packScriptPath = Path.Combine(_repoRoot, "eng", "scripts", "pack-cli-npm-package.ps1");
    }

    public void Dispose() => _tempDirectory.Dispose();

    [Fact]
    public async Task LauncherDetectsMuslArm64AndThrowsUnsupported()
    {
        var launcher = await ReadRepoFileAsync("eng/clipack/npm/aspire.js");

        // libc-mismatched binaries crash at exec with cryptic dynamic-linker
        // errors. Previously the launcher only checked musl for x64 and silently
        // fell through to the glibc-linked `linux-arm64` binary on Alpine arm64.
        // Assert the musl-arm64 case throws a friendly "Unsupported platform"
        // error rather than silently resolving to a binary that will crash.
        Assert.Contains("if (arch === 'arm64' && musl)", launcher);
        Assert.Contains("Unsupported platform: ${platform} musl ${arch}", launcher);
    }

    [Fact]
    public async Task LauncherRejectsNonRegularCachedBinary()
    {
        var launcher = await ReadRepoFileAsync("eng/clipack/npm/aspire.js");

        // The native binary is copied from the (signed) node_modules source into
        // a writable cache before exec. `needsCopy` previously trusted any cache
        // entry that matched the source size/mtime, so a same-user process could
        // swap the cache entry for a symlink to attacker content and the launcher
        // would exec it. Verify the staleness check lstat's the target and only
        // trusts regular files, forcing a fresh copy otherwise.
        Assert.Contains("const target = fs.lstatSync(targetPath);", launcher);
        Assert.Contains("if (!target.isFile()) {", launcher);
    }

    [Fact]
    public async Task LauncherForwardsTerminatingSignalsToChild()
    {
        var launcher = await ReadRepoFileAsync("eng/clipack/npm/aspire.js");

        // Programmatic `kill <wrapper-pid>` was orphaning the native CLI because
        // the launcher only handled child exit and never forwarded inbound
        // signals to the child. Especially bad for `aspire run` which keeps an
        // AppHost alive. Verify termination signals are wired up and that we use
        // `once` so a second signal can still kill the wrapper if the child
        // ignores the first.
        Assert.Contains("process.once(signal", launcher);
        Assert.Contains("child.kill(signal)", launcher);
    }

    [Fact]
    public async Task LauncherUsesPlatformSpecificSignalListToAvoidWindowsCrash()
    {
        var launcher = await ReadRepoFileAsync("eng/clipack/npm/aspire.js");

        // SIGQUIT is POSIX-only: on Windows `process.once('SIGQUIT', ...)` throws
        // `uv_signal_start EINVAL`, which (because the child is already spawned)
        // would crash the launcher and orphan the running CLI on every Windows
        // invocation. Verify the signal list is built per platform so SIGQUIT is
        // never registered on Windows, and that each registration is guarded so an
        // unexpected platform/libuv mismatch can never crash the launcher.
        Assert.Contains("process.platform === 'win32'", launcher);
        Assert.Contains("? ['SIGINT', 'SIGTERM', 'SIGHUP', 'SIGBREAK']", launcher);
        Assert.Contains(": ['SIGINT', 'SIGTERM', 'SIGHUP', 'SIGQUIT']", launcher);
    }

    [Fact]
    public async Task LauncherRegistersSignalHandlersBeforeSpawningChild()
    {
        var launcher = await ReadRepoFileAsync("eng/clipack/npm/aspire.js");

        // A signal delivered in the window between spawn and handler registration
        // could terminate the wrapper and orphan the child - the exact failure the
        // forwarding is meant to prevent. Verify `child` is declared before the
        // signal loop and assigned by `childProcess.spawn` afterwards.
        var declarationIndex = launcher.IndexOf("let child;", System.StringComparison.Ordinal);
        var spawnIndex = launcher.IndexOf("child = childProcess.spawn(", System.StringComparison.Ordinal);
        Assert.True(declarationIndex >= 0, "Expected `let child;` declaration in launcher.");
        Assert.True(spawnIndex >= 0, "Expected `child = childProcess.spawn(` assignment in launcher.");
        Assert.True(declarationIndex < spawnIndex, "Signal handlers must be registered before the child is spawned.");
    }

    [Fact]
    public async Task LauncherDetectsMuslViaFilesystemFallbackWhenLddMissing()
    {
        var launcher = await ReadRepoFileAsync("eng/clipack/npm/aspire.js");

        // On minimal Alpine images `ldd` can be absent, in which case the old code
        // wrongly assumed glibc and resolved a binary whose dynamic linker is
        // missing (crashing at exec). Verify the launcher falls back to probing
        // for the musl dynamic linker (/lib/ld-musl-*.so) when ldd gives no
        // recognizable banner.
        Assert.Contains("ld-musl-", launcher);
        Assert.Contains("readdirSync", launcher);
    }

    [Fact]
    public async Task LauncherCreatesCacheDirectoryOwnerOnly()
    {
        var launcher = await ReadRepoFileAsync("eng/clipack/npm/aspire.js");

        // The native binary cache may land in a shared location (an
        // ASPIRE_NPM_CACHE_DIR override, or the os.tmpdir() fallback when no home
        // directory is available). Create it owner-only so another local user
        // cannot pre-create or read the cached executable.
        Assert.Contains("recursive: true, mode: 0o700", launcher);
    }

    [Fact]
    public async Task LauncherLoadsRidPackageMapInsideErrorHandler()
    {
        var launcher = await ReadRepoFileAsync("eng/clipack/npm/aspire.js");

        // A missing/corrupt aspire-package-map.json used to throw raw
        // ENOENT/SyntaxError with a Node stack at module load — bypassing the
        // launcher's top-level try/catch that produces friendly errors. Verify
        // the map is loaded lazily inside main() and that read/parse failures
        // produce a "corrupted ... Reinstall" message rather than a stack.
        Assert.Contains("if (ridPackageNames === null)", launcher);
        Assert.Contains("ridPackageNames = loadRidPackageNames()", launcher);
        Assert.Contains("Aspire CLI installation is corrupted", launcher);
        Assert.Contains("Reinstall @microsoft/aspire-cli", launcher);
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
        Assert.Equal(
            s_supportedRids.ToDictionary(rid => $"{PackageName}-{rid.Rid}", _ => PackageVersion, StringComparer.Ordinal),
            GetStringMap(GetObject(packageJson, "optionalDependencies")));

        var packageMap = ReadJsonObject(Path.Combine(package.PointerPackageRoot, "bin", "aspire-package-map.json"));
        Assert.Equal(
            s_supportedRids.ToDictionary(rid => rid.Rid, rid => $"{PackageName}-{rid.Rid}", StringComparer.Ordinal),
            GetStringMap(packageMap));

        var readme = await File.ReadAllTextAsync(Path.Combine(package.PointerPackageRoot, "README.md"));
        Assert.Equal(await RenderTemplateAsync("eng/scripts/pack-cli-npm-package.pointer.README.md", ("PACKAGE_NAME", PackageName)), readme);
        Assert.Contains("This package requires Node.js 20 or later.", readme);
        Assert.Contains($"npm install -g {PackageName}", readme);
        Assert.Contains("The native platform packages are installed through npm optional dependencies.", readme);
        Assert.Contains("If you run `aspire update --self` from an npm install, the CLI points you back to this npm update command.", readme);
        Assert.Contains("TypeScript AppHost (`apphost.mts`)", readme);
        Assert.Contains("import { createBuilder } from './.aspire/modules/aspire.mjs';", readme);
        Assert.Contains("aspire dashboard run", readme);
        Assert.DoesNotContain("apphost.ts", readme);
        Assert.DoesNotContain("./.aspire/modules/aspire.js", readme);
        Assert.DoesNotContain("__PACKAGE_NAME__", readme);
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

    [Fact]
    public async Task NpmInstallValidationJobsUseExplicitJobsSharedStepsTemplateAndCentralArtifactNames()
    {
        var commonVariables = await ReadRepoFileAsync("eng/pipelines/common-variables.yml");
        var buildPipeline = await ReadRepoFileAsync("eng/pipelines/azure-pipelines.yml");
        var releasePipeline = await ReadRepoFileAsync("eng/pipelines/release-publish-nuget.yml");

        Assert.Contains("NPM_VALIDATION_SUMMARY_WIN_X64_ARTIFACT", commonVariables);
        Assert.Contains("NPM_VALIDATION_SUMMARY_LINUX_X64_ARTIFACT", commonVariables);
        Assert.Contains("NPM_VALIDATION_SUMMARY_OSX_ARTIFACT", commonVariables);

        Assert.Equal(3, CountOccurrences(buildPipeline, "template: /eng/pipelines/templates/npm-cli-install-validation-steps.yml@self"));
        Assert.DoesNotContain("template: /eng/pipelines/templates/npm-cli-install-validation-job.yml@self", buildPipeline);
        Assert.Contains("job: NpmInstall_Windows_x64", buildPipeline);
        Assert.Contains("job: NpmInstall_Linux_x64", buildPipeline);
        Assert.Contains("job: NpmInstall_macOS", buildPipeline);
        Assert.DoesNotContain("validationSummaryArtifactName: npm-validation-summary-win-x64", buildPipeline);
        Assert.DoesNotContain("validationSummaryArtifactName: npm-validation-summary-linux-x64", buildPipeline);
        Assert.DoesNotContain("validationSummaryArtifactName: npm-validation-summary-osx", buildPipeline);

        Assert.DoesNotContain("artifact: npm-validation-summary-win-x64", releasePipeline);
        Assert.DoesNotContain("artifact: npm-validation-summary-linux-x64", releasePipeline);
        Assert.DoesNotContain("artifact: npm-validation-summary-osx", releasePipeline);
        Assert.DoesNotContain("artifact: $(NPM_VALIDATION_SUMMARY_WIN_X64_ARTIFACT)", releasePipeline);
        Assert.DoesNotContain("artifact: $(NPM_VALIDATION_SUMMARY_LINUX_X64_ARTIFACT)", releasePipeline);
        Assert.DoesNotContain("artifact: $(NPM_VALIDATION_SUMMARY_OSX_ARTIFACT)", releasePipeline);
        Assert.Equal(3, CountOccurrences(releasePipeline, "task: DownloadBuildArtifacts@0"));
        Assert.Equal(3, CountOccurrences(releasePipeline, "pipeline: $(SourceBuildPipeline)"));
        Assert.Equal(3, CountOccurrences(releasePipeline, "buildId: $(SourceBuildId)"));
        Assert.Equal(3, CountOccurrences(releasePipeline, "downloadPath: '$(Pipeline.Workspace)/aspire-build'"));
        Assert.Contains("SourceBuildPipeline: microsoft-aspire", releasePipeline);
        Assert.Contains("artifactName: $(NPM_VALIDATION_SUMMARY_WIN_X64_ARTIFACT)", releasePipeline);
        Assert.Contains("artifactName: $(NPM_VALIDATION_SUMMARY_LINUX_X64_ARTIFACT)", releasePipeline);
        Assert.Contains("artifactName: $(NPM_VALIDATION_SUMMARY_OSX_ARTIFACT)", releasePipeline);
        Assert.Contains("$(NPM_VALIDATION_SUMMARY_WIN_X64_ARTIFACT)", releasePipeline);
        Assert.Contains("$(NPM_VALIDATION_SUMMARY_LINUX_X64_ARTIFACT)", releasePipeline);
        Assert.Contains("$(NPM_VALIDATION_SUMMARY_OSX_ARTIFACT)", releasePipeline);
    }

    [Fact]
    public async Task NpmSigningScopeCoversNestedTarballPayloads()
    {
        var signingProps = XDocument.Parse(await ReadRepoFileAsync("eng/Signing.props"));

        AssertScopedSigningRule(signingProps, "FileExtensionSignInfo", ".tgz", "LinuxSign500180PGP");
        AssertScopedSigningRule(signingProps, "FileSignInfo", "aspire.js", "MicrosoftDotNet500");

        // The native npm packages are built from already-signed native archives.
        // The main Windows build should only produce the detached npm tarball
        // signature; it must still provide scoped rules for nested native
        // executables because Arcade resolves nested file certificates inside
        // the ItemsToSign collision scope.
        AssertScopedSigningRule(signingProps, "FileSignInfo", "aspire.exe", "None");
        AssertScopedSigningRule(signingProps, "FileSignInfo", "aspire", "None");
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
        var testRoot = Path.Combine(_tempDirectory.Path, Path.GetRandomFileName());
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

    private static int CountOccurrences(string value, string substring)
    {
        var count = 0;
        var index = 0;

        while ((index = value.IndexOf(substring, index, System.StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += substring.Length;
        }

        return count;
    }

    private static void AssertScopedSigningRule(XDocument document, string elementName, string include, string certificateName)
    {
        var matchingRules = document
            .Descendants(elementName)
            .Where(element =>
                (string?)element.Attribute("CollisionPriorityId") == "AspireCliNpmPackage" &&
                ((string?)element.Attribute("Include") == include || (string?)element.Attribute("Update") == include) &&
                (string?)element.Attribute("CertificateName") == certificateName)
            .ToArray();

        Assert.True(
            matchingRules.Length == 1,
            $"Expected exactly one {elementName} for '{include}' using '{certificateName}' in the AspireCliNpmPackage signing scope, but found {matchingRules.Length}.");
    }

    public sealed record RidPackageExpectation(string Rid, string BinaryName, string[] Os, string[] Cpu, string[]? Libc);

    private sealed record PackedNpmPackage(string RidPackageRoot, string PointerPackageRoot);
}
