// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Xunit;

namespace Infrastructure.Tests;

public sealed class ReleasePublishNugetPipelineTests
{
    private readonly string _repoRoot = RepoRoot.Path;

    /// <summary>
    /// The public registry smoke test runs whenever the release publishes npm packages or performs
    /// a mirror-only rerun. It is never gated on <c>NpmInternalMirrorAction</c> being enabled, so a
    /// run that publishes npm packages always proves the public package installs.
    /// </summary>
    private const string PublicNpmSmokeCondition =
        "condition: and(succeeded(), or(eq('${{ parameters.SkipNpmRidPublish }}', 'false'), eq('${{ parameters.SkipNpmPointerPublish }}', 'false'), eq('${{ parameters.NpmInternalMirrorAction }}', 'only')))";

    /// <summary>
    /// Internal mirror seeding and validation is its own release action: it runs when
    /// <c>NpmInternalMirrorAction</c> is <c>auto</c> and this run publishes npm packages, or when it
    /// is <c>only</c> (an intentional mirror-only rerun).
    /// </summary>
    private const string InternalMirrorCondition =
        "condition: and(succeeded(), or(and(eq('${{ parameters.NpmInternalMirrorAction }}', 'auto'), or(eq('${{ parameters.SkipNpmRidPublish }}', 'false'), eq('${{ parameters.SkipNpmPointerPublish }}', 'false'))), eq('${{ parameters.NpmInternalMirrorAction }}', 'only')))";

    /// <summary>
    /// Compile-time gate for every stable npm step in the release job. A release rerun that is
    /// unrelated to npm (both publish skips set, <c>NpmInternalMirrorAction</c> left at
    /// <c>auto</c>) emits none of these steps.
    /// </summary>
    private const string StableNpmWorkGate =
        "- ${{ if and(eq(parameters.DryRun, false), eq(parameters.IsPrerelease, false), or(eq(parameters.SkipNpmRidPublish, false), eq(parameters.SkipNpmPointerPublish, false), eq(parameters.NpmInternalMirrorAction, 'only'))) }}:";

    /// <summary>
    /// Compile-time gate for staging npm artifacts. npm artifacts are staged when the run publishes
    /// npm packages, or for a stable mirror-only rerun that validates already-published packages.
    /// </summary>
    private const string StableNpmStagingGate =
        "- ${{ if or(eq(parameters.SkipNpmRidPublish, false), eq(parameters.SkipNpmPointerPublish, false), and(eq(parameters.DryRun, false), eq(parameters.IsPrerelease, false), eq(parameters.NpmInternalMirrorAction, 'only'))) }}:";

    [Fact]
    public async Task ValidatesNpmPublishPreconditionsBeforeNuGetPublish()
    {
        var pipeline = await ReadRepoFileAsync("eng/pipelines/release-publish-nuget.yml");
        var nuGetPublishIndex = FindRequiredText(pipeline, "task: 1ES.PublishNuget@1");

        AssertBefore(
            pipeline,
            "npm publishing is blocked for prerelease runs because the MicroBuild npm publish template does not yet expose a dist-tag parameter.",
            nuGetPublishIndex);

        AssertBefore(
            pipeline,
            "$parameterName must include at least one required ESRP owner alias",
            nuGetPublishIndex);

        AssertBefore(
            pipeline,
            "Assert-SingleNpmReleaseAlias $normalizedApprovers 'NpmPublishApprovers'",
            nuGetPublishIndex);
    }

    [Fact]
    public async Task UsesEsrpPublishTemplateForNpmPublishing()
    {
        var pipeline = await ReadRepoFileAsync("eng/pipelines/release-publish-nuget.yml");

        // The MicroBuild. prefix is REQUIRED for ESRP-based publishing — it wires the MicroBuild
        // signing/publish credential context so MicroBuild.Publish.yml and the auto-injected
        // MicroBuildAuthorizePublishPlugin task can authenticate against the
        // devdiv.pkgs.visualstudio.com/_packaging/MicroBuildToolset feed.
        // Plain `1ES.Official.Publish.yml@MicroBuildTemplate` (no `MicroBuild.` prefix) injects
        // the authorize task without supplying credentials, causing a 401.
        // See microsoft/vscode-azuretools, microsoft/pyright, microsoft/vscode-python-environments.
        Assert.Contains("template: azure-pipelines/MicroBuild.1ES.Official.Publish.yml@MicroBuildTemplate", pipeline);
        Assert.DoesNotContain("template: v1/1ES.Official.PipelineTemplate.yml@1ESPipelineTemplates", pipeline);
        // Guard against accidental regression to the plain template (without the MicroBuild. prefix)
        Assert.DoesNotContain("template: azure-pipelines/1ES.Official.Publish.yml@MicroBuildTemplate", pipeline);
    }

    [Fact]
    public async Task DefinesTeamNameVariableForMicroBuildTelemetry()
    {
        var pipeline = await ReadRepoFileAsync("eng/pipelines/release-publish-nuget.yml");

        // MicroBuild.1ES.Official.Publish.yml@MicroBuildTemplate auto-injects MicroBuildCleanup@1
        // (displayName "🔩 MicroBuild Telemetry") at the END of every job. That task hard-requires
        // a variable literally named `TeamName`; if absent the task fails with:
        //   "The TeamName variable is required to use MicroBuild. Please update your definition
        //    variables to include your team name in the 'TeamName' variable."
        // common-variables.yml defines `_TeamName: dotnet-aspire` for Arcade conventions but
        // MicroBuild reads the unprefixed name, so we must declare TeamName at pipeline scope.
        Assert.Contains("- name: TeamName", pipeline);
        Assert.Contains("value: dotnet-aspire", pipeline);
    }

    [Fact]
    public async Task RoutesMicroBuildPublishAuthPluginToDncengFeedOrDisablesIt()
    {
        var pipeline = await ReadRepoFileAsync("eng/pipelines/release-publish-nuget.yml");

        // MicroBuild.1ES.Official.Publish.yml@MicroBuildTemplate -> Stages/PublishStage.yml
        // -> Jobs/PublishJob.yml auto-injects MicroBuildAuthorizePublishPlugin@0 at the START
        // of every job. By default that task pulls its nuget package from
        // `devdiv.pkgs.visualstudio.com/_packaging/MicroBuildToolset`, which is NOT accessible
        // from the dnceng collection -> 401 -> stage fails before any customer step runs.
        // Two valid escapes from MicroBuildTemplate are required:
        //   1) templateContext.mb.publish.enabled: false  (for jobs that don't ESRP-publish)
        //   2) templateContext.mb.publish.feedSource: <dnceng mirror>  (for the publishing job)
        // Both must be present in this pipeline:
        //   - non-publishing jobs (PrepareJob, WinGetJob, DispatchGitHubTasksJob,
        //     PublishReleaseAssetsJob, UpdateNixPackageJob, HomebrewValidateJob) -> enabled: false
        //   - ReleaseJob (the only job that actually publishes) -> feedSource = dnceng mirror
        Assert.Contains("enabled: false", pipeline);
        Assert.Contains(
            "feedSource: 'https://pkgs.dev.azure.com/dnceng/_packaging/MicroBuildToolset/nuget/v3/index.json'",
            pipeline);
    }

    [Fact]
    public async Task AlreadyPublishedNpmPreflightExitsZeroAfterHandledRegistryMisses()
    {
        var pipeline = await ReadRepoFileAsync("eng/pipelines/release-publish-nuget.yml");

        var successIndex = FindRequiredText(pipeline, "No scheduled npm package versions already exist on npm.");
        var displayNameIndex = FindRequiredText(pipeline, "displayName: 'Verify npm Packages Are Not Already Published'");
        var successTail = pipeline[successIndex..displayNameIndex];

        // Azure Pipelines' PowerShell task exits with $LASTEXITCODE after the inline script.
        // `npm view` returns 1 for E404, which this script handles as success, so the success
        // path must override that stale native exit code.
        Assert.Contains("exit 0", successTail);
    }

    [Fact]
    public async Task NpmPublishAndMirrorValidationUseDedicatedSkipParameters()
    {
        var pipeline = await ReadRepoFileAsync("eng/pipelines/release-publish-nuget.yml");
        var spec = await ReadRepoFileAsync("docs/specs/npm-cli-package.md");

        Assert.DoesNotContain("SkipNpmPublish", pipeline);
        Assert.DoesNotContain("Skip npm Publish", pipeline);
        Assert.DoesNotContain("SkipNpmPublish", spec);
        Assert.Contains("displayName: '[Advanced] Skip npm RID Package Publishing", pipeline);
        Assert.Contains("displayName: '[Advanced] Skip npm Pointer Package Publishing", pipeline);
        Assert.DoesNotContain("SkipNpmMirrorValidation", pipeline);
        Assert.DoesNotContain("SkipNpmMirrorValidation", spec);
        Assert.Contains("displayName: '[Advanced] npm Internal Mirror Seeding and Validation", pipeline);
        Assert.Equal("auto", FindYamlParameterDefault(pipeline, "NpmInternalMirrorAction"));
        Assert.Contains(
            "or(eq(parameters.SkipNpmRidPublish, false), eq(parameters.SkipNpmPointerPublish, false), and(eq(parameters.DryRun, false), eq(parameters.IsPrerelease, false), eq(parameters.NpmInternalMirrorAction, 'only')))",
            pipeline);
        Assert.Contains(
            "and(eq(parameters.SkipNpmRidPublish, true), eq(parameters.SkipNpmPointerPublish, true), or(eq(parameters.DryRun, true), eq(parameters.IsPrerelease, true), ne(parameters.NpmInternalMirrorAction, 'only')))",
            pipeline);
    }

    /// <summary>
    /// Evaluates the npm gates against the operator recipes documented in
    /// <c>docs/release-process.md</c>. The decisive case is the recovery rerun that is unrelated to
    /// npm (both publish skips set, <c>NpmInternalMirrorAction</c> left at its <c>auto</c> default):
    /// it must emit no npm steps at all, so it cannot fail on a source build whose npm pointer
    /// package was never published.
    /// </summary>
    [Theory]
    // ridSkip, pointerSkip, mirrorAction, dryRun, prerelease, stagesNpmArtifacts, runsStableNpmSteps, seedsMirror, runsPublicSmoke
    [InlineData(false, false, "auto", false, false, true, true, true, true)]
    [InlineData(true, true, "only", false, false, true, true, true, true)]
    [InlineData(false, true, "auto", false, false, true, true, true, true)]
    [InlineData(false, false, "skip", false, false, true, true, false, true)]
    [InlineData(true, true, "auto", false, false, false, false, false, false)]
    [InlineData(true, true, "skip", false, false, false, false, false, false)]
    [InlineData(false, false, "auto", true, false, true, false, false, false)]
    [InlineData(true, true, "auto", false, true, false, false, false, false)]
    public async Task NpmGatesFollowDocumentedReleaseRecipes(
        bool ridSkip,
        bool pointerSkip,
        string mirrorAction,
        bool dryRun,
        bool prerelease,
        bool stagesNpmArtifacts,
        bool runsStableNpmSteps,
        bool seedsMirror,
        bool runsPublicSmoke)
    {
        var pipeline = await ReadRepoFileAsync("eng/pipelines/release-publish-nuget.yml");
        const string placeholderGate =
            "- ${{ if and(eq(parameters.SkipNpmRidPublish, true), eq(parameters.SkipNpmPointerPublish, true), or(eq(parameters.DryRun, true), eq(parameters.IsPrerelease, true), ne(parameters.NpmInternalMirrorAction, 'only'))) }}:";

        foreach (var gate in new[] { StableNpmStagingGate, StableNpmWorkGate, placeholderGate })
        {
            Assert.Contains(gate, pipeline, StringComparison.Ordinal);
        }

        foreach (var condition in new[] { PublicNpmSmokeCondition, InternalMirrorCondition })
        {
            Assert.Contains(condition, pipeline, StringComparison.Ordinal);
        }

        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["SkipNpmRidPublish"] = ridSkip ? "true" : "false",
            ["SkipNpmPointerPublish"] = pointerSkip ? "true" : "false",
            ["NpmInternalMirrorAction"] = mirrorAction,
            ["DryRun"] = dryRun ? "true" : "false",
            ["IsPrerelease"] = prerelease ? "true" : "false"
        };

        var stages = EvaluateGate(StableNpmStagingGate, parameters);
        var stableSteps = EvaluateGate(StableNpmWorkGate, parameters);

        Assert.Equal(stagesNpmArtifacts, stages);
        Assert.Equal(runsStableNpmSteps, stableSteps);

        // The empty-placeholder gate must stay the exact negation of the staging gate, otherwise a
        // release either stages nothing and fails on missing artifacts, or double-declares them.
        Assert.Equal(!stages, EvaluateGate(placeholderGate, parameters));

        // Mirror and smoke steps live inside the stable gate, so their effective behavior is the
        // gate combined with the step condition.
        Assert.Equal(seedsMirror, stableSteps && EvaluateCondition(InternalMirrorCondition, parameters));
        Assert.Equal(runsPublicSmoke, stableSteps && EvaluateCondition(PublicNpmSmokeCondition, parameters));

        // Comment 5 regression guard: the public install smoke never depends on mirror seeding.
        if (stableSteps && mirrorAction == "skip")
        {
            Assert.True(EvaluateCondition(PublicNpmSmokeCondition, parameters));
        }
    }

    private static bool EvaluateGate(string gate, IReadOnlyDictionary<string, string> parameters)
    {
        const string prefix = "- ${{ if ";
        const string suffix = " }}:";

        Assert.StartsWith(prefix, gate, StringComparison.Ordinal);
        Assert.EndsWith(suffix, gate, StringComparison.Ordinal);

        return AzurePipelinesExpression.Evaluate(gate[prefix.Length..^suffix.Length], parameters);
    }

    private static bool EvaluateCondition(string condition, IReadOnlyDictionary<string, string> parameters)
    {
        const string prefix = "condition: ";

        Assert.StartsWith(prefix, condition, StringComparison.Ordinal);

        return AzurePipelinesExpression.Evaluate(condition[prefix.Length..], parameters);
    }

    [Fact]
    public async Task NpmLatestDistTagDowngradeGuardHasNoOverrideParameter()
    {
        var pipeline = await ReadRepoFileAsync("eng/pipelines/release-publish-nuget.yml");
        var spec = await ReadRepoFileAsync("docs/specs/npm-cli-package.md");

        Assert.DoesNotContain("AllowNpmLatestDistTagMove", pipeline);
        Assert.DoesNotContain("AllowNpmLatestDistTagMove", spec);
        Assert.DoesNotContain("skipping npm latest dist-tag downgrade guard", pipeline);
        Assert.Contains("Publishing $($pointerPackage.Spec) would move the npm latest dist-tag backward", pipeline);
    }

    [Fact]
    public async Task UsesRequiredNpmEsrpOwnersAndApprover()
    {
        var commonVariables = await ReadRepoFileAsync("eng/pipelines/common-variables.yml");
        var pipeline = await ReadRepoFileAsync("eng/pipelines/release-publish-nuget.yml");

        Assert.DoesNotContain("NPM_PUBLISH_REQUIRED_OWNERS", commonVariables);
        Assert.DoesNotContain("NPM_PUBLISH_DEFAULT_APPROVER", commonVariables);
        Assert.DoesNotContain("NPM_PUBLISH_REQUIRED_APPROVERS", commonVariables);
        Assert.Contains("- name: NPM_PUBLISH_REQUIRED_OWNERS", pipeline);
        Assert.Equal("joperezr,ankj", FindYamlVariableValue(pipeline, "NPM_PUBLISH_REQUIRED_OWNERS"));
        Assert.Contains("displayName: '[Advanced] npm ESRP owner (single Microsoft alias or email; must be joperezr or ankj)'", pipeline);
        Assert.Contains("displayName: '[Advanced] npm ESRP approver (single Microsoft alias or email; must differ from the owner)'", pipeline);

        AssertOwnerDefaultIsSingleRequiredAlias(
            FindYamlVariableValue(pipeline, "NPM_PUBLISH_REQUIRED_OWNERS"),
            FindYamlParameterDefault(pipeline, "NpmPublishOwners"),
            "NpmPublishOwners");
        Assert.Equal("adamratzman", FindYamlParameterDefault(pipeline, "NpmPublishApprovers"));

        Assert.Contains("$requiredNpmOwnersValue = $env:NPM_PUBLISH_REQUIRED_OWNERS", pipeline);
        Assert.DoesNotContain("NPM_PUBLISH_DEFAULT_APPROVER", pipeline);
        Assert.DoesNotContain("NPM_PUBLISH_REQUIRED_APPROVERS", pipeline);
        Assert.DoesNotContain("requiredNpmApprovers", pipeline);
        Assert.Contains("owners: '$(NpmPublishOwnersEffective)'", pipeline);
        Assert.Contains("approvers: '$(NpmPublishApproversEffective)'", pipeline);
        Assert.Contains("NpmPublishOwners and NpmPublishApprovers must not contain the same alias(es)", pipeline);
    }

    [Fact]
    public async Task NpmEsrpOwnersRequireAnyConfiguredOwnerAlias()
    {
        var pipeline = await ReadRepoFileAsync("eng/pipelines/release-publish-nuget.yml");

        Assert.Contains("Assert-SingleNpmReleaseAlias $normalizedOwners 'NpmPublishOwners'", pipeline);
        Assert.Contains("Assert-ContainsAnyRequiredNpmOwnerAlias $normalizedOwners $requiredNpmOwners 'NpmPublishOwners'", pipeline);
        Assert.DoesNotContain("Assert-ContainsRequiredNpmAliases $normalizedOwners $requiredNpmOwners 'NpmPublishOwners'", pipeline);
        Assert.Contains("Assert-SingleNpmReleaseAlias $normalizedApprovers 'NpmPublishApprovers'", pipeline);
        Assert.DoesNotContain("Assert-ContainsRequiredNpmAliases $normalizedApprovers", pipeline);
        Assert.DoesNotContain("NpmPublishOwners not provided; using NPM_PUBLISH_REQUIRED_OWNERS.", pipeline);
        Assert.DoesNotContain("NpmPublishApprovers not provided; using NPM_PUBLISH_DEFAULT_APPROVER.", pipeline);
    }

    [Fact]
    public async Task ForwardsNpmOwnerAndApproverParametersAsEnvironmentVariables()
    {
        var pipeline = await ReadRepoFileAsync("eng/pipelines/release-publish-nuget.yml");

        // The queue-time owner/approver values must reach the validation script as environment
        // variables (data) rather than being interpolated into the inline PowerShell source, where
        // a hostile value could break out of the quoted literal. Keep the template expression inside
        // a string scalar; using the raw expression makes Azure Pipelines preserve expression-object
        // typing and fail release-job expansion with "Unable to convert from Object to String."
        Assert.Contains("NPM_PUBLISH_OWNERS: '${{ parameters.NpmPublishOwners }}'", pipeline);
        Assert.Contains("NPM_PUBLISH_APPROVERS: '${{ parameters.NpmPublishApprovers }}'", pipeline);
        Assert.Contains("$owners = $env:NPM_PUBLISH_OWNERS", pipeline);
        Assert.Contains("$approvers = $env:NPM_PUBLISH_APPROVERS", pipeline);
        Assert.DoesNotContain("NPM_PUBLISH_OWNERS: ${{ parameters.NpmPublishOwners }}", pipeline);
        Assert.DoesNotContain("NPM_PUBLISH_APPROVERS: ${{ parameters.NpmPublishApprovers }}", pipeline);
        Assert.DoesNotContain("$owners = \"${{ parameters.NpmPublishOwners }}\"", pipeline);
        Assert.DoesNotContain("$approvers = \"${{ parameters.NpmPublishApprovers }}\"", pipeline);
    }

    [Fact]
    public async Task ComputesInstallerOnlyModeInsidePowerShell()
    {
        var pipeline = await ReadRepoFileAsync("eng/pipelines/release-publish-nuget.yml");

        // Azure Pipelines reports the start of the `powershell: |` scalar when an embedded
        // template expression evaluates to a non-string object. Keep the composed boolean
        // calculation in PowerShell and substitute only the primitive parameter values.
        Assert.DoesNotContain("Installer-only mode: ${{ and(", pipeline);
        var installerOnlyModeBlocks = System.Text.RegularExpressions.Regex.Matches(
            pipeline,
            @"\$installerOnlyMode = \((?<body>.*?)\)",
            System.Text.RegularExpressions.RegexOptions.Singleline);
        Assert.Equal(2, installerOnlyModeBlocks.Count);
        Assert.All(
            installerOnlyModeBlocks.Cast<System.Text.RegularExpressions.Match>(),
            block => Assert.Contains(
                "\"${{ parameters.NpmInternalMirrorAction }}\" -ne \"only\"",
                block.Groups["body"].Value,
                StringComparison.Ordinal));
        Assert.Equal(
            2,
            System.Text.RegularExpressions.Regex.Matches(
                pipeline,
                System.Text.RegularExpressions.Regex.Escape(
                    "Write-Host \"Installer-only mode: $installerOnlyMode\"")).Count);
    }

    [Fact]
    public async Task DoesNotUseWildcardTemplateParameterExpressionLiteral()
    {
        var pipeline = await ReadRepoFileAsync("eng/pipelines/release-publish-nuget.yml");

        // Azure Pipelines expands template expressions inside block scalars even when the text is
        // inside a PowerShell comment. The literal wildcard expression evaluates to the parameters
        // object, which fails release-job parsing with "Unable to convert from Object to String."
        Assert.DoesNotContain("${{ parameters.* }}", pipeline);
    }

    [Fact]
    public async Task NpmPublishOwnerAndApproverParametersHaveWorkingDefaults()
    {
        var pipeline = await ReadRepoFileAsync("eng/pipelines/release-publish-nuget.yml");

        // Defaults let an unattended queue submission pass validation without operator input:
        // the owner is a single required owner alias, the approver is a single distinct alias, and
        // the per-run override parameters are marked advanced.
        Assert.Contains("- name: NpmPublishOwners", pipeline);
        Assert.Contains("default: 'joperezr'", pipeline);
        Assert.Contains("- name: NpmPublishApprovers", pipeline);
        Assert.Contains("default: 'adamratzman'", pipeline);
        Assert.Contains("[Advanced] npm ESRP owner", pipeline);
        Assert.Contains("[Advanced] npm ESRP approver", pipeline);
        Assert.Contains("[Advanced] Minutes to wait between npm RID and pointer package submissions", pipeline);
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
        Assert.Contains(
            "Registry validation still runs unless this run neither publishes npm packages nor sets NpmInternalMirrorAction=only.",
            pipeline);

        // The public smoke test must not be gated on the internal mirror action. A run that
        // publishes npm packages has to prove the package installs from registry.npmjs.org before
        // channel promotion, even when the operator opted out of internal mirror seeding.
        Assert.Contains(
            PublicNpmSmokeCondition,
            ExtractYamlStep(pipeline, "displayName: 'Validate Published npm Package from Registry'"),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task NpmMirrorSeedingPinsLocalPrefixBeforeEveryWorkingDirectoryChange()
    {
        var pipeline = await ReadRepoFileAsync("eng/pipelines/release-publish-nuget.yml");

        // npm resolves its project config by walking up from the working directory to the nearest
        // package.json/node_modules and reading that directory's .npmrc. --userconfig/--globalconfig
        // do not cover that layer, so an ancestor .npmrc on the release agent could inject a scoped
        // registry or credentials and make the "anonymous" verification prove nothing. Both
        // directories the seed script cds into must be pinned before the Push-Location.
        var markerIndex = FindRequiredText(pipeline, "function Set-NpmLocalPrefixMarker {");
        var seedPinIndex = FindRequiredText(pipeline, "Set-NpmLocalPrefixMarker -Directory $seedDirectory");
        var anonymousPinIndex = FindRequiredText(pipeline, "Set-NpmLocalPrefixMarker -Directory $anonymousDirectory");
        var seedPushIndex = FindRequiredText(pipeline, "Push-Location $seedDirectory");
        var anonymousPushIndex = FindRequiredText(pipeline, "Push-Location $anonymousDirectory");

        Assert.True(markerIndex < seedPinIndex);
        Assert.True(seedPinIndex < seedPushIndex);
        Assert.True(anonymousPinIndex < anonymousPushIndex);

        // The marker must match the CLI's PinNpmLocalPrefixAsync so both isolation paths stay
        // recognizably the same mechanism.
        Assert.Contains(
            """'{"name":"aspire-npm-isolated","version":"0.0.0","private":true}'""",
            pipeline);

        // Every directory the seed script enters has to be pinned; a new Push-Location without a
        // matching marker would silently reopen the ancestor-.npmrc hole.
        var pushLocations = System.Text.RegularExpressions.Regex.Matches(
            pipeline[FindRequiredText(pipeline, "$packageName = '@microsoft/aspire-cli'")..],
            @"Push-Location \$(\w+)");
        Assert.Equal(
            new[] { "seedDirectory", "anonymousDirectory" },
            pushLocations.Select(match => match.Groups[1].Value).Distinct().ToArray());
    }

    [Fact]
    public async Task SeedsAndAnonymouslyValidatesNpmInternalMirrorBeforePromotion()
    {
        var pipeline = await ReadRepoFileAsync("eng/pipelines/release-publish-nuget.yml");

        var stableRealGate = StableNpmWorkGate;
        const string mirrorCondition = InternalMirrorCondition;
        const string anonymousViewCommand =
            "$viewOutput = npm view \"$packageName@latest\" version --prefer-online --registry=$internalRegistry --userconfig=$anonymousNpmrc --globalconfig=$anonymousGlobalNpmrc --cache=$attemptCache --json=false --loglevel=warn 2>&1";
        var stableRealGateIndex = FindRequiredText(pipeline, stableRealGate);
        var stableRealGateEndIndex = FindYamlIndentedBlockEnd(pipeline, stableRealGate);
        var publicValidationIndex = FindRequiredText(
            pipeline,
            "displayName: 'Validate Published npm Package from Registry'");
        var prepareAuthenticationIndex = FindRequiredText(
            pipeline,
            "displayName: 'Prepare npm Internal Mirror Authentication'");
        var authenticateTaskIndex = FindRequiredText(pipeline, "task: npmAuthenticate@0");
        var authenticateIndex = FindRequiredText(
            pipeline,
            "displayName: 'Authenticate to npm Internal Mirror'");
        var seedScriptIndex = FindRequiredText(pipeline, "$packageName = '@microsoft/aspire-cli'");
        var anonymousViewIndex = FindRequiredText(pipeline, anonymousViewCommand);
        var seedIndex = FindRequiredText(
            pipeline,
            "displayName: 'Seed and Validate npm Internal Mirror'");
        var promotionIndex = FindRequiredText(
            pipeline,
            "# ===== PROMOTE TO CHANNEL =====");

        Assert.Equal(promotionIndex, stableRealGateEndIndex);
        Assert.True(stableRealGateIndex < publicValidationIndex);
        Assert.True(publicValidationIndex < prepareAuthenticationIndex);
        Assert.True(prepareAuthenticationIndex < authenticateTaskIndex);
        Assert.True(authenticateTaskIndex < authenticateIndex);
        Assert.True(authenticateIndex < seedScriptIndex);
        Assert.True(seedScriptIndex < anonymousViewIndex);
        Assert.True(anonymousViewIndex < seedIndex);
        Assert.True(seedIndex < promotionIndex);
        Assert.Equal(
            3,
            System.Text.RegularExpressions.Regex.Matches(
                pipeline,
                System.Text.RegularExpressions.Regex.Escape(mirrorCondition)).Count);

        foreach (var displayName in new[]
        {
            "displayName: 'Prepare npm Internal Mirror Authentication'",
            "displayName: 'Authenticate to npm Internal Mirror'",
            "displayName: 'Seed and Validate npm Internal Mirror'"
        })
        {
            var step = ExtractYamlStep(pipeline, displayName);
            Assert.Contains(mirrorCondition, step, StringComparison.Ordinal);
        }

        var seedScript = ExtractSection(
            pipeline,
            "function Invoke-NpmPack",
            "displayName: 'Seed and Validate npm Internal Mirror'");

        Assert.Contains(
            "workingFile: '$(Agent.TempDirectory)\\aspire-cli-internal-mirror.npmrc'",
            pipeline,
            StringComparison.Ordinal);
        Assert.Contains(
            "$authenticatedNpmrc = \"$(Agent.TempDirectory)\\aspire-cli-internal-mirror.npmrc\"",
            seedScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "$anonymousNpmrc = Join-Path $workRoot 'anonymous.npmrc'",
            seedScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "$authenticatedCache = Join-Path $workRoot 'authenticated-cache'",
            seedScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "$anonymousCache = Join-Path $workRoot 'anonymous-cache'",
            seedScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "$stagedPointerPackagePath = \"$(Pipeline.Workspace)/npm/pointer-package\"",
            seedScript,
            StringComparison.Ordinal);

        var protectedTryIndex = FindRequiredText(seedScript, "try {");
        var workRootDeleteIndex = FindRequiredText(seedScript, "if (Test-Path -LiteralPath $workRoot)");
        var seedDirectoryCreateIndex = FindRequiredText(
            seedScript,
            "New-Item -ItemType Directory -Path $seedDirectory -Force | Out-Null");
        var authenticatedCacheCreateIndex = FindRequiredText(
            seedScript,
            "New-Item -ItemType Directory -Path $authenticatedCache -Force | Out-Null");
        var anonymousCacheCreateIndex = FindRequiredText(
            seedScript,
            "New-Item -ItemType Directory -Path $anonymousCache -Force | Out-Null");
        var anonymousNpmrcWriteIndex = FindRequiredText(
            seedScript,
            "Set-Content -LiteralPath $anonymousNpmrc -Encoding utf8NoBOM");

        Assert.True(protectedTryIndex < workRootDeleteIndex);
        Assert.True(protectedTryIndex < seedDirectoryCreateIndex);
        Assert.True(protectedTryIndex < authenticatedCacheCreateIndex);
        Assert.True(protectedTryIndex < anonymousCacheCreateIndex);
        Assert.True(protectedTryIndex < anonymousNpmrcWriteIndex);

        var stagedDependencyExtractionIndex = FindRequiredText(
            seedScript,
            "foreach ($dependency in $stagedOptionalDependencies.PSObject.Properties)");
        var authenticatedPackIndex = FindRequiredText(
            seedScript,
            "Invoke-AuthenticatedNpmPack -PackageSpec $authenticatedPackageSpec");
        var anonymousViewScriptIndex = FindRequiredText(seedScript, anonymousViewCommand);
        var anonymousPointerPackIndex = FindRequiredText(
            seedScript,
            "Invoke-AnonymousNpmPack -PackageSpec \"$packageName@$mirroredVersion\"");
        var anonymousDependencyExtractionIndex = FindRequiredText(
            seedScript,
            "foreach ($dependency in $anonymousOptionalDependencies.PSObject.Properties)");
        var anonymousDependencyPackIndex = FindRequiredText(
            seedScript,
            "Invoke-AnonymousNpmPack -PackageSpec $dependencySpec");

        Assert.True(stagedDependencyExtractionIndex < authenticatedPackIndex);
        Assert.True(authenticatedPackIndex < anonymousViewScriptIndex);
        Assert.True(anonymousViewScriptIndex < anonymousPointerPackIndex);
        Assert.True(anonymousPointerPackIndex < anonymousDependencyExtractionIndex);
        Assert.True(anonymousDependencyExtractionIndex < anonymousDependencyPackIndex);
        Assert.Equal(
            anonymousViewScriptIndex,
            seedScript.LastIndexOf(anonymousViewCommand, StringComparison.Ordinal));

        Assert.DoesNotContain("npm install --ignore-scripts", seedScript, StringComparison.Ordinal);
        Assert.Contains(
            "npm pack $PackageSpec --ignore-scripts --pack-destination $Destination",
            seedScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "[version]$mirroredVersion -ge [version]$packageVersion",
            seedScript,
            StringComparison.Ordinal);
        Assert.Contains("$maxAttempts = 10", seedScript, StringComparison.Ordinal);
        Assert.Contains("Start-Sleep -Seconds 30", seedScript, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NpmMirrorValidationIsExplicitlyGatedWhenBothPublishFlagsAreSkipped()
    {
        var pipeline = await ReadRepoFileAsync("eng/pipelines/release-publish-nuget.yml");

        var nodeSetupGate = StableNpmStagingGate;
        var stableRealGate = StableNpmWorkGate;
        var bothSkippedMessageIndex = FindRequiredText(
            pipeline,
            "displayName: 'Skip npm Packages (flagged)'");
        var nodeSetupGateIndex = FindRequiredText(pipeline, nodeSetupGate);
        var stableRealGateIndex = FindRequiredText(pipeline, stableRealGate);
        var publicValidationIndex = FindRequiredText(
            pipeline,
            "displayName: 'Validate Published npm Package from Registry'");
        var mirrorValidationIndex = FindRequiredText(
            pipeline,
            "displayName: 'Seed and Validate npm Internal Mirror'");

        Assert.True(nodeSetupGateIndex < bothSkippedMessageIndex);
        Assert.True(bothSkippedMessageIndex < stableRealGateIndex);
        Assert.True(stableRealGateIndex < publicValidationIndex);
        Assert.True(publicValidationIndex < mirrorValidationIndex);
        Assert.Contains(
            "##vso[task.setvariable variable=NpmPublishedPointerVersion]$packageVersion",
            pipeline,
            StringComparison.Ordinal);
        Assert.Contains(
            "$packageVersion = '$(NpmPublishedPointerVersion)'",
            pipeline,
            StringComparison.Ordinal);

        Assert.Contains(
            "displayName: 'Skip npm Internal Mirror Validation (flagged)'",
            pipeline,
            StringComparison.Ordinal);
        Assert.Contains(
            "condition: and(succeeded(), eq('${{ parameters.NpmInternalMirrorAction }}', 'skip'))",
            ExtractYamlStep(pipeline, "displayName: 'Skip npm Internal Mirror Validation (flagged)'"),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task StableNpmMirrorRerunStagesPointerArtifactAndValidationSummaries()
    {
        var pipeline = await ReadRepoFileAsync("eng/pipelines/release-publish-nuget.yml");
        var prepareStage = ExtractSection(
            pipeline,
            "displayName: 'Download and Re-publish Artifacts'",
            "displayName: 'Validate, Publish, and Promote'");
        var stableNpmGate = StableNpmStagingGate;

        var downloadGateIndex = FindRequiredText(prepareStage, stableNpmGate);
        var downloadIndex = FindRequiredText(
            prepareStage,
            "displayName: 'Download npm packages from Source Build'");
        var prepareGateIndex = prepareStage.IndexOf(
            stableNpmGate,
            downloadGateIndex + stableNpmGate.Length,
            StringComparison.Ordinal);
        var prepareIndex = FindRequiredText(
            prepareStage,
            "displayName: 'Prepare npm Artifacts for Publishing'");

        Assert.True(downloadGateIndex < downloadIndex);
        Assert.True(prepareGateIndex > downloadIndex);
        Assert.True(prepareGateIndex < prepareIndex);
        Assert.Contains(
            "- ${{ if and(eq(parameters.SkipNpmRidPublish, true), eq(parameters.SkipNpmPointerPublish, true), or(eq(parameters.DryRun, true), eq(parameters.IsPrerelease, true), ne(parameters.NpmInternalMirrorAction, 'only'))) }}:",
            prepareStage,
            StringComparison.Ordinal);

        var stagedValidationIndex = FindRequiredText(
            pipeline,
            "displayName: 'Verify Staged npm Package Versions'");
        var stableStagedValidationGateIndex = pipeline.LastIndexOf(
            stableNpmGate,
            stagedValidationIndex,
            StringComparison.Ordinal);
        var publicationOnlyGateIndex = pipeline.LastIndexOf(
            "- ${{ if or(eq(parameters.SkipNpmRidPublish, false), eq(parameters.SkipNpmPointerPublish, false)) }}:",
            stagedValidationIndex,
            StringComparison.Ordinal);

        Assert.True(stableStagedValidationGateIndex > publicationOnlyGateIndex);
    }

    [Fact]
    public async Task NpmMirrorAnonymousValidationUsesOnlyFreshCredentialFreeConfiguration()
    {
        var pipeline = await ReadRepoFileAsync("eng/pipelines/release-publish-nuget.yml");
        var seedScript = ExtractSection(
            pipeline,
            "function Invoke-NpmPack",
            "displayName: 'Seed and Validate npm Internal Mirror'");

        Assert.Contains(
            "$anonymousGlobalNpmrc = Join-Path $workRoot 'anonymous-global.npmrc'",
            seedScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "$anonymousDirectory = Join-Path $workRoot 'anonymous'",
            seedScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "$env:NPM_CONFIG_GLOBALCONFIG = $anonymousGlobalNpmrc",
            seedScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "npm view \"$packageName@latest\" version --prefer-online --registry=$internalRegistry --userconfig=$anonymousNpmrc --globalconfig=$anonymousGlobalNpmrc --cache=$attemptCache --json=false",
            seedScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "Remove-Item Env:NPM_CONFIG_GLOBALCONFIG -ErrorAction SilentlyContinue",
            seedScript,
            StringComparison.Ordinal);

        var anonymousDirectoryCreateIndex = FindRequiredText(
            seedScript,
            "New-Item -ItemType Directory -Path $anonymousDirectory -Force | Out-Null");
        var anonymousGlobalConfigCreateIndex = FindRequiredText(
            seedScript,
            "New-Item -ItemType File -Path $anonymousGlobalNpmrc -Force | Out-Null");
        var anonymousLocationIndex = FindRequiredText(
            seedScript,
            "Push-Location $anonymousDirectory");
        var anonymousViewIndex = FindRequiredText(
            seedScript,
            "npm view \"$packageName@latest\" version --prefer-online --registry=$internalRegistry --userconfig=$anonymousNpmrc --globalconfig=$anonymousGlobalNpmrc --cache=$attemptCache --json=false");

        Assert.True(anonymousDirectoryCreateIndex < anonymousLocationIndex);
        Assert.True(anonymousGlobalConfigCreateIndex < anonymousLocationIndex);
        Assert.True(anonymousLocationIndex < anonymousViewIndex);
    }

    [Fact]
    public async Task NpmMirrorSeedingAndAnonymousValidationDownloadEveryTarballWithoutScripts()
    {
        var pipeline = await ReadRepoFileAsync("eng/pipelines/release-publish-nuget.yml");
        var seedScript = ExtractSection(
            pipeline,
            "function Invoke-NpmPack",
            "displayName: 'Seed and Validate npm Internal Mirror'");

        Assert.DoesNotContain("npm install --ignore-scripts", seedScript, StringComparison.Ordinal);
        Assert.Contains(
            "foreach ($dependency in $stagedOptionalDependencies.PSObject.Properties)",
            seedScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "Invoke-AuthenticatedNpmPack -PackageSpec $authenticatedPackageSpec",
            seedScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "Invoke-AnonymousNpmPack -PackageSpec \"$packageName@$mirroredVersion\"",
            seedScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "foreach ($dependency in $anonymousOptionalDependencies.PSObject.Properties)",
            seedScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "Invoke-AnonymousNpmPack -PackageSpec $dependencySpec",
            seedScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"$($anonymousPackageJson.name)\" -eq $packageName",
            seedScript,
            StringComparison.Ordinal);
        Assert.Equal(
            2,
            System.Text.RegularExpressions.Regex.Matches(
                seedScript,
                System.Text.RegularExpressions.Regex.Escape(
                    "$dependencyVersion -notmatch '^\\d+\\.\\d+\\.\\d+$'")).Count);
        Assert.Contains(
            "npm pack $PackageSpec --ignore-scripts --pack-destination $Destination",
            seedScript,
            StringComparison.Ordinal);

        var authenticatedLocationIndex = FindRequiredText(seedScript, "Push-Location $seedDirectory");
        var authenticatedPackIndex = FindRequiredText(
            seedScript,
            "Invoke-AuthenticatedNpmPack -PackageSpec $authenticatedPackageSpec");
        Assert.True(authenticatedLocationIndex < authenticatedPackIndex);
    }

    [Fact]
    public async Task NpmMirrorCleanupDoesNotMaskSuccessfulValidation()
    {
        var pipeline = await ReadRepoFileAsync("eng/pipelines/release-publish-nuget.yml");
        var seedScript = ExtractSection(
            pipeline,
            "$packageName = '@microsoft/aspire-cli'",
            "displayName: 'Seed and Validate npm Internal Mirror'");

        Assert.Contains(
            "Best-effort cleanup of '$workRoot' failed:",
            seedScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "Remove-Item -LiteralPath $workRoot -Recurse -Force -ErrorAction Stop",
            seedScript,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task NpmMirrorOnlyRerunDocumentationSkipsEveryUnrelatedReleaseAction()
    {
        var releaseProcess = await ReadRepoFileAsync("docs/release-process.md");
        var mirrorRecovery = ExtractSection(
            releaseProcess,
            "### npm internal mirror seeding or anonymous validation fails",
            "### Tag already exists but points to different commit");

        foreach (var parameterName in new[]
        {
            "SkipNuGetPublish",
            "SkipNpmRidPublish",
            "SkipNpmPointerPublish",
            "SkipChannelPromotion",
            "SkipWinGetPublish",
            "SkipGitHubTasks",
            "SkipReleaseAssets",
            "SkipHomebrewValidation",
            "SkipNixPackageUpdate",
            "SkipVSCodeExtensionPublish"
        })
        {
            Assert.Contains($"{parameterName}=true", mirrorRecovery, StringComparison.Ordinal);
        }

        // The mirror-only rerun is expressed by the dedicated action, not by an extra skip flag.
        Assert.Contains("NpmInternalMirrorAction=only", mirrorRecovery, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NpmMirrorMacroGuardsUseLiteralAssignments()
    {
        var pipeline = await ReadRepoFileAsync("eng/pipelines/release-publish-nuget.yml");

        Assert.Equal(
            2,
            System.Text.RegularExpressions.Regex.Matches(
                pipeline,
                @"\$validatedVersion = '\$\(NpmValidatedExpectedVersion\)'").Count);
        Assert.Contains("$packageVersion = '$(NpmPublishedPointerVersion)'", pipeline);
        Assert.Contains("$internalRegistry = '$(NPM_REGISTRY)'", pipeline);
        Assert.DoesNotContain("$validatedVersion = \"$(NpmValidatedExpectedVersion)\"", pipeline);
        Assert.DoesNotContain("$packageVersion = \"$(NpmPublishedPointerVersion)\"", pipeline);
        Assert.DoesNotContain("$internalRegistry = \"$(NPM_REGISTRY)\"", pipeline);
    }

    [Fact]
    public async Task NpmPublishSkipMessageAndSummaryDistinguishMirrorValidation()
    {
        var pipeline = await ReadRepoFileAsync("eng/pipelines/release-publish-nuget.yml");

        Assert.DoesNotContain("=== Skipping npm Publishing", pipeline);
        Assert.Contains(
            "=== Skipping npm Package Publishing (SkipNpmRidPublish=true and SkipNpmPointerPublish=true); internal mirror handling follows NpmInternalMirrorAction=${{ parameters.NpmInternalMirrorAction }} ===",
            pipeline);
        Assert.DoesNotContain(
            "Registry validation will still install the selected source build's pointer package version from npm.",
            pipeline);
        Assert.Contains(
            "Registry validation still runs unless this run neither publishes npm packages nor sets NpmInternalMirrorAction=only.",
            pipeline);
        Assert.Contains(
            "Write-Host \" (PARTIAL - pointer publish skipped; registry smoke still ran)\"",
            pipeline);

        // Reaching the pointer-skip summary branch means RID publishing ran, so the public smoke
        // test ran too. A "registry smoke skipped" summary there would contradict the step's
        // condition, which never skips the smoke test for a run that publishes npm packages.
        Assert.DoesNotContain(
            "Write-Host \" (PARTIAL - pointer publish skipped; registry smoke skipped)\"",
            pipeline);
        var partialPointerSummaryIndex = FindRequiredText(
            pipeline,
            "} elseif (\"${{ parameters.SkipNpmPointerPublish }}\" -eq \"true\") {");
        var ranSmokeSummaryIndex = FindRequiredText(
            pipeline,
            "Write-Host \" (PARTIAL - pointer publish skipped; registry smoke still ran)\"");
        var mirrorSummaryConditionIndex = pipeline.IndexOf(
            "} elseif (\"${{ parameters.NpmInternalMirrorAction }}\" -eq \"skip\") {",
            partialPointerSummaryIndex,
            StringComparison.Ordinal);
        Assert.True(partialPointerSummaryIndex < ranSmokeSummaryIndex);
        Assert.True(ranSmokeSummaryIndex < mirrorSummaryConditionIndex);
        Assert.Contains("║ npm Mirror:     ${{ parameters.NpmInternalMirrorAction }}", pipeline);

        // 'auto' with both publish skips set is a release that did no npm work at all. The summary
        // must not report the mirror as EXECUTED for those runs.
        Assert.Contains(
            "Write-Host \" (NOT RUN - this release published no npm packages)\"",
            pipeline);
    }

    [Fact]
    public async Task PublicNpmRegistrySmokeTestIsNotGatedOnMirrorValidationAlone()
    {
        var pipeline = await ReadRepoFileAsync("eng/pipelines/release-publish-nuget.yml");
        var smokeStep = ExtractYamlStep(
            pipeline,
            "displayName: 'Validate Published npm Package from Registry'");

        // Regression guard: gating this pre-existing step on the internal mirror flag let a run
        // publish @microsoft/aspire-cli to npm and promote the channel without ever proving
        // `npm install -g @microsoft/aspire-cli@<version>` works.
        Assert.Contains(PublicNpmSmokeCondition, smokeStep, StringComparison.Ordinal);

        Assert.DoesNotContain(InternalMirrorCondition, smokeStep);
        Assert.DoesNotContain("NpmInternalMirrorAction }}', 'skip'", smokeStep);

        // The mirror seed step reads NpmPublishedPointerVersion from the smoke test, so the smoke
        // test must run in every configuration where the mirror steps run.
        var mirrorStep = ExtractYamlStep(
            pipeline,
            "displayName: 'Seed and Validate npm Internal Mirror'");
        Assert.Contains(
            InternalMirrorCondition,
            mirrorStep,
            StringComparison.Ordinal);
        Assert.Contains(
            "$packageVersion = '$(NpmPublishedPointerVersion)'",
            mirrorStep,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task PrepareNpmCliPackagesScriptIsBash32Compatible()
    {
        var template = await ReadRepoFileAsync("eng/pipelines/templates/prepare-npm-cli-packages.yml");

        // macOS AzDO runners execute bash@3 tasks with /bin/bash which is still
        // Bash 3.2 on every shipping macOS release. These constructs are Bash 4+
        // and silently break the install/uninstall smoke that gates the npm release.
        // See dry-run build 2987449 where `shopt: globstar: invalid shell option name`
        // killed `🟣Locate pointer and RID tarballs` on macOS.
        Assert.DoesNotContain("shopt -s globstar", template);
        Assert.DoesNotContain("mapfile ", template);
        Assert.DoesNotContain("readarray ", template);
        // declare -A (associative arrays) is also Bash 4+.
        Assert.DoesNotContain("declare -A", template);
    }

    [Fact]
    public async Task PrepareNpmCliPackagesScriptInstallsOfflineWithTimeout()
    {
        var template = await ReadRepoFileAsync("eng/pipelines/templates/prepare-npm-cli-packages.yml");

        // The pointer package declares every supported RID as an optionalDependency
        // pinned to the just-built version, which does not yet exist in the public
        // npm registry. Even with --omit=optional, npm still resolves optional dep
        // metadata while building the dep tree. In 1ES Linux/Windows pools the
        // registry call is blackholed by network isolation rules and each of 7
        // lookups burns the full fetch-timeout — that's the 9-minute pointer install
        // hang observed in dry-run build 2987581. Pair --omit=optional with --offline
        // (no registry traffic at all) and cap any accidental fetch with a short
        // --fetch-timeout. NPM_CONFIG_CACHE points at a fresh empty directory so
        // --offline cannot reuse a poisoned cache.
        Assert.Contains("--offline", template);
        Assert.Contains("--fetch-timeout=", template);
    }

    [Fact]
    public async Task PointerPublishPreflightsRidPackagesAreOnRegistry()
    {
        var pipeline = await ReadRepoFileAsync("eng/pipelines/release-publish-nuget.yml");

        // The pointer pins each RID package via optionalDependencies. If any
        // RID dep is missing on npm at pointer-publish time (operator set
        // SkipNpmRidPublish=true; only some RIDs landed in an earlier attempt;
        // ESRP partial failure), end-user `npm install -g @microsoft/aspire-cli`
        // succeeds but the launcher throws "The Aspire CLI native package '…'
        // was not installed" on first invocation. The post-publish smoke only
        // covers the publish-pool's own RID, so missing other-RID tarballs
        // reach customers invisibly without this preflight.
        Assert.Contains("Verify npm RID Packages Present Before Pointer Publish", pipeline);
        Assert.Contains("Refusing to publish pointer package", pipeline);

        var preflightIndex = pipeline.IndexOf("Verify npm RID Packages Present Before Pointer Publish", StringComparison.Ordinal);
        Assert.True(preflightIndex > 0);

        // The preflight must precede the actual pointer publish so it can gate
        // submission.
        var pointerPublishIndex = pipeline.IndexOf(
            "folderLocation: '$(Pipeline.Workspace)\\npm\\pointer-package'",
            StringComparison.Ordinal);
        Assert.True(pointerPublishIndex > preflightIndex,
            "Preflight RID-check must appear before the pointer-publish step.");
    }

    [Fact]
    public async Task VerifiesStagedNpmPackageVersionsBeforeRidPublish()
    {
        var pipeline = await ReadRepoFileAsync("eng/pipelines/release-publish-nuget.yml");

        // An npm publish is unrevocable. The pointer preflight cross-checks the
        // pointer version against the prepare-stage validated version, but that
        // runs AFTER the 7 RID tarballs are already submitted to ESRP. So every
        // staged RID and pointer tarball's own package.json version must be
        // asserted against NpmValidatedExpectedVersion BEFORE the RID publish, or
        // a wrong-version build that slipped into staging would leak onto the
        // public registry before any version gate fires.
        Assert.Contains("Verify Staged npm Package Versions", pipeline);
        Assert.Contains("does not match the prepare-stage validated version", pipeline);

        var versionCheckIndex = pipeline.IndexOf("Verify Staged npm Package Versions", StringComparison.Ordinal);
        Assert.True(versionCheckIndex > 0);

        // The version check must precede the RID-package publish so it can gate
        // submission to ESRP.
        var ridPublishIndex = pipeline.IndexOf(
            "folderLocation: '$(Pipeline.Workspace)\\npm\\rid-packages'",
            StringComparison.Ordinal);
        Assert.True(ridPublishIndex > versionCheckIndex,
            "Staged-version check must appear before the RID-publish step.");
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
    public async Task AspireVersionCaptureStripsCarriageReturnForWindowsRunner()
    {
        var template = await ReadRepoFileAsync("eng/pipelines/templates/prepare-npm-cli-packages.yml");

        // Regression guard for the CRLF-stripping fix surfaced by opus-4.7 review.
        //
        // On Windows runners the prepare-npm step runs under Git Bash, which
        // launches `aspire.exe` as a Windows console process. System.CommandLine
        // 2.x's VersionOption writes through Console.Out.WriteLine, which
        // terminates lines with Environment.NewLine = "\r\n" on Windows. Bash
        // command substitution `$(...)` strips trailing LF but NOT CR, so the
        // captured variable ends with "\r". The semver capture regex used by
        // the install validation is anchored with `$` (end-of-line), which does
        // not match a literal CR — so without `tr -d '\r'` on the version
        // capture, the entire install validation silently fails on Windows with
        // "##[error]aspire --version reported '' but expected '<version>'".
        //
        // Verified locally: `printf 'X\r\n' | grep -Eo '^X$'` produces NO match.
        //
        // This regressed in commit debf4ebf38 ("Harden npm prepare/publish
        // validation against partial-failure leakage"), which replaced the
        // earlier `tr -d '[:space:]'` form with a `grep -Eo`+`$` form. The dry
        // run on 2987740 did NOT exercise this path because npm publishing was
        // skipped, bypassing the release-pipeline consumer that reads the
        // win-x64 validation summary; the Monday real publish would have hit
        // the bug at the first source-build Windows install validation.
        Assert.Contains("aspire --version 2>&1 | tr -d '\\r'", template);
    }

    [Fact]
    public async Task PointerPreflightExplicitlyPinsRegistryOnSpecLine()
    {
        var pipeline = await ReadRepoFileAsync("eng/pipelines/release-publish-nuget.yml");

        // The preflight that gates the pointer publish runs `npm view $spec ...`
        // (note: `$spec`, not `$packageSpec` — the latter is the post-publish
        // smoke). A separate test asserts the post-publish line is registry-
        // pinned; this one asserts the preflight line is also pinned, so that
        // a future refactor that drops `--registry=https://registry.npmjs.org/`
        // from the preflight call would be caught at PR-time rather than
        // silently letting a stale internal-mirror result decide whether to
        // ship a broken pointer to npmjs.
        Assert.Contains("npm view $spec version --registry=https://registry.npmjs.org/", pipeline);
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
    public async Task ExtensionReleaseInstructionsSkipNonExtensionReleaseLegs()
    {
        var workflow = await ReadRepoFileAsync(".github/workflows/extension-release.yml");
        var instructions = ExtractSection(
            workflow,
            "For an extension-only release, use these parameters:",
            "For a full Aspire release");

        Assert.Contains("| \\`SkipNuGetPublish\\` | \\`true\\` |", instructions);
        Assert.Contains("| \\`SkipNpmRidPublish\\` | \\`true\\` |", instructions);
        Assert.Contains("| \\`SkipNpmPointerPublish\\` | \\`true\\` |", instructions);
        Assert.Contains("| \\`SkipChannelPromotion\\` | \\`true\\` |", instructions);

        // The extension-only recipe must not have to opt out of npm mirror work. Leaving
        // NpmInternalMirrorAction at its 'auto' default already emits no npm steps for a run that
        // publishes no npm packages, so listing a mirror flag here would be a regression.
        Assert.DoesNotContain("NpmInternalMirrorAction", instructions);
        Assert.DoesNotContain("SkipNpmMirrorValidation", instructions);
        Assert.Contains("| \\`SkipWinGetPublish\\` | \\`true\\` |", instructions);
        Assert.Contains("| \\`SkipHomebrewValidation\\` | \\`true\\` |", instructions);
        Assert.Contains("| \\`SkipGitHubTasks\\` | \\`true\\` |", instructions);
        Assert.Contains("| \\`SkipReleaseAssets\\` | \\`true\\` |", instructions);
        Assert.Contains("| \\`SkipNixPackageUpdate\\` | \\`true\\` |", instructions);
        Assert.Contains("| \\`SkipVSCodeExtensionPublish\\` | \\`false\\` |", instructions);
    }

    [Fact]
    public async Task ExtensionReleaseDryRunInstructionsDoNotOverstatePublisherRoleValidation()
    {
        var workflow = await ReadRepoFileAsync(".github/workflows/extension-release.yml");

        Assert.Contains("can acquire an Azure credential and read publisher role assignments", workflow);
        Assert.Contains("Separately confirm the service connection identity is a Contributor", workflow);
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

    private static string ExtractYamlStep(string contents, string displayName)
    {
        var displayNameIndex = FindRequiredText(contents, displayName);
        var displayLineStart = contents.LastIndexOf('\n', displayNameIndex) + 1;
        var displayLineEnd = contents.IndexOf('\n', displayNameIndex);
        var displayIndent = CountLeadingWhitespace(contents[displayLineStart..displayLineEnd]);
        var stepIndent = displayIndent - 2;
        var stepStart = displayLineStart;

        while (stepStart > 0)
        {
            var previousLineEnd = stepStart - 1;
            var previousLineStart = contents.LastIndexOf('\n', previousLineEnd - 1) + 1;
            var line = contents[previousLineStart..previousLineEnd].TrimEnd('\r');
            if (CountLeadingWhitespace(line) == stepIndent &&
                line.TrimStart().StartsWith("- ", StringComparison.Ordinal))
            {
                stepStart = previousLineStart;
                break;
            }

            stepStart = previousLineStart;
        }

        var stepEnd = contents.Length;
        var lineStart = contents.IndexOf('\n', stepStart) + 1;
        while (lineStart > 0 && lineStart < contents.Length)
        {
            var lineEnd = contents.IndexOf('\n', lineStart);
            if (lineEnd < 0)
            {
                lineEnd = contents.Length;
            }

            var line = contents[lineStart..lineEnd].TrimEnd('\r');
            if (line.Trim().Length > 0 &&
                CountLeadingWhitespace(line) <= stepIndent)
            {
                stepEnd = lineStart;
                break;
            }

            lineStart = lineEnd + 1;
        }

        return contents[stepStart..stepEnd];
    }

    private static void AssertBefore(string contents, string text, int boundaryIndex)
    {
        var textIndex = FindRequiredText(contents, text);

        Assert.True(
            textIndex < boundaryIndex,
            $"Expected '{text}' to appear before 'task: 1ES.PublishNuget@1'.");
    }

    private static int FindRequiredText(string contents, string text)
    {
        var index = contents.IndexOf(text, StringComparison.Ordinal);

        Assert.True(index >= 0, $"Expected to find '{text}'.");

        return index;
    }

    private static int FindYamlIndentedBlockEnd(string contents, string marker)
    {
        var markerIndex = FindRequiredText(contents, marker);
        var markerLineStart = contents.LastIndexOf('\n', markerIndex) + 1;
        var markerLineEnd = contents.IndexOf('\n', markerIndex);
        if (markerLineEnd < 0)
        {
            return contents.Length;
        }

        var markerIndent = CountLeadingWhitespace(contents[markerLineStart..markerLineEnd]);
        var lineStart = markerLineEnd + 1;

        while (lineStart < contents.Length)
        {
            var lineEnd = contents.IndexOf('\n', lineStart);
            if (lineEnd < 0)
            {
                lineEnd = contents.Length;
            }

            var line = contents[lineStart..lineEnd].TrimEnd('\r');
            if (line.Trim().Length > 0)
            {
                var indent = CountLeadingWhitespace(line);
                if (indent <= markerIndent)
                {
                    return lineStart + indent;
                }
            }

            lineStart = lineEnd + 1;
        }

        return contents.Length;
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

    private static string FindYamlVariableValue(string contents, string variableName)
        => FindYamlValueAfterMarker(contents, $"- name: {variableName}", "value:");

    private static string FindYamlParameterDefault(string contents, string parameterName)
        => FindYamlValueAfterMarker(contents, $"- name: {parameterName}", "default:");

    private static string FindYamlValueAfterMarker(string contents, string marker, string valueKey)
    {
        var lines = contents.Split('\n');
        var markerLineIndex = Array.FindIndex(lines, line => line.TrimEnd('\r').Trim() == marker);

        Assert.True(markerLineIndex >= 0, $"Expected to find '{marker}'.");

        var markerIndent = CountLeadingWhitespace(lines[markerLineIndex]);
        for (var i = markerLineIndex + 1; i < lines.Length; i++)
        {
            var rawLine = lines[i].TrimEnd('\r');
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var indent = CountLeadingWhitespace(rawLine);
            if (indent == markerIndent && line.StartsWith("- ", StringComparison.Ordinal))
            {
                break;
            }

            if (indent > markerIndent && line.StartsWith(valueKey, StringComparison.Ordinal))
            {
                return TrimYamlQuotes(line[valueKey.Length..].Trim());
            }
        }

        throw new Xunit.Sdk.XunitException($"Expected to find '{valueKey}' after '{marker}'.");
    }

    private static int CountLeadingWhitespace(string value)
    {
        var count = 0;
        while (count < value.Length && char.IsWhiteSpace(value[count]))
        {
            count++;
        }

        return count;
    }

    private static string TrimYamlQuotes(string value)
    {
        if (value.Length >= 2 &&
            ((value[0] == '\'' && value[^1] == '\'') ||
             (value[0] == '"' && value[^1] == '"')))
        {
            return value[1..^1];
        }

        return value;
    }

    private Task<string> ReadRepoFileAsync(string relativePath)
        => File.ReadAllTextAsync(Path.Combine(_repoRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
}
