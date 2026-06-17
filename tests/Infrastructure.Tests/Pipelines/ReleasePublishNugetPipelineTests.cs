// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Xunit;

namespace Infrastructure.Tests;

public sealed class ReleasePublishNugetPipelineTests
{
    private readonly string _repoRoot = RepoRoot.Path;

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
        //     PublishReleaseAssetsJob, HomebrewValidateJob) -> enabled: false
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
    public async Task NpmPublishUsesOnlyRidAndPointerSkipParameters()
    {
        var pipeline = await ReadRepoFileAsync("eng/pipelines/release-publish-nuget.yml");
        var spec = await ReadRepoFileAsync("docs/specs/npm-cli-package.md");

        Assert.DoesNotContain("SkipNpmPublish", pipeline);
        Assert.DoesNotContain("Skip npm Publish", pipeline);
        Assert.DoesNotContain("SkipNpmPublish", spec);
        Assert.Contains("displayName: '[Advanced] Skip npm RID Package Publishing", pipeline);
        Assert.Contains("displayName: '[Advanced] Skip npm Pointer Package Publishing", pipeline);
        Assert.Contains("or(eq(parameters.SkipNpmRidPublish, false), eq(parameters.SkipNpmPointerPublish, false))", pipeline);
        Assert.Contains("and(eq(parameters.SkipNpmRidPublish, true), eq(parameters.SkipNpmPointerPublish, true))", pipeline);
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
        Assert.Contains("$installerOnlyMode = (", pipeline);
        Assert.Contains("Write-Host \"Installer-only mode: $installerOnlyMode\"", pipeline);
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
        Assert.Contains("Registry validation will still install the selected source build's pointer package version from npm.", pipeline);
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
