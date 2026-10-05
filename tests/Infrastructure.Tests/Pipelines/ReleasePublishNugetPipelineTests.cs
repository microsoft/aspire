// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Xunit;

namespace Infrastructure.Tests;

public sealed class ReleasePublishNugetPipelineTests
{
    private readonly string _repoRoot = RepoRoot.Path;

    [Fact]
    public void UsesMicroBuildPublishTemplateAndRoutesPublishAuthenticationPerJob()
    {
        var pipeline = AzurePipelinesYaml.Load("eng/pipelines/release-publish-nuget.yml");
        Assert.Equal(
            "azure-pipelines/MicroBuild.1ES.Official.Publish.yml@MicroBuildTemplate",
            AzurePipelinesYaml.Scalar(AzurePipelinesYaml.Mapping(pipeline, "extends"), "template"));
        Assert.Equal("dotnet-aspire", AzurePipelinesYaml.Scalar(AzurePipelinesYaml.Variable(pipeline, "TeamName"), "value"));

        var releaseStage = AzurePipelinesYaml.Stage(pipeline, "Release");
        var releaseJob = AzurePipelinesYaml.Job(releaseStage, "ReleaseJob");
        var releasePublish = AzurePipelinesYaml.Mapping(
            AzurePipelinesYaml.Mapping(
                AzurePipelinesYaml.Mapping(releaseJob, "templateContext"),
                "mb"),
            "publish");
        Assert.Equal(
            "https://pkgs.dev.azure.com/dnceng/_packaging/MicroBuildToolset/nuget/v3/index.json",
            AzurePipelinesYaml.Scalar(releasePublish, "feedSource"));

        var nonPublishingJobs = new[]
        {
            AzurePipelinesYaml.Job(AzurePipelinesYaml.Stage(pipeline, "PrepareArtifacts"), "PrepareJob"),
            AzurePipelinesYaml.Job(releaseStage, "VSCodeExtensionJob"),
            AzurePipelinesYaml.Job(releaseStage, "WinGetJob"),
            AzurePipelinesYaml.Job(AzurePipelinesYaml.Stage(pipeline, "GitHubTasks"), "DispatchGitHubTasksJob"),
            AzurePipelinesYaml.Job(AzurePipelinesYaml.Stage(pipeline, "GitHubTasks"), "PublishReleaseAssetsJob"),
            AzurePipelinesYaml.Job(AzurePipelinesYaml.Stage(pipeline, "GitHubTasks"), "UpdateNixPackageJob")
        };
        Assert.All(nonPublishingJobs, job =>
        {
            var publish = AzurePipelinesYaml.Mapping(
                AzurePipelinesYaml.Mapping(
                    AzurePipelinesYaml.Mapping(job, "templateContext"),
                    "mb"),
                "publish");
            Assert.Equal("false", AzurePipelinesYaml.Scalar(publish, "enabled"));
        });
    }

    [Fact]
    public void NpmPublishParametersAndEsrpIdentitiesHaveSafeDefaults()
    {
        var pipeline = AzurePipelinesYaml.Load("eng/pipelines/release-publish-nuget.yml");
        var parameterNames = AzurePipelinesYaml.Parameters(pipeline)
            .Select(parameter => AzurePipelinesYaml.Scalar(parameter, "name"))
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains("SkipNpmRidPublish", parameterNames);
        Assert.Contains("SkipNpmPointerPublish", parameterNames);
        Assert.DoesNotContain("SkipNpmPublish", parameterNames);
        Assert.DoesNotContain("AllowNpmLatestDistTagMove", parameterNames);
        Assert.Equal("false", AzurePipelinesYaml.Scalar(AzurePipelinesYaml.Parameter(pipeline, "SkipNpmRidPublish"), "default"));
        Assert.Equal("false", AzurePipelinesYaml.Scalar(AzurePipelinesYaml.Parameter(pipeline, "SkipNpmPointerPublish"), "default"));

        var requiredOwners = AzurePipelinesYaml.Scalar(
            AzurePipelinesYaml.Variable(pipeline, "NPM_PUBLISH_REQUIRED_OWNERS"),
            "value");
        var ownerDefault = AzurePipelinesYaml.Scalar(AzurePipelinesYaml.Parameter(pipeline, "NpmPublishOwners"), "default")!;
        var approverDefault = AzurePipelinesYaml.Scalar(AzurePipelinesYaml.Parameter(pipeline, "NpmPublishApprovers"), "default");
        Assert.Equal("joperezr,ankj", requiredOwners);
        AssertOwnerDefaultIsSingleRequiredAlias(requiredOwners!, ownerDefault, "NpmPublishOwners");
        Assert.Equal("adamratzman", approverDefault);

        var releaseJob = AzurePipelinesYaml.Job(AzurePipelinesYaml.Stage(pipeline, "Release"), "ReleaseJob");
        var npmPublishSteps = AzurePipelinesYaml.StepsRecursively(releaseJob)
            .Where(step => AzurePipelinesYaml.Scalar(step, "template") == "MicroBuild.Publish.yml@MicroBuildTemplate")
            .ToArray();
        Assert.Equal(2, npmPublishSteps.Length);
        Assert.Equal(
            [
                "$(Pipeline.Workspace)\\npm\\pointer-package",
                "$(Pipeline.Workspace)\\npm\\rid-packages"
            ],
            npmPublishSteps
                .Select(step => AzurePipelinesYaml.Scalar(AzurePipelinesYaml.Mapping(step, "parameters"), "folderLocation"))
                .Order(StringComparer.Ordinal));
        Assert.All(npmPublishSteps, step =>
        {
            var parameters = AzurePipelinesYaml.Mapping(step, "parameters");
            Assert.Equal("$(NpmPublishOwnersEffective)", AzurePipelinesYaml.Scalar(parameters, "owners"));
            Assert.Equal("$(NpmPublishApproversEffective)", AzurePipelinesYaml.Scalar(parameters, "approvers"));
        });
    }

    [Fact]
    public void NpmPublishValidationAndArtifactStepsRemainOrdered()
    {
        var pipeline = AzurePipelinesYaml.Load("eng/pipelines/release-publish-nuget.yml");
        var releaseJob = AzurePipelinesYaml.Job(AzurePipelinesYaml.Stage(pipeline, "Release"), "ReleaseJob");
        var steps = AzurePipelinesYaml.StepsRecursively(releaseJob);

        var validateParameters = FindStepIndex(steps, "Validate Parameters");
        var nugetPublish = FindStepIndex(steps, step => AzurePipelinesYaml.Scalar(step, "task") == "1ES.PublishNuget@1");
        var validateSummaries = FindStepIndex(steps, "Validate npm Prepare-Stage Summaries");
        var verifyVersions = FindStepIndex(steps, "Verify Staged npm Package Versions");
        var alreadyPublished = FindStepIndex(steps, "Verify npm Packages Are Not Already Published");
        var ridPublish = FindStepIndex(
            steps,
            step => IsNpmPublishTemplateFor(step, "$(Pipeline.Workspace)\\npm\\rid-packages"));
        var pointerPreflight = FindStepIndex(steps, "Verify npm RID Packages Present Before Pointer Publish");
        var pointerPublish = FindStepIndex(
            steps,
            step => IsNpmPublishTemplateFor(step, "$(Pipeline.Workspace)\\npm\\pointer-package"));
        var registryValidation = FindStepIndex(steps, "Validate Published npm Package from Registry");

        Assert.True(validateParameters < nugetPublish);
        Assert.True(validateSummaries < verifyVersions);
        Assert.True(verifyVersions < alreadyPublished);
        Assert.True(alreadyPublished < ridPublish);
        Assert.True(ridPublish < pointerPreflight);
        Assert.True(pointerPreflight < pointerPublish);
        Assert.True(pointerPublish < registryValidation);
    }

    [Fact]
    public async Task NpmOwnerParametersArePassedAsDataAndWildcardExpressionsRemainForbidden()
    {
        var pipeline = await ReadRepoFileAsync("eng/pipelines/release-publish-nuget.yml");

        var parsed = AzurePipelinesYaml.Load("eng/pipelines/release-publish-nuget.yml");
        var releaseJob = AzurePipelinesYaml.Job(AzurePipelinesYaml.Stage(parsed, "Release"), "ReleaseJob");
        Assert.DoesNotContain(
            AzurePipelinesYaml.StepsRecursively(releaseJob),
            step => AzurePipelinesYaml.Scalar(step, "checkout") is not null);

        var validateParameters = AzurePipelinesYaml.Step(releaseJob, "Validate Parameters");
        var environment = AzurePipelinesYaml.Mapping(validateParameters, "env");
        Assert.Equal("${{ parameters.NpmPublishOwners }}", AzurePipelinesYaml.Scalar(environment, "NPM_PUBLISH_OWNERS"));
        Assert.Equal("${{ parameters.NpmPublishApprovers }}", AzurePipelinesYaml.Scalar(environment, "NPM_PUBLISH_APPROVERS"));
        Assert.Equal("$(NPM_PUBLISH_REQUIRED_OWNERS)", AzurePipelinesYaml.Scalar(environment, "NPM_PUBLISH_REQUIRED_OWNERS"));

        // Parsing erases whether the expression was quoted, and Azure treats an unquoted template
        // expression as an object. Keep this exact lexical guard in addition to the parsed env test.
        Assert.DoesNotContain("NPM_PUBLISH_OWNERS: ${{ parameters.NpmPublishOwners }}", pipeline);
        Assert.DoesNotContain("NPM_PUBLISH_APPROVERS: ${{ parameters.NpmPublishApprovers }}", pipeline);
        Assert.DoesNotContain("${{ parameters.* }}", pipeline);
    }

    [Fact]
    public async Task NpmAliasValidationHelpersMatchScript()
    {
        var pipeline = await ReadRepoFileAsync("eng/pipelines/release-publish-nuget.yml");
        var script = await ReadRepoFileAsync("eng/scripts/validate-npm-release-aliases.ps1");

        // releaseJob runs with `checkout: none`, so the pipeline cannot dot-source the script and
        // instead inlines the same helper functions. Keep the two copies identical (ignoring
        // indentation) so the behavior verified by ValidateNpmReleaseAliasesTests against the
        // script also holds for the inlined release-pipeline copy.
        var pipelineHelpers = ExtractHelperRegion(pipeline);
        var scriptHelpers = ExtractHelperRegion(script);

        Assert.NotEmpty(pipelineHelpers);
        Assert.Equal(scriptHelpers, pipelineHelpers);
    }

    private static IReadOnlyList<string> ExtractHelperRegion(string contents)
    {
        const string begin = ">>> BEGIN npm release alias helpers";
        const string end = "<<< END npm release alias helpers";

        var beginIndex = contents.IndexOf(begin, StringComparison.Ordinal);
        var endIndex = contents.IndexOf(end, StringComparison.Ordinal);

        Assert.True(beginIndex >= 0, $"Expected to find '{begin}'.");
        Assert.True(endIndex > beginIndex, $"Expected to find '{end}' after '{begin}'.");

        // Take the lines between the begin- and end-marker lines, trim the (differing) indentation,
        // and drop blank lines so only the helper-function content is compared.
        var regionStart = contents.IndexOf('\n', beginIndex) + 1;
        var regionEnd = contents.LastIndexOf('\n', endIndex);

        return contents[regionStart..regionEnd]
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToArray();
    }

    [Fact]
    public async Task ValidatesPublishedNpmPackageFromRegistryAfterPublish()
    {
        var pipeline = await ReadRepoFileAsync("eng/pipelines/release-publish-nuget.yml");
        var pointerPublishIndex = FindRequiredText(pipeline, "folderLocation: '$(Pipeline.Workspace)\\npm\\pointer-package'");
        var registryValidationIndex = FindRequiredText(pipeline, "npm install -g --foreground-scripts=true --no-audit --no-fund --loglevel=warn --registry=https://registry.npmjs.org/ $packageSpec");
        var channelPromotionIndex = FindRequiredText(pipeline, "# ===== PROMOTE TO CHANNEL =====");
        var nodeToolIndex = FindRequiredText(pipeline, "task: NodeTool@0");
        var dryRunReachabilityIndex = FindRequiredText(pipeline, "Dry Run - Validate npm Registry Reachability");
        var pointerSkipIndex = FindRequiredText(pipeline, "SkipNpmPointerPublish");

        Assert.True(
            pointerPublishIndex < registryValidationIndex,
            "Expected registry validation to happen after the npm pointer package is published.");

        Assert.True(
            registryValidationIndex < channelPromotionIndex,
            "Expected registry validation to happen before channel promotion.");

        Assert.True(
            nodeToolIndex < registryValidationIndex,
            "Expected Node.js to be installed before registry validation uses npm.");

        Assert.True(
            dryRunReachabilityIndex < registryValidationIndex,
            "Expected dry-run registry reachability validation to exercise npm before the actual publish-only install smoke.");

        Assert.True(
            pointerSkipIndex < registryValidationIndex,
            "Expected pointer package publishing to be independently skippable so registry validation can be retried without republishing.");

        Assert.Contains("aspire --version output matched the published npm package version", pipeline);
        Assert.Contains("npm view $packageSpec version --registry=https://registry.npmjs.org/", pipeline);
        Assert.Contains("Registry validation will still install the selected source build's pointer package version from npm.", pipeline);
    }

    [Fact]
    public async Task PostPublishSmokeRejectsEmptyAspireVersionOutput()
    {
        var pipeline = await ReadRepoFileAsync("eng/pipelines/release-publish-nuget.yml");

        // Without an explicit empty-stdout check, `@(...)` wraps an empty
        // version line into an empty array and PowerShell's `-notmatch`
        // against an empty array silently returns an empty array (falsy),
        // letting an `aspire --version` that exits 0 with no output slip past
        // the version-pattern check. Assert the explicit guard is present.
        Assert.Contains("$versionLine.Count -eq 0", pipeline);
        Assert.Contains("produced no output.", pipeline);
    }

    [Fact]
    public async Task PointerPreflightPinsPublicNpmRegistry()
    {
        var pipeline = await ReadRepoFileAsync("eng/pipelines/release-publish-nuget.yml");

        // Every npm command in the publish flow MUST explicitly pin
        // `--registry=https://registry.npmjs.org/`. The release agent's
        // ambient registry is not guaranteed to be public npmjs — an
        // internal mirror may be configured via .npmrc or
        // npm_config_registry. Without the explicit pin, the preflight
        // could (a) spuriously fail after a successful public publish
        // if the mirror lacks the new package, or (b) pass against a
        // stale mirror and let the pointer publish reference RIDs the
        // public registry can't serve. Guard against future drift by
        // asserting the preflight `npm view` is registry-pinned.
        Assert.Contains(
            "npm view $spec version --registry=https://registry.npmjs.org/",
            pipeline);
    }

    [Fact]
    public async Task PointerPreflightRetriesForPropagationLag()
    {
        var pipeline = await ReadRepoFileAsync("eng/pipelines/release-publish-nuget.yml");

        // The post-publish smoke uses 10×30s retry loops to ride out npm
        // CDN propagation. The pre-pointer RID preflight must do the same
        // because npm propagation of 7 freshly-published scoped tarballs
        // can exceed the fixed NpmRegistryPropagationDelayMinutes wait.
        // A single-shot preflight would fail closed AFTER all 7 RID
        // packages are already published, forcing a manual re-run with
        // SkipNpmRidPublish=true. Assert the preflight has its own
        // retry loop.
        Assert.Contains("$preflightAttempts = 10", pipeline);
        Assert.Contains("$preflightDelaySeconds = 30", pipeline);
        Assert.Contains("for ($preflightAttempt = 1; $preflightAttempt -le $preflightAttempts;", pipeline);
    }

    [Fact]
    public async Task NpmViewParsingFiltersToSemverShape()
    {
        var pipeline = await ReadRepoFileAsync("eng/pipelines/release-publish-nuget.yml");

        // `npm view --loglevel=warn` merges deprecation / peer-dep /
        // EBADENGINE warnings onto stderr. With `2>&1`, taking
        // `Select-Object -First 1` could latch a warning line as the
        // version, burn all 10 retries, and fail the release even though
        // the publish succeeded. Both the preflight and post-publish
        // smoke filter to lines that match a semver shape before
        // comparing.
        var semverRegexUses = System.Text.RegularExpressions.Regex.Matches(
            pipeline,
            @"\$semverRegex\s*=\s*'\^\\d\+\\\.\\d\+\\\.\\d\+");
        Assert.True(
            semverRegexUses.Count >= 2,
            $"Expected the semver regex to be defined in both the preflight and post-publish smoke; found {semverRegexUses.Count} occurrence(s).");
    }

    [Fact]
    public async Task NpmSignatureSidecarsAreContentSanityChecked()
    {
        // release-publish-nuget.yml inlines a content sanity check on every
        // microsoft-aspire-cli*.tgz.sig sidecar. The check exists to catch
        // the most likely silent failure mode in Arcade/ESRP signing: the
        // sidecar file gets emitted (so a file-existence check passes) but
        // the content is empty or garbage. A real PGP signature is hundreds
        // of bytes and starts with either the ASCII-armored header
        // `-----BEGIN PGP SIGNATURE-----` (RFC 9580 §6) or an OpenPGP binary
        // signature packet (tag 2: old-format 0x88..0x8B or new-format 0xC2,
        // RFC 9580 §4.3 / §5.2).
        //
        // Behavioral coverage of the same logic in eng/scripts/validate-npm-package-signatures.ps1
        // lives in ValidateNpmPackageSignaturesTests; if release-publish-nuget.yml
        // is ever refactored to call that script instead of inlining the
        // bytes, assert the script invocation here and drop these literal
        // marker assertions.
        var pipeline = await ReadRepoFileAsync("eng/pipelines/release-publish-nuget.yml");

        Assert.Contains("'-----BEGIN PGP SIGNATURE-----'", pipeline);
        Assert.Contains("0x8B", pipeline);
        Assert.Contains("0xC2", pipeline);
        Assert.Contains("content sanity check", pipeline);
    }

    [Fact]
    public async Task WinGetPublishingRunsOnlyForStableReleases()
    {
        var pipeline = await ReadRepoFileAsync("eng/pipelines/release-publish-nuget.yml");

        var parsed = AzurePipelinesYaml.Load("eng/pipelines/release-publish-nuget.yml");
        Assert.Equal("false", AzurePipelinesYaml.Scalar(AzurePipelinesYaml.Parameter(parsed, "SkipWinGetPublish"), "default"));

        const string stableReleaseGate = "${{ if and(eq(parameters.SkipWinGetPublish, false), eq(parameters.IsPrerelease, false)) }}:";
        Assert.Equal(2, pipeline.Split(stableReleaseGate, StringSplitOptions.None).Length - 1);

        var winGetJob = ExtractSection(
            pipeline,
            "# ===== WINGET PUBLISHING =====",
            "# ===== STAGE 3: GITHUB TASKS =====");
        Assert.Contains("eq('${{ parameters.SkipWinGetPublish }}', 'false')", winGetJob);
        Assert.Contains("eq('${{ parameters.IsPrerelease }}', 'false')", winGetJob);

        var releaseSummary = ExtractSection(
            pipeline,
            "# ===== SUMMARY =====",
            "# ===== VS CODE EXTENSION PUBLISHING =====");
        var winGetSummary = ExtractSection(
            releaseSummary,
            """Write-Host "║ WinGet:""",
            """Write-Host "║ GitHub Tasks:""");
        Assert.Contains("""if ("${{ parameters.SkipWinGetPublish }}" -eq "true")""", winGetSummary);
        Assert.Contains("""elseif ("${{ parameters.IsPrerelease }}" -eq "true")""", winGetSummary);
        Assert.Contains("Write-Host \" (SKIPPED - prerelease)\"", winGetSummary);
    }

    [Fact]
    public async Task VSCodeExtensionPublishUsesAzureCredential()
    {
        var pipeline = await ReadRepoFileAsync("eng/pipelines/release-publish-nuget.yml");
        var job = ExtractSection(
            pipeline,
            "# ===== VS CODE EXTENSION PUBLISHING =====",
            "# ===== WINGET PUBLISHING =====");

        Assert.Contains("task: AzureCLI@2", job);
        // The service connection name must match the connection whose identity is authorized on
        // the microsoft-aspire Marketplace publisher. A mismatch fails only at publish time,
        // which is the last step of a release.
        Assert.Contains("azureSubscription: 'AspireSecurePublishPipelineMarketplaceConnectionWithManagedIdentity'", job);
        Assert.Contains("vsce verify-pat --azure-credential $publisher", job);
        Assert.Contains("""$publishArgs = @("publish", "--azure-credential", "--packagePath", $vsix.FullName, "--manifestPath", $manifestPath, "--signaturePath", $signaturePath)""", job);
        Assert.Contains("vsce @publishArgs", job);

        var secretReferenceMatches = System.Text.RegularExpressions.Regex.Matches(job, @"\b(VSCE_PAT|VscePublishToken)\b");
        Assert.Empty(secretReferenceMatches);
    }

    [Fact]
    public async Task WinGetJobVerifiesWingetCreateBeforeConditionalSubmission()
    {
        var template = await ReadRepoFileAsync("eng/pipelines/templates/publish-winget.yml");
        var runtimeInstallIndex = FindRequiredText(template, "- task: UseDotNet@2");
        var wingetCreateInstallIndex = FindRequiredText(template, "Write-Host \"Downloading wingetcreate...\"");
        var submitIndex = FindRequiredText(template, "Write-Host \"Submitting WinGet manifests");
        var runtimeInstall = template[runtimeInstallIndex..wingetCreateInstallIndex];
        var wingetCreateInstall = template[wingetCreateInstallIndex..submitIndex];
        var submission = template[submitIndex..];

        Assert.Contains("packageType: 'runtime'", runtimeInstall);
        Assert.Contains("version: '9.0.x'", runtimeInstall);
        Assert.Contains("condition: succeeded()", runtimeInstall);
        Assert.Contains("wingetcreate.exe\" info", wingetCreateInstall);
        Assert.Contains("condition: succeeded()", wingetCreateInstall);
        Assert.Contains("eq('${{ parameters.dryRun }}', 'false')", submission);
        Assert.Contains("eq(variables['_IsProductionBranch'], 'true')", submission);
    }

    [Fact]
    public async Task WinGetPreparationExercisesWingetCreate()
    {
        var template = await ReadRepoFileAsync("eng/pipelines/templates/prepare-winget-manifest.yml");

        Assert.Contains("- task: UseDotNet@2", template);
        Assert.Contains("packageType: 'runtime'", template);
        Assert.Contains("version: '9.0.x'", template);
        Assert.Contains("https://aka.ms/wingetcreate/latest", template);
        Assert.Contains("wingetcreate.exe\" info", template);
    }

    [Fact]
    public async Task MarketplacePublishingDocumentationKeepsIdentityDetailsInternalAndRetiresPat()
    {
        var documentation = await ReadRepoFileAsync("docs/release-process.md");
        var identitySection = ExtractSection(
            documentation,
            "#### Marketplace publishing identity",
            "### Approved GitHub Actions");

        Assert.Contains(
            "[Azure DevOps service connections](https://dev.azure.com/dnceng/internal/_settings/adminservices)",
            identitySection);
        Assert.DoesNotMatch(
            @"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b",
            documentation);
        Assert.Contains(
            "If the variable is still present in `Aspire-Release-Secrets`, revoke the PAT and delete the variable.",
            documentation);
    }

    private static string ExtractSection(string contents, string begin, string end)
    {
        var beginIndex = FindRequiredText(contents, begin);
        var endIndex = FindRequiredText(contents, end);

        Assert.True(endIndex > beginIndex, $"Expected '{end}' after '{begin}'.");

        return contents[beginIndex..endIndex];
    }

    private static int FindStepIndex(IReadOnlyList<YamlDotNet.RepresentationModel.YamlMappingNode> steps, string displayName)
        => FindStepIndex(steps, step => AzurePipelinesYaml.Scalar(step, "displayName") == displayName);

    private static int FindStepIndex(
        IReadOnlyList<YamlDotNet.RepresentationModel.YamlMappingNode> steps,
        Func<YamlDotNet.RepresentationModel.YamlMappingNode, bool> predicate)
    {
        for (var index = 0; index < steps.Count; index++)
        {
            if (predicate(steps[index]))
            {
                return index;
            }
        }

        throw new Xunit.Sdk.XunitException("Expected pipeline step was not found.");
    }

    private static bool IsNpmPublishTemplateFor(
        YamlDotNet.RepresentationModel.YamlMappingNode step,
        string folderLocation)
        => AzurePipelinesYaml.Scalar(step, "template") == "MicroBuild.Publish.yml@MicroBuildTemplate" &&
           AzurePipelinesYaml.Scalar(AzurePipelinesYaml.Mapping(step, "parameters"), "folderLocation") == folderLocation;

    private static int FindRequiredText(string contents, string text)
    {
        var index = contents.IndexOf(text, StringComparison.Ordinal);

        Assert.True(index >= 0, $"Expected to find '{text}'.");

        return index;
    }

    private static void AssertOwnerDefaultIsSingleRequiredAlias(string requiredAliasesValue, string actualAliasesValue, string parameterName)
    {
        // The single-owner rule means the default must normalize to exactly one alias, and that
        // alias must be one of the required ESRP owner aliases so unattended runs pass validation.
        var actualAliases = ParseNpmReleaseAliasSet(actualAliasesValue);
        Assert.True(
            actualAliases.Count == 1,
            $"{parameterName} default must be a single alias, but was '{actualAliasesValue}'.");

        var requiredAliases = ParseNpmReleaseAliasSet(requiredAliasesValue);
        Assert.True(
            actualAliases.All(requiredAliases.Contains),
            $"{parameterName} default '{actualAliasesValue}' must be one of the required ESRP owner aliases: {requiredAliasesValue}.");
    }

    private static HashSet<string> ParseNpmReleaseAliasSet(string value)
    {
        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var alias = entry;
            if (alias.EndsWith("@microsoft.com", StringComparison.OrdinalIgnoreCase))
            {
                alias = alias[..^"@microsoft.com".Length];
            }

            aliases.Add(alias.ToLowerInvariant());
        }

        return aliases;
    }

    private Task<string> ReadRepoFileAsync(string relativePath)
        => File.ReadAllTextAsync(Path.Combine(_repoRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
}
