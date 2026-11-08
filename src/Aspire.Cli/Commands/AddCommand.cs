// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using System.CommandLine;
using System.Globalization;
using System.Xml.Linq;
using Aspire.Cli.Configuration;
using Aspire.Cli.DotNet;
using Aspire.Cli.Interaction;
using Aspire.Cli.NuGet;
using Aspire.Cli.Packaging;
using Aspire.Cli.Projects;
using Aspire.Cli.Resources;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Utils;
using Aspire.Hosting.Utils;
using Semver;
using Spectre.Console;
using NuGetPackage = Aspire.Shared.NuGetPackageCli;

namespace Aspire.Cli.Commands;

internal sealed class AddCommand : BaseCommand
{
    internal override HelpGroup HelpGroup => HelpGroup.AppCommands;

    protected override bool UpdateNotificationsEnabled => true;

    private readonly IProjectLocator _projectLocator;
    private readonly IntegrationPackageSearchService _integrationPackageSearchService;
    private readonly BundleNuGetService _nugetService;
    private readonly IAddCommandPrompter _prompter;
    private readonly IDotNetSdkInstaller _sdkInstaller;
    private readonly ICliHostEnvironment _hostEnvironment;
    private readonly IAppHostProjectFactory _projectFactory;
    private readonly ProfilingTelemetry _profilingTelemetry;
    private readonly IFeatures _features;

    private static readonly Argument<string> s_integrationArgument = new("integration")
    {
        Description = AddCommandStrings.IntegrationArgumentDescription,
        Arity = ArgumentArity.ZeroOrOne
    };
    private static readonly OptionWithLegacy<FileInfo?> s_appHostOption = new("--apphost", "--project", AddCommandStrings.ProjectArgumentDescription);
    private static readonly Option<string> s_versionOption = new("--version")
    {
        Description = AddCommandStrings.VersionArgumentDescription
    };
    private static readonly Option<string?> s_sourceOption = new("--source", "-s")
    {
        Description = AddCommandStrings.SourceArgumentDescription
    };
    private static readonly Option<bool> s_allOption = new("--all")
    {
        Description = AddCommandStrings.AllArgumentDescription
    };

    public AddCommand(IProjectLocator projectLocator, IntegrationPackageSearchService integrationPackageSearchService, BundleNuGetService nugetService, IAddCommandPrompter prompter, IDotNetSdkInstaller sdkInstaller, ICliHostEnvironment hostEnvironment, IAppHostProjectFactory projectFactory, ProfilingTelemetry profilingTelemetry, CommonCommandServices services)
        : base("add", AddCommandStrings.Description, services)
    {
        _projectLocator = projectLocator;
        _integrationPackageSearchService = integrationPackageSearchService;
        _nugetService = nugetService;
        _prompter = prompter;
        _sdkInstaller = sdkInstaller;
        _hostEnvironment = hostEnvironment;
        _projectFactory = projectFactory;
        _profilingTelemetry = profilingTelemetry;
        _features = services.Features;

        Arguments.Add(s_integrationArgument);
        Options.Add(s_appHostOption);
        Options.Add(s_versionOption);
        Options.Add(s_sourceOption);
        Options.Add(s_allOption);
    }

    protected override async Task<CommandResult> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        using var activity = Telemetry.StartDiagnosticActivity(this.Name);

        AddPackageContext? context = null;
        ProfilingTelemetry.ActivityScope addActivity = default;

        CommandResult AddCommandFailure(int exitCode, string? message = null)
        {
            addActivity.SetProcessExitCode(exitCode);
            addActivity.SetError(message ?? $"Add command exited with code {exitCode}.");

            return message is null
                ? CommandResult.Failure(exitCode)
                : CommandResult.Failure(exitCode, message);
        }

        CommandResult AddCommandFromExitCode(int exitCode)
        {
            addActivity.SetProcessExitCode(exitCode);
            if (exitCode != CliExitCodes.Success)
            {
                addActivity.SetError($"Add command exited with code {exitCode}.");
            }

            return CommandResult.FromExitCode(exitCode);
        }

        try
        {
            var integrationName = parseResult.GetValue(s_integrationArgument);
            var passedAppHostProjectFile = parseResult.GetValue(s_appHostOption);
            var version = parseResult.GetValue(s_versionOption);
            var source = parseResult.GetValue(s_sourceOption);
            var includeAllIntegrations = parseResult.GetValue(s_allOption);
            if (integrationName is null && !_hostEnvironment.SupportsInteractiveInput)
            {
                return AddCommandFailure(CliExitCodes.InvalidCommand, AddCommandStrings.NonInteractiveRequiresIntegrationName);
            }

            if (!string.IsNullOrWhiteSpace(source) &&
                PackageSourceOverrideMappings.HasCredentialMaterial(source))
            {
                InteractionService.DisplayError(AddCommandStrings.SourceWithCredentialsNotSupported);
                return CommandResult.Failure(CliExitCodes.InvalidCommand);
            }

            source = string.IsNullOrWhiteSpace(source)
                ? null
                : PackageSourceOverrideMappings.ResolveForWorkingDirectory(
                    source,
                    ExecutionContext.WorkingDirectory);
            if (source is not null && !PackageSourceIdentity.Comparer.Equals(source, PackageSources.NuGetOrg))
            {
                InteractionService.DisplaySourceTrustWarning(ExecutionContext);
            }
            addActivity = _profilingTelemetry.StartAddCommand(integrationName, version, source, passedAppHostProjectFile);

            AppHostProjectSearchResult searchResult;
            using (var findAppHostActivity = _profilingTelemetry.StartAddFindAppHost(passedAppHostProjectFile))
            {
                searchResult = await _projectLocator.UseOrFindAppHostProjectFileAsync(passedAppHostProjectFile, MultipleAppHostProjectsFoundBehavior.Prompt, createSettingsFile: true, cancellationToken);
                findAppHostActivity.SetAppHostCandidateCount(searchResult.AllProjectFileCandidates.Count);
            }
            addActivity.SetAppHostCandidateCount(searchResult.AllProjectFileCandidates.Count);

            var effectiveAppHostProjectFile = searchResult.SelectedProjectFile;

            if (effectiveAppHostProjectFile is null)
            {
                return AddCommandFailure(CliExitCodes.FailedToFindProject);
            }

            // Get the appropriate project handler
            var project = _projectFactory.GetProject(effectiveAppHostProjectFile);
            addActivity.SetAppHostLanguage(project.LanguageId);

            // Check if the .NET SDK is available (only needed for .NET projects)
            if (project.LanguageId == KnownLanguageId.CSharp)
            {
                if (!await SdkInstallHelper.EnsureSdkInstalledAsync(_sdkInstaller, InteractionService, Telemetry, cancellationToken: cancellationToken))
                {
                    return AddCommandFailure(CliExitCodes.SdkNotInstalled);
                }
            }

            string? configuredChannel;
            int? configuredChannelExitCode;
            using (var configuredChannelActivity = _profilingTelemetry.StartAddGetConfiguredChannel())
            {
                (configuredChannel, configuredChannelExitCode) = _integrationPackageSearchService.GetConfiguredChannel(effectiveAppHostProjectFile, project);
                configuredChannelActivity.SetAddConfiguredChannel(configuredChannel);
                if (configuredChannelExitCode is { } channelExitCode)
                {
                    configuredChannelActivity.SetProcessExitCode(channelExitCode);
                    if (channelExitCode != CliExitCodes.Success)
                    {
                        configuredChannelActivity.SetError($"Configured channel lookup exited with code {channelExitCode}.");
                    }
                }
            }
            if (configuredChannelExitCode is { } exitCode)
            {
                return AddCommandFromExitCode(exitCode);
            }

            // For non-C# (polyglot) AppHosts, only integrations with ATS export coverage are usable: a
            // TypeScript/Python/Go/Java/Rust AppHost gets a generated SDK only for packages carrying the
            // `polyglot` NuGet tag. The tag is added by default to Aspire.Hosting integrations that run the
            // export analyzer; authors opt out with <IsAspirePolyglotCompatible>false</IsAspirePolyglotCompatible>.
            // Decide up front whether to hide the rest so the polyglot allow-list is resolved in the same
            // discovery pass as the integration search rather than a second serial round. C# AppHosts consume
            // the C# API directly and are never filtered; --all opts out of filtering entirely.
            //
            // The filter is opt-in and off by default: resolving the allow-list depends on the package source
            // answering a `tags:polyglot` search, which no remote feed does usefully today (see the notes on
            // PackageChannel.PolyglotTagSearchTerm). Because the filter fails closed, an unresolvable
            // allow-list is indistinguishable from "nothing is compatible" and hides every integration.
            // See https://github.com/microsoft/aspire/issues/19161.
            var applyPolyglotFilter = _features.IsFeatureEnabled(KnownFeatures.PolyglotIntegrationFilterEnabled, false)
                && project.LanguageId != KnownLanguageId.CSharp
                && !includeAllIntegrations;

            List<(NuGetPackage Package, PackageChannel Channel)> packagesWithChannels;
            IReadOnlySet<string> polyglotCompatibleIds = ImmutableHashSet<string>.Empty;
            bool searchLimitReached;
            IReadOnlyList<string> failedSources;
            using (var searchPackagesActivity = _profilingTelemetry.StartAddSearchPackages(configuredChannel))
            {
                if (applyPolyglotFilter)
                {
                    var result = await InteractionService.ShowStatusAsync(
                        AddCommandStrings.SearchingForAspirePackages,
                        async () => await _integrationPackageSearchService.GetIntegrationPackagesWithPolyglotCompatibilityAsync(
                            effectiveAppHostProjectFile.Directory!,
                            configuredChannel,
                            source,
                            cancellationToken));
                    packagesWithChannels = result.Packages.ToList();
                    polyglotCompatibleIds = result.PolyglotCompatibleIds;
                    searchLimitReached = result.LimitReached;
                    failedSources = result.FailedSources;
                }
                else
                {
                    var result = await InteractionService.ShowStatusAsync(
                        AddCommandStrings.SearchingForAspirePackages,
                        async () => await _integrationPackageSearchService.GetIntegrationPackagesWithChannelsAsync(
                            effectiveAppHostProjectFile.Directory!,
                            configuredChannel,
                            source,
                            cancellationToken));
                    packagesWithChannels = result.Packages.ToList();
                    searchLimitReached = result.LimitReached;
                    failedSources = result.FailedSources;
                }

                var packageCount = packagesWithChannels.Count;
                searchPackagesActivity.SetAddPackageSearchResultCount(packageCount);
                addActivity.SetAddPackageSearchResultCount(packageCount);
            }

            if (searchLimitReached)
            {
                InteractionService.DisplayNuGetSearchLimitWarning();
            }

            InteractionService.DisplayNuGetSearchFailureWarnings(failedSources);

            if (packagesWithChannels.Count == 0)
            {
                throw new EmptyChoicesException(AddCommandStrings.NoIntegrationPackagesFound);
            }

            var packagesWithShortName = packagesWithChannels
                .Select(IntegrationPackageSearchService.GenerateFriendlyName)
                .OrderByDescending(p => PackageIdFilters.IsOfficialIntegrationPackageId(p.Package.Id))
                .ThenBy(p => p.FriendlyName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (packagesWithShortName.Count == 0)
            {
                return AddCommandFailure(CliExitCodes.FailedToAddPackage, AddCommandStrings.NoPackagesFound);
            }

            if (applyPolyglotFilter)
            {
                bool MatchesIntegrationName((string FriendlyName, NuGetPackage Package, PackageChannel Channel) p)
                    => p.FriendlyName == integrationName || p.Package.Id == integrationName;

                // If the user named a specific integration that exists but is not polyglot-compatible, give a
                // precise, actionable error rather than silently dropping it and fuzzy-matching something else.
                if (integrationName is not null
                    && packagesWithShortName.Any(p => MatchesIntegrationName(p) && !polyglotCompatibleIds.Contains(p.Package.Id))
                    && !packagesWithShortName.Any(p => MatchesIntegrationName(p) && polyglotCompatibleIds.Contains(p.Package.Id)))
                {
                    var notCompatibleMessage = string.Format(CultureInfo.CurrentCulture, AddCommandStrings.IntegrationNotPolyglotCompatible, integrationName, project.LanguageId);
                    return AddCommandFailure(CliExitCodes.FailedToAddPackage, notCompatibleMessage);
                }

                var compatiblePackagesWithShortName = packagesWithShortName
                    .Where(p => polyglotCompatibleIds.Contains(p.Package.Id))
                    .ToList();
                var hiddenIntegrationCount = packagesWithShortName.Count - compatiblePackagesWithShortName.Count;
                packagesWithShortName = compatiblePackagesWithShortName;

                if (packagesWithShortName.Count == 0)
                {
                    return AddCommandFailure(CliExitCodes.FailedToAddPackage, AddCommandStrings.NoPolyglotCompatibleIntegrationsFound);
                }

                if (hiddenIntegrationCount > 0)
                {
                    InteractionService.DisplaySubtleMessage(string.Format(CultureInfo.CurrentCulture, AddCommandStrings.PolyglotIntegrationsHidden, hiddenIntegrationCount));
                }
            }

            var filteredPackagesWithShortName = packagesWithShortName
                .Where(p => p.FriendlyName == integrationName || p.Package.Id == integrationName)
                .ToList();
            var packageMatchKind = filteredPackagesWithShortName.Count > 0
                ? ProfilingTelemetry.Values.AddPackageMatchKindExact
                : ProfilingTelemetry.Values.AddPackageMatchKindNone;

            // Non-interactive mode never falls back to fuzzy search: in interactive mode the user picks
            // from the fuzzy candidates, but a script/CI invocation would otherwise silently auto-select
            // distinctPackages.First() in GetPackageByInteractiveFlow and install the wrong package
            // (https://github.com/microsoft/aspire/issues/17724). Refusing with an actionable error
            // forces the caller to supply an exact package id or friendly name.
            if (filteredPackagesWithShortName.Count == 0 && integrationName is not null && !_hostEnvironment.SupportsInteractiveInput)
            {
                var message = version is not null
                    ? string.Format(CultureInfo.CurrentCulture, AddCommandStrings.SpecifiedVersionRequiresExactPackageMatch, integrationName)
                    : string.Format(CultureInfo.CurrentCulture, AddCommandStrings.NonInteractiveRequiresExactPackageMatch, integrationName);
                throw new EmptyChoicesException(message);
            }

            if (filteredPackagesWithShortName.Count == 0 && integrationName is not null)
            {
                // If we didn't get an exact match on the friendly name or the package ID
                // then try a fuzzy search to create a broader filtered list.
                // Materialize the query with ToList() to avoid multiple enumerations
                // (which would recalculate fuzzy scores on each Count()/First() call).
                filteredPackagesWithShortName = IntegrationPackageSearchService.GetIntegrationSearchMatches(packagesWithShortName, integrationName)
                    .Select(x => (x.FriendlyName, x.Package, x.Channel))
                    .ToList();
                packageMatchKind = filteredPackagesWithShortName.Count > 0
                    ? ProfilingTelemetry.Values.AddPackageMatchKindFuzzy
                    : ProfilingTelemetry.Values.AddPackageMatchKindNone;
            }

            // If the user supplied a partial/fuzzy search term, keep the package prompt even when
            // the fallback only found one candidate; otherwise `aspire add kube` can silently add
            // the lone fuzzy match without asking the interactive user to confirm it.
            var promptForSingleFuzzyPackage = packageMatchKind == ProfilingTelemetry.Values.AddPackageMatchKindFuzzy;

            // If we didn't match any, show a complete list. If we matched one, and it's
            // an exact match, then we still prompt, but it will only prompt for
            // the version. If there is more than one match then we prompt.
            var selection = filteredPackagesWithShortName.Count switch
            {
                0 => await GetPackageByInteractiveFlowWithNoMatchesMessage(
                    effectiveAppHostProjectFile.Directory!,
                    packagesWithShortName,
                    integrationName,
                    version,
                    configuredChannel,
                    source,
                    cancellationToken,
                    promptForSinglePackage: true),
                1 when packageMatchKind == ProfilingTelemetry.Values.AddPackageMatchKindExact
                    && filteredPackagesWithShortName[0].Package.Version == version
                    => new IntegrationSelection(filteredPackagesWithShortName[0], null),
                _ => await GetPackageByInteractiveFlow(
                    effectiveAppHostProjectFile.Directory!,
                    filteredPackagesWithShortName,
                    version,
                    configuredChannel,
                    source,
                    cancellationToken,
                    promptForSingleFuzzyPackage)
            };
            var selectedNuGetPackage = selection.Package;
            var selectedSource = source ?? selection.SourceOverride;
            if (selectedSource is not null && PackageSourceOverrideMappings.HasCredentialMaterial(selectedSource))
            {
                InteractionService.DisplayError(AddCommandStrings.SourceWithCredentialsNotSupported);
                return CommandResult.Failure(CliExitCodes.InvalidCommand);
            }
            using (var selectPackageActivity = _profilingTelemetry.StartAddSelectPackage(integrationName, version))
            {
                selectPackageActivity.SetAddPackageMatch(filteredPackagesWithShortName.Count, packageMatchKind);
                selectPackageActivity.SetAddSelectedPackage(selectedNuGetPackage.Package.Id, selectedNuGetPackage.Package.Version, selectedNuGetPackage.Channel.Name);
                addActivity.SetAddSelectedPackage(selectedNuGetPackage.Package.Id, selectedNuGetPackage.Package.Version, selectedNuGetPackage.Channel.Name);
            }

            // When installing from a PR channel, ensure the project has access to
            // the PR hive as a NuGet source so `dotnet add package` can resolve the
            // PR-version package. We add the hive source to the project's nuget.config
            // while preserving the effective package source mapping policy, if present.
            //
            // IsBackedByLocalPackageDirectory (not just the local-build NAME) is required so this
            // also fires when emulating a released build via ASPIRE_CLI_PACKAGES: there the channel
            // is named stable/daily/staging but its Aspire.* packages live in a local directory, and
            // without the local source in nuget.config the C# `dotnet add package` restore of the
            // local-only version fails. See docs/specs/cli-identity-sidecar.md.
            // Named integration feeds persist the selected package below; their discovery patterns
            // must not become hive-wide restore mappings.
            if (project.LanguageId == KnownLanguageId.CSharp &&
                selectedNuGetPackage.Channel.IntegrationSource is null &&
                string.IsNullOrEmpty(source) &&
                (VersionHelper.IsLocalBuildChannel(selectedNuGetPackage.Channel.Name) || selectedNuGetPackage.Channel.IsBackedByLocalPackageDirectory))
            {
                var mappings = selectedNuGetPackage.Channel.Mappings;
                if (mappings is { Length: > 0 })
                {
                    var projectDir = effectiveAppHostProjectFile.Directory!;
                    var nugetConfigPath = Path.Combine(projectDir.FullName, "nuget.config");
                    if (!File.Exists(nugetConfigPath))
                    {
                        var packageSourceMappingEnabled = _nugetService.IsPackageSourceMappingEnabled(
                            projectDir,
                            cancellationToken);
                        CreateAdditiveLocalSourceNuGetConfig(
                            projectDir,
                            mappings,
                            packageSourceMappingEnabled);
                        InteractionService.DisplayMessage(KnownEmojis.Package, Aspire.Cli.Resources.TemplatingStrings.NuGetConfigCreatedOrUpdatedConfirmationMessage);
                    }
                }
            }

            // Native --source excludes other SDK and dependency feeds. Named picker selections instead
            // use persistent NuGet configuration; polyglot restores already compose exact overrides.
            var restoreSource = source ?? (project.LanguageId == KnownLanguageId.CSharp ? null : selection.SourceOverride);
            DotNetAppHostNuGetConfigMergerCandidate? configurationCandidate = null;
            if (restoreSource is null && selectedNuGetPackage.Channel.IntegrationSource?.Source is { } integrationSource)
            {
                // Prepare an exact package mapping so later builds can still restore the integration.
                // Unlike --source, this keeps ambient feeds available for the SDK and dependencies.
                var configuration = _nugetService.BuildAppHostConfiguration(
                    effectiveAppHostProjectFile.Directory!,
                    "integration-add",
                    [new PackageMapping(selectedNuGetPackage.Package.Id, integrationSource)
                    {
                        SourceKey = selectedNuGetPackage.Channel.IntegrationSource.Name
                    }],
                    overrideIntegrationSources: false,
                    cancellationToken);
                var targetDirectory = DotNetAppHostNuGetConfigMerger.GetRecommendedNuGetConfigDirectory(
                    configuration.Settings.ConfigPaths,
                    effectiveAppHostProjectFile.Directory!);
                configurationCandidate = await new DotNetAppHostNuGetConfigMerger(_nugetService).PrepareAsync(
                    targetDirectory,
                    configuration,
                    createIfMissing: true,
                    globalPackagesFolder: null,
                    cancellationToken);
            }

            context = new AddPackageContext
            {
                AppHostFile = effectiveAppHostProjectFile,
                PackageId = selectedNuGetPackage.Package.Id,
                PackageVersion = selectedNuGetPackage.Package.Version,
                RequestedChannel = GetRequestedRestoreChannel(
                    selectedNuGetPackage.Channel,
                    selectedNuGetPackage.Package.Id,
                    selectedSource),
                Source = restoreSource,
                SourcePackagePattern = restoreSource is null
                    ? null
                    : selectedNuGetPackage.Package.Id
            };

            // Stop any running AppHost instance before adding the package.
            // A running AppHost (especially in detach mode) locks project files,
            // which prevents 'dotnet add package' from modifying the project.
            RunningInstanceResult runningInstanceResult;
            using (var stopRunningInstanceActivity = _profilingTelemetry.StartAddStopExistingInstance())
            {
                runningInstanceResult = await project.FindAndStopRunningInstanceAsync(
                    effectiveAppHostProjectFile,
                    ExecutionContext.HomeDirectory,
                    cancellationToken);
                stopRunningInstanceActivity.SetAppHostRunningInstanceResult(runningInstanceResult);
            }

            if (runningInstanceResult == RunningInstanceResult.InstanceStopped)
            {
                InteractionService.DisplayMessage(KnownEmojis.Information, AddCommandStrings.StoppedRunningInstance);
            }
            else if (runningInstanceResult == RunningInstanceResult.StopFailed)
            {
                return AddCommandFailure(CliExitCodes.FailedToAddPackage, AddCommandStrings.UnableToStopRunningInstances);
            }

            bool success;
            using (var addPackageActivity = _profilingTelemetry.StartAddPackage(context.PackageId, context.PackageVersion, context.Source))
            {
                success = await InteractionService.ShowStatusAsync(
                    AddCommandStrings.AddingAspireIntegration,
                    async () => await AddPackageWithNuGetConfigurationAsync(project, context, configurationCandidate, cancellationToken)
                );
                addPackageActivity.SetAddPackageSuccess(success);
                if (!success)
                {
                    addPackageActivity.SetError("Package installation failed.");
                }
            }

            if (!success)
            {
                if (context.OutputCollector is { } outputCollector)
                {
                    InteractionService.DisplayLines(outputCollector.GetLines());
                }
                return AddCommandFailure(CliExitCodes.FailedToAddPackage, string.Format(CultureInfo.CurrentCulture, AddCommandStrings.PackageInstallationFailed, CliExitCodes.FailedToAddPackage));
            }

            InteractionService.DisplaySuccess(string.Format(CultureInfo.CurrentCulture, AddCommandStrings.PackageAddedSuccessfully, selectedNuGetPackage.Package.Id, selectedNuGetPackage.Package.Version));
            addActivity.SetProcessExitCode(CliExitCodes.Success);
            return CommandResult.Success();
        }
        catch (ProjectLocatorException ex)
        {
            addActivity.SetError(ex);
            return HandleProjectLocatorException(ex, InteractionService, Telemetry);
        }
        catch (OperationCanceledException)
        {
            return CommandResult.Cancelled();
        }
        catch (EmptyChoicesException ex)
        {
            addActivity.SetProcessExitCode(CliExitCodes.FailedToAddPackage);
            addActivity.SetError(ex.Message);
            Telemetry.RecordError(ex.Message, ex);
            return CommandResult.Failure(CliExitCodes.FailedToAddPackage, ex.Message);
        }
        catch (Exception ex)
        {
            if (context?.OutputCollector is { } outputCollector)
            {
                InteractionService.DisplayLines(outputCollector.GetLines());
            }
            var errorMessage = string.Format(CultureInfo.CurrentCulture, AddCommandStrings.ErrorOccurredWhileAddingPackage, ex.Message);
            addActivity.SetProcessExitCode(CliExitCodes.FailedToAddPackage);
            addActivity.SetError(ex);
            Telemetry.RecordError(errorMessage, ex);
            return CommandResult.Failure(CliExitCodes.FailedToAddPackage, errorMessage);
        }
        finally
        {
            addActivity.Dispose();
        }
    }

    private static async Task<bool> AddPackageWithNuGetConfigurationAsync(
        IAppHostProject project,
        AddPackageContext context,
        DotNetAppHostNuGetConfigMergerCandidate? candidate,
        CancellationToken cancellationToken)
    {
        if (candidate is null)
        {
            return await project.AddPackageAsync(context, cancellationToken);
        }

        // Native package installation and polyglot preparation need the candidate in the real
        // configuration hierarchy. Roll back that temporary write if adding the package fails.
        await using var transaction = new DotNetAppHostUpdateTransaction(new Dictionary<string, byte[]?>
        {
            [PathNormalizer.ResolveToFilesystemPath(candidate.TargetFile.FullName)] = candidate.OriginalContent?.ToArray()
        });
        try
        {
            await transaction.ApplyPreparedAsync(candidate.TargetFile, candidate.ProposedContent,
                writeStarting => DotNetAppHostNuGetConfigMerger.ApplyAsync(candidate, writeStarting, cancellationToken),
                cancellationToken);
            var success = await project.AddPackageAsync(context, cancellationToken);
            if (!success)
            {
                try
                {
                    await transaction.DisposeAsync();
                }
                catch (Exception rollbackFailure)
                {
                    throw new AggregateException(
                        new InvalidOperationException(string.Format(CultureInfo.CurrentCulture,
                            AddCommandStrings.PackageInstallationFailed, CliExitCodes.FailedToAddPackage)),
                        rollbackFailure);
                }

                return false;
            }

            await transaction.VerifyAsync(cancellationToken);
            transaction.Commit();

            return true;
        }
        catch (Exception addFailure)
        {
            try
            {
                await transaction.DisposeAsync();
            }
            catch (Exception rollbackFailure)
            {
                throw new AggregateException(addFailure, rollbackFailure);
            }
            throw;
        }
    }

    private static string? GetRequestedRestoreChannel(
        PackageChannel channel,
        string packageId,
        string? source)
    {
        if (channel.IntegrationSource is not null)
        {
            // Named integration sources select the package feed, not the AppHost's SDK channel.
            return null;
        }
        if (channel.Type is PackageChannelType.Explicit)
        {
            return channel.Name;
        }

        // A source-scoped search always identifies one canonical package ID. Mapping that exact
        // package to the source can coexist with the project's channel while ambient policy
        // remains available for the dependency closure.
        if (!string.IsNullOrWhiteSpace(source) ||
            !packageId.StartsWith("Aspire", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // A null requested channel means "use the channel persisted by the project". Selecting an
        // Aspire package from the implicit channel must instead retain ambient NuGet policy for
        // this operation, which is the stable-channel contract.
        return PackageChannelNames.Stable;
    }

    internal static void CreateAdditiveLocalSourceNuGetConfig(
        DirectoryInfo projectDirectory,
        PackageMapping[] mappings,
        bool packageSourceMappingEnabled)
    {
        var localMappings = mappings
            .Where(static mapping => !mapping.Source.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var localSources = localMappings
            .Select(static mapping => mapping.Source)
            .Distinct(PackageSourceIdentity.Comparer);
        var configuration = new XElement(
            "configuration",
            new XElement(
                "packageSources",
                localSources.Select(static source =>
                    new XElement(
                        "add",
                        new XAttribute("key", source),
                        new XAttribute("value", source)))));

        if (packageSourceMappingEnabled)
        {
            // NuGet retains the higher-precedence key when the same source value exists in an
            // inherited config, so the child mapping must use the exact local key written above.
            configuration.Add(
                new XElement(
                    "packageSourceMapping",
                    localMappings
                        .GroupBy(static mapping => mapping.Source, PackageSourceIdentity.Comparer)
                        .Select(static group =>
                            new XElement(
                                "packageSource",
                                new XAttribute("key", group.Key),
                                group.Select(static mapping =>
                                    new XElement(
                                        "package",
                                        new XAttribute("pattern", mapping.PackageFilter)))))));
        }

        new XDocument(configuration).Save(
            Path.Combine(projectDirectory.FullName, "nuget.config"));
    }

    private static async Task<IEnumerable<(string FriendlyName, NuGetPackage Package, PackageChannel Channel)>> GetAllPackageVersions(
        DirectoryInfo workingDirectory,
        IEnumerable<(string FriendlyName, NuGetPackage Package, PackageChannel Channel)> possiblePackages,
        string? source,
        CancellationToken cancellationToken)
    {
        var distinctPackageIds = possiblePackages.DistinctBy(package => package.Package.Id);
        var channels = possiblePackages.Select(package => package.Channel).Distinct();
        var sourceMappings = source is null
            ? null
            : PackageSourceOverrideMappings.CreateForSourceOnlyOperations(source);

        var versions = new List<(string FriendlyName, NuGetPackage Package, PackageChannel Channel)>();
        foreach (var channel in channels)
        {
            foreach (var package in distinctPackageIds)
            {
                var packages = sourceMappings is null
                    ? await channel.GetPackageVersionsAsync(package.Package.Id, workingDirectory, cancellationToken)
                    : await channel.GetPackageVersionsAsync(package.Package.Id, workingDirectory, sourceMappings, cancellationToken);
                versions.AddRange(packages.Select(p => (FriendlyName: package.FriendlyName, Package: p, Channel: channel)));
            }
        }
        return versions;
    }

    private async Task<IntegrationSelection> GetPackageByInteractiveFlow(
        DirectoryInfo workingDirectory,
        IEnumerable<(string FriendlyName, NuGetPackage Package, PackageChannel Channel)> possiblePackages,
        string? preferredVersion,
        string? configuredChannel,
        string? source,
        CancellationToken cancellationToken,
        bool promptForSinglePackage = false)
    {
        var candidates = possiblePackages.ToArray();
        var distinctPackages = candidates.DistinctBy(p => p.Package.Id, StringComparer.OrdinalIgnoreCase).ToArray();
        var promptForPackage = _hostEnvironment.SupportsInteractiveInput &&
            (distinctPackages.Length > 1 || promptForSinglePackage);

        // Only exact matches can skip the package prompt. Discovery and fuzzy/no-match
        // fallbacks require confirmation even when other feeds failed and one package remains.
        var selectedPackage = distinctPackages.Length switch
        {
            1 when promptForSinglePackage && _hostEnvironment.SupportsInteractiveInput => await PromptForIntegrationAsync(candidates, cancellationToken),
            1 => distinctPackages.First(),
            > 1 when !_hostEnvironment.SupportsInteractiveInput => distinctPackages.First(),
            > 1 => await PromptForIntegrationAsync(candidates, cancellationToken),
            _ => throw new InvalidOperationException(AddCommandStrings.UnexpectedNumberOfPackagesFound)
        };

        var selectedSource = promptForPackage && candidates.Select(AddCommandPrompter.GetSourceIdentity).Distinct().Skip(1).Any()
            ? selectedPackage.Package.Source
            : null;
        // A source group is a real choice of feed, not just decoration. Keep both discovery
        // and explicit-version lookup within it, and carry the exact feed into package restore.
        var packageVersions = candidates.Where(p => string.Equals(p.Package.Id, selectedPackage.Package.Id, StringComparison.OrdinalIgnoreCase) &&
            (selectedSource is null || PackageSourceIdentity.Comparer.Equals(p.Package.Source, selectedSource))).ToArray();

        // If any of the package versions are an exact match for the preferred version
        // then we can skip the version prompt and just use that version.
        if (!string.IsNullOrEmpty(preferredVersion))
        {
            if (packageVersions.Any(p => p.Package.Version == preferredVersion))
            {
                var preferredVersionPackage = packageVersions.First(p => p.Package.Version == preferredVersion);
                return new(preferredVersionPackage, selectedSource);
            }

            var allVersions = await InteractionService.ShowStatusAsync(
                string.Format(CultureInfo.CurrentCulture, AddCommandStrings.SearchingForSpecifiedPackageVersion, selectedPackage.Package.Id, preferredVersion),
                async () => await GetAllPackageVersions(workingDirectory, packageVersions, source ?? selectedSource, cancellationToken));
            var matchedPreferredVersionPackage = allVersions.FirstOrDefault(packageVersion => packageVersion.Package.Version == preferredVersion);
            if (matchedPreferredVersionPackage.Package is not null)
            {
                return new(matchedPreferredVersionPackage, selectedSource);
            }

            throw new EmptyChoicesException(string.Format(CultureInfo.CurrentCulture, AddCommandStrings.SpecifiedVersionNotFoundForPackage, selectedPackage.Package.Id, preferredVersion));
        }

        // When PR hives are present, prefer the package that exactly matches the installed
        // CLI/SDK version so template- and add-generated projects stay on the same build.
        // IsBackedByLocalPackageDirectory also covers the ASPIRE_CLI_PACKAGES emulation case,
        // where the local channel is named stable/daily/staging rather than a local-build name.
        var prChannelPackageVersions = packageVersions
            .Where(p => VersionHelper.IsLocalBuildChannel(p.Channel.Name) || p.Channel.IsBackedByLocalPackageDirectory)
            .ToArray();

        if (VersionHelper.TryGetCurrentCliVersionMatch(
            prChannelPackageVersions,
            p => p.Package.Version,
            ExecutionContext.IdentitySdkVersion,
            out var cliVersionPackage,
            channelName: null,
            hasPrHives: ExecutionContext.GetHiveCount() > 0 || ExecutionContext.IdentityPackagesDirectory is not null))
        {
            return new(cliVersionPackage, selectedSource);
        }

        // In non-interactive mode, prefer the channel the apphost has pinned (e.g. a polyglot
        // apphost that persists `channel: daily` in aspire.config.json). When a channel is pinned,
        // it reflects explicit user intent and must outrank the implicit/ambient channel — otherwise
        // the implicit channel (which for polyglot apphosts just mirrors nuget.org) shadows the pinned
        // feed and we auto-select a stable version the pinned feed can't restore, producing a hard
        // restore failure. See https://github.com/microsoft/aspire/issues/18114 (the polyglot
        // selection-path manifestation of the C#-only display bug https://github.com/microsoft/aspire/issues/17294).
        // When no channel is pinned (the common C# case, where configuredChannel is null) we keep the prior behavior:
        // implicit/default channel first, then latest version within the chosen channel.
        var orderedPackageVersions = packageVersions
            .OrderByDescending(p => MatchesConfiguredChannel(p.Channel, configuredChannel))
            .ThenByDescending(p => p.Channel.Type is PackageChannelType.Implicit)
            .ThenByDescending(p => SemVersion.Parse(p.Package.Version), SemVersion.PrecedenceComparer);
        if (!_hostEnvironment.SupportsInteractiveInput)
        {
            return new(orderedPackageVersions.First(), selectedSource);
        }

        // ... otherwise we had better prompt.
        var version = await PromptForIntegrationVersionAsync(orderedPackageVersions, configuredChannel, cancellationToken);

        return new(version, selectedSource ??
            (packageVersions.Select(AddCommandPrompter.GetSourceIdentity).Distinct().Skip(1).Any() ? version.Package.Source : null));
    }

    // A package "matches" the pinned channel when the apphost pinned an explicit channel name and the
    // package was discovered from the explicit channel of that name (case-insensitive). The implicit
    // channel never matches because it has no stable name to pin against.
    private static bool MatchesConfiguredChannel(PackageChannel channel, string? configuredChannel)
    {
        return !string.IsNullOrEmpty(configuredChannel)
            && channel.Type is PackageChannelType.Explicit
            && string.Equals(channel.Name, configuredChannel, StringComparisons.ChannelName);
    }

    private async Task<(string FriendlyName, NuGetPackage Package, PackageChannel Channel)> PromptForIntegrationAsync(IEnumerable<(string FriendlyName, NuGetPackage Package, PackageChannel Channel)> packages, CancellationToken cancellationToken)
    {
        using var promptActivity = _profilingTelemetry.StartAddSelectPackagePrompt();
        return await _prompter.PromptForIntegrationAsync(packages, cancellationToken);
    }

    private async Task<(string FriendlyName, NuGetPackage Package, PackageChannel Channel)> PromptForIntegrationVersionAsync(IEnumerable<(string FriendlyName, NuGetPackage Package, PackageChannel Channel)> packages, string? configuredChannel, CancellationToken cancellationToken)
    {
        using var promptActivity = _profilingTelemetry.StartAddSelectPackagePrompt();
        return await _prompter.PromptForIntegrationVersionAsync(packages, configuredChannel, cancellationToken);
    }

    private async Task<IntegrationSelection> GetPackageByInteractiveFlowWithNoMatchesMessage(
        DirectoryInfo workingDirectory,
        IEnumerable<(string FriendlyName, NuGetPackage Package, PackageChannel Channel)> possiblePackages,
        string? searchTerm,
        string? preferredVersion,
        string? configuredChannel,
        string? source,
        CancellationToken cancellationToken,
        bool promptForSinglePackage = false)
    {
        if (searchTerm is not null)
        {
            InteractionService.DisplaySubtleMessage(string.Format(CultureInfo.CurrentCulture, AddCommandStrings.NoPackagesMatchedSearchTerm, searchTerm));
        }

        return await GetPackageByInteractiveFlow(
            workingDirectory,
            possiblePackages,
            preferredVersion,
            configuredChannel,
            source,
            cancellationToken,
            promptForSinglePackage);
    }

    private readonly record struct IntegrationSelection(
        (string FriendlyName, NuGetPackage Package, PackageChannel Channel) Package,
        string? SourceOverride);
}

internal interface IAddCommandPrompter
{
    Task<(string FriendlyName, NuGetPackage Package, PackageChannel Channel)> PromptForIntegrationAsync(IEnumerable<(string FriendlyName, NuGetPackage Package, PackageChannel Channel)> packages, CancellationToken cancellationToken);
    Task<(string FriendlyName, NuGetPackage Package, PackageChannel Channel)> PromptForIntegrationVersionAsync(IEnumerable<(string FriendlyName, NuGetPackage Package, PackageChannel Channel)> packages, string? configuredChannel, CancellationToken cancellationToken);
}

internal class AddCommandPrompter(IInteractionService interactionService) : IAddCommandPrompter
{
    public virtual async Task<(string FriendlyName, NuGetPackage Package, PackageChannel Channel)> PromptForIntegrationVersionAsync(IEnumerable<(string FriendlyName, NuGetPackage Package, PackageChannel Channel)> packages, string? configuredChannel, CancellationToken cancellationToken)
    {
        // Preserve the pinned-channel default, while separating feeds within the implicit
        // channel instead of silently choosing one of several ambient NuGet sources.
        var candidates = packages.ToArray();
        var choices = candidates
            .GroupBy(package => (package.Channel, Source: GetSourceIdentity(package)))
            .Select(group => group.OrderByDescending(package => SemVersion.Parse(package.Package.Version), SemVersion.PrecedenceComparer).First())
            .OrderByDescending(package => package.Channel.Type is PackageChannelType.Explicit &&
                string.Equals(package.Channel.Name, configuredChannel, StringComparisons.ChannelName))
            .ThenByDescending(package => package.Channel.Type is PackageChannelType.Implicit)
            .GroupBy(package => (Source: GetSourceIdentity(package), package.Package.Version))
            // Retain configured provenance without reordering distinct feeds or changing pinned defaults.
            .Select(group => group
                .OrderByDescending(package => package.Channel.Type is PackageChannelType.Explicit &&
                    string.Equals(package.Channel.Name, configuredChannel, StringComparisons.ChannelName))
                .ThenByDescending(package => package.Channel.IntegrationSource is not null)
                .First())
            .ToArray();
        if (choices.Length == 1)
        {
            return choices[0];
        }

        DisplaySources(candidates);
        var showGroups = choices.Select(GetSourceIdentity).Distinct().Skip(1).Any();
        HashSet<string> usedLabels = [];
        var labels = choices.ToDictionary(package => package, package =>
            ConsoleHelpers.GetUniqueSelectionLabel($"{package.Package.Version} ({GetSourceGroup(package).Label})", usedLabels).EscapeMarkup());
        return await interactionService.PromptForSelectionAsync(
            string.Format(CultureInfo.CurrentCulture, AddCommandStrings.SelectAVersionOfPackage, choices[0].Package.Id),
            choices,
            package => labels[package],
            package => showGroups ? GetSourceGroup(package) : null,
            package => package.Package.Version.EscapeMarkup(),
            cancellationToken: cancellationToken);
    }

    public virtual async Task<(string FriendlyName, NuGetPackage Package, PackageChannel Channel)> PromptForIntegrationAsync(IEnumerable<(string FriendlyName, NuGetPackage Package, PackageChannel Channel)> packages, CancellationToken cancellationToken)
    {
        // Keep each source's highest version so identical package IDs remain selectable
        // from every feed rather than inheriting whichever source happened to finish first.
        var candidates = packages.ToArray();
        var filteredPackages = candidates
            .GroupBy(package => (Id: package.Package.Id.ToUpperInvariant(), Source: GetSourceIdentity(package)))
            .Select(group => group
                .OrderByDescending(package => SemVersion.Parse(package.Package.Version), SemVersion.PrecedenceComparer)
                .ThenByDescending(package => package.Channel.IntegrationSource is not null)
                .First())
            .ToArray();

        DisplaySources(candidates);
        var showGroups = filteredPackages.Select(GetSourceIdentity).Distinct().Skip(1).Any();
        HashSet<string> usedLabels = [];
        var labels = filteredPackages.ToDictionary(package => package, package =>
        {
            var name = package.FriendlyName is { } friendlyName ? $"{friendlyName} ({package.Package.Id})" : package.Package.Id;
            return ConsoleHelpers.GetUniqueSelectionLabel($"{name} - {GetSourceGroup(package).Label}", usedLabels).EscapeMarkup();
        });
        var selectedIntegration = await interactionService.PromptForSelectionAsync(
            AddCommandStrings.SelectAnIntegrationToAdd,
            filteredPackages,
            package => showGroups
                ? labels[package]
                : PackageNameWithFriendlyNameIfAvailable(package),
            package => showGroups ? GetSourceGroup(package) : null,
            PackageNameWithFriendlyNameIfAvailable,
            cancellationToken: cancellationToken);
        return selectedIntegration;
    }

    internal static string GetSourceIdentity((string FriendlyName, NuGetPackage Package, PackageChannel Channel) package) =>
        PackageSourceIdentity.Normalize(package.Package.Source);

    private static SelectionChoiceGroup GetSourceGroup((string FriendlyName, NuGetPackage Package, PackageChannel Channel) package)
    {
        var isNuGetOrg = PackageSourceIdentity.Comparer.Equals(package.Package.Source, PackageSources.NuGetOrg);
        var name = package.Channel.IntegrationSource?.Name ??
            (isNuGetOrg ? "nuget.org" : package.Channel.Type is PackageChannelType.Explicit
                ? PackageSourceRedactor.RedactForDisplay(package.Channel.Name)
                : PackageSourceRedactor.RedactForDisplay(package.Package.Source));
        var group = new SelectionChoiceGroup(GetSourceIdentity(package), name);
        return isNuGetOrg ? group : group with { Style = new Style(Color.Green) };
    }

    private void DisplaySources(IReadOnlyList<(string FriendlyName, NuGetPackage Package, PackageChannel Channel)> packages)
    {
        var sources = packages.Where(package => package.Channel.IntegrationSource is not null)
            .DistinctBy(GetSourceIdentity).ToArray();
        if (sources.Length > 0)
        {
            interactionService.DisplayMessage(KnownEmojis.Information, AddCommandStrings.IntegrationSourceExplanation);
            interactionService.DisplaySourceDetails(sources.Select(package =>
            {
                List<(string Label, string Value)> details =
                [
                    (SharedCommandStrings.SourceName, GetSourceGroup(package).Label),
                    (SharedCommandStrings.SourceFeed, PackageSourceRedactor.RedactForDisplay(package.Package.Source))
                ];
                if (package.Channel.IntegrationSource?.Patterns is { } patterns)
                {
                    details.Add((SharedCommandStrings.SourcePatterns, string.Join(", ", patterns)));
                }
                return details;
            }));
        }
        if (packages.Any(package => !PackageSourceIdentity.Comparer.Equals(package.Package.Source, PackageSources.NuGetOrg)))
        {
            interactionService.DisplaySourceTrustWarning();
        }
    }

    private static string PackageNameWithFriendlyNameIfAvailable((string FriendlyName, NuGetPackage Package, PackageChannel Channel) packageWithFriendlyName)
    {
        if (packageWithFriendlyName.FriendlyName is { } friendlyName)
        {
            return $"[bold]{friendlyName.EscapeMarkup()}[/] ({packageWithFriendlyName.Package.Id.EscapeMarkup()})";
        }
        else
        {
            return packageWithFriendlyName.Package.Id.EscapeMarkup();
        }
    }
}
