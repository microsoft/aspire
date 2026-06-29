// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.Globalization;
using Aspire.Cli.DotNet;
using Aspire.Cli.Interaction;
using Aspire.Cli.Packaging;
using Aspire.Cli.Projects;
using Aspire.Cli.Resources;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Utils;
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
    private readonly IAddCommandPrompter _prompter;
    private readonly IDotNetSdkInstaller _sdkInstaller;
    private readonly IDotNetCliRunner _runner;
    private readonly ICliHostEnvironment _hostEnvironment;
    private readonly IAppHostProjectFactory _projectFactory;
    private readonly ProfilingTelemetry _profilingTelemetry;

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
    private static readonly Option<string?> s_discoveryScopeOption = new("--discovery-scope")
    {
        Description = AddCommandStrings.DiscoveryScopeOptionDescription
    };

    public AddCommand(IProjectLocator projectLocator, IntegrationPackageSearchService integrationPackageSearchService, IAddCommandPrompter prompter, IDotNetSdkInstaller sdkInstaller, IDotNetCliRunner runner, ICliHostEnvironment hostEnvironment, IAppHostProjectFactory projectFactory, ProfilingTelemetry profilingTelemetry, CommonCommandServices services)
        : base("add", AddCommandStrings.Description, services)
    {
        _projectLocator = projectLocator;
        _integrationPackageSearchService = integrationPackageSearchService;
        _prompter = prompter;
        _sdkInstaller = sdkInstaller;
        _runner = runner;
        _hostEnvironment = hostEnvironment;
        _projectFactory = projectFactory;
        _profilingTelemetry = profilingTelemetry;

        Arguments.Add(s_integrationArgument);
        Options.Add(s_appHostOption);
        Options.Add(s_versionOption);
        Options.Add(s_sourceOption);
        Options.Add(s_discoveryScopeOption);
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
            var requestedDiscoveryScope = parseResult.GetValue(s_discoveryScopeOption);
            addActivity = _profilingTelemetry.StartAddCommand(integrationName, version, source, passedAppHostProjectFile);

            AppHostProjectSearchResult searchResult;
            using (var findAppHostActivity = _profilingTelemetry.StartAddFindAppHost(passedAppHostProjectFile))
            {
                searchResult = await _projectLocator.UseOrFindAppHostProjectFileAsync(passedAppHostProjectFile, MultipleAppHostProjectsFoundBehavior.Prompt, createSettingsFile: false, cancellationToken);
                findAppHostActivity.SetAppHostCandidateCount(searchResult.AllProjectFileCandidates.Count);
            }
            addActivity.SetAppHostCandidateCount(searchResult.AllProjectFileCandidates.Count);

            var effectiveAppHostProjectFile = searchResult.SelectedProjectFile;

            if (effectiveAppHostProjectFile is null)
            {
                return AddCommandFailure(CliExitCodes.FailedToFindProject);
            }

            if (TryGetMissingLocalNuGetSource(source, effectiveAppHostProjectFile.Directory!, out var missingSource))
            {
                InteractionService.DisplayError(string.Format(CultureInfo.CurrentCulture, AddCommandStrings.SourceDoesNotExist, missingSource));
                return CommandResult.FromExitCode(CliExitCodes.FailedToAddPackage);
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

            List<(NuGetPackage Package, PackageChannel Channel)> packagesWithChannels;
            var (discoveryScope, discoveryScopeExitCode) = await ResolveDiscoveryScopeAsync(
                effectiveAppHostProjectFile.Directory!,
                requestedDiscoveryScope,
                integrationName,
                cancellationToken);
            if (discoveryScopeExitCode is { } scopeExitCode)
            {
                return AddCommandFromExitCode(scopeExitCode);
            }

            using (var searchPackagesActivity = _profilingTelemetry.StartAddSearchPackages(configuredChannel))
            {
                var discoveredPackages = await InteractionService.ShowStatusAsync(
                    AddCommandStrings.SearchingForAspirePackages,
                    async () => await _integrationPackageSearchService.GetIntegrationPackagesWithChannelsAsync(effectiveAppHostProjectFile.Directory!, configuredChannel, discoveryScope, source, cancellationToken));
                packagesWithChannels = discoveredPackages as List<(NuGetPackage Package, PackageChannel Channel)> ?? discoveredPackages.ToList();
                var packageCount = packagesWithChannels.Count;
                searchPackagesActivity.SetAddPackageSearchResultCount(packageCount);
                addActivity.SetAddPackageSearchResultCount(packageCount);
            }

            if (!packagesWithChannels.Any() && integrationName is null)
            {
                throw new EmptyChoicesException(AddCommandStrings.NoIntegrationPackagesFound);
            }

            var packagesWithShortName = packagesWithChannels.Select(IntegrationPackageSearchService.GenerateFriendlyName).OrderBy(p => p.FriendlyName, StringComparer.OrdinalIgnoreCase).ToList();

            if (integrationName is null && _hostEnvironment.SupportsInteractiveInput)
            {
                packagesWithShortName = [.. await ExcludeInstalledPackagesAsync(effectiveAppHostProjectFile, project, packagesWithShortName, cancellationToken)];
            }

            if (!packagesWithShortName.Any() && integrationName is null)
            {
                return AddCommandFailure(CliExitCodes.FailedToAddPackage, AddCommandStrings.NoPackagesFound);
            }

            if (integrationName is null && !_hostEnvironment.SupportsInteractiveInput)
            {
                InteractionService.DisplayError(AddCommandStrings.IntegrationNameRequiredInNonInteractiveMode);
                return CommandResult.FromExitCode(CliExitCodes.FailedToAddPackage);
            }

            var filteredPackagesWithShortName = packagesWithShortName
                .Where(p => p.FriendlyName == integrationName || p.Package.Id == integrationName)
                .ToList();
            var packageMatchKind = filteredPackagesWithShortName.Count > 0
                ? ProfilingTelemetry.Values.AddPackageMatchKindExact
                : ProfilingTelemetry.Values.AddPackageMatchKindNone;

            List<(string FriendlyName, NuGetPackage Package, PackageChannel Channel)> exactPackageIdMatches = [];

            if (!filteredPackagesWithShortName.Any() && integrationName is not null)
            {
                exactPackageIdMatches =
                [
                    .. (await _integrationPackageSearchService.GetPackagesByExactIdWithChannelsAsync(
                        effectiveAppHostProjectFile.Directory!,
                        integrationName,
                        configuredChannel,
                        GetExactPackageIdDiscoveryScope(discoveryScope, integrationName),
                        cancellationToken,
                        source))
                    .Select(IntegrationPackageSearchService.GenerateFriendlyName)
                ];

                if (exactPackageIdMatches.Count > 0)
                {
                    filteredPackagesWithShortName = exactPackageIdMatches;
                }
            }

            if (!filteredPackagesWithShortName.Any() && integrationName is not null && discoveryScope.IncludesGroup(IntegrationSearchResultGroup.Microsoft) && TryGetBuiltInHostingPackageIdCandidate(integrationName, out var builtInPackageIdCandidate))
            {
                var builtInPackageMatches = (await _integrationPackageSearchService.SearchBuiltInPackagesByExactIdWithChannelsAsync(
                        effectiveAppHostProjectFile.Directory!,
                        builtInPackageIdCandidate,
                        configuredChannel,
                        cancellationToken,
                        source))
                    .Select(IntegrationPackageSearchService.GenerateFriendlyName)
                    .ToList();

                if (builtInPackageMatches.Count > 0)
                {
                    filteredPackagesWithShortName = builtInPackageMatches;
                }
            }

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
                IEnumerable<(string FriendlyName, NuGetPackage Package, PackageChannel Channel)> fuzzySearchSource = _hostEnvironment.SupportsInteractiveInput
                    ? await ExcludeInstalledPackagesAsync(effectiveAppHostProjectFile, project, packagesWithShortName, cancellationToken)
                    : packagesWithShortName;

                filteredPackagesWithShortName = IntegrationPackageSearchService.GetIntegrationSearchMatches(fuzzySearchSource, integrationName)
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
            (string FriendlyName, NuGetPackage Package, PackageChannel Channel) selectedNuGetPackage;
            selectedNuGetPackage = filteredPackagesWithShortName.Count switch
            {
                0 => await GetPackageByInteractiveFlowWithNoMatchesMessage(
                    effectiveAppHostProjectFile.Directory!,
                    packagesWithShortName,
                    integrationName,
                    version,
                    configuredChannel,
                    cancellationToken,
                    promptForSinglePackage: true),
                1 when packageMatchKind == ProfilingTelemetry.Values.AddPackageMatchKindExact
                    && filteredPackagesWithShortName[0].Package.Version == version
                    => filteredPackagesWithShortName[0],
                _ => await GetPackageByInteractiveFlow(
                    effectiveAppHostProjectFile.Directory!,
                    filteredPackagesWithShortName,
                    version,
                    configuredChannel,
                    cancellationToken,
                    promptForSingleFuzzyPackage)
            };
            using (var selectPackageActivity = _profilingTelemetry.StartAddSelectPackage(integrationName, version))
            {
                selectPackageActivity.SetAddPackageMatch(filteredPackagesWithShortName.Count, packageMatchKind);
                selectPackageActivity.SetAddSelectedPackage(selectedNuGetPackage.Package.Id, selectedNuGetPackage.Package.Version, selectedNuGetPackage.Channel.Name);
                addActivity.SetAddSelectedPackage(selectedNuGetPackage.Package.Id, selectedNuGetPackage.Package.Version, selectedNuGetPackage.Channel.Name);
            }

            if (IntegrationDisplayHelpers.GetIntegrationGroup(selectedNuGetPackage.Package.Id) is IntegrationSearchResultGroup.ThirdParty)
            {
                var thirdPartyConfirmed = await ConfirmThirdPartyIntegrationAsync(selectedNuGetPackage.Package.Id, cancellationToken);
                if (!thirdPartyConfirmed)
                {
                    InteractionService.DisplayError(AddCommandStrings.ThirdPartyIntegrationDeclined);
                    return CommandResult.FromExitCode(CliExitCodes.FailedToAddPackage);
                }
            }

            await EnsureLocalBuildChannelNuGetConfigAsync(source, selectedNuGetPackage.Channel, effectiveAppHostProjectFile.Directory!, cancellationToken);

            context = new AddPackageContext
            {
                AppHostFile = effectiveAppHostProjectFile,
                PackageId = selectedNuGetPackage.Package.Id,
                PackageVersion = selectedNuGetPackage.Package.Version,
                Source = GetPackageSourceForInstall(source, selectedNuGetPackage.Package, selectedNuGetPackage.Channel)
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
                    async () => await project.AddPackageAsync(context, cancellationToken)
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

    private async Task<(IntegrationDiscoveryScope Scope, int? ExitCode)> ResolveDiscoveryScopeAsync(DirectoryInfo workingDirectory, string? requestedDiscoveryScope, string? integrationName, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(requestedDiscoveryScope))
        {
            if (IntegrationDiscoveryScopeHelpers.TryParse(requestedDiscoveryScope, out var parsedScope))
            {
                return (parsedScope, ExitCode: null);
            }

            InteractionService.DisplayError(string.Format(CultureInfo.CurrentCulture, AddCommandStrings.InvalidDiscoveryScope, requestedDiscoveryScope));
            return (IntegrationDiscoveryScope.Official, CliExitCodes.FailedToAddPackage);
        }

        var mode = IntegrationDiscoveryScopeHelpers.GetConfiguredThirdPartyMode(workingDirectory);
        if (mode is ThirdPartyIntegrationDiscoveryMode.Ask && _hostEnvironment.SupportsInteractiveInput && integrationName is null)
        {
            var choices = new[]
            {
                (Scope: IntegrationDiscoveryScope.Official, Label: AddCommandStrings.DiscoveryScopeOfficialOnly),
                (Scope: IntegrationDiscoveryScope.All, Label: AddCommandStrings.DiscoveryScopeIncludeThirdParty)
            };

            var selectedChoice = await InteractionService.PromptForSelectionAsync(
                AddCommandStrings.SelectIntegrationDiscoveryScope,
                choices,
                static choice => choice.Label,
                cancellationToken: cancellationToken);

            return (selectedChoice.Scope, ExitCode: null);
        }

        return (IntegrationDiscoveryScopeHelpers.GetConfiguredScope(workingDirectory), ExitCode: null);
    }

    private async Task<IOrderedEnumerable<(string FriendlyName, NuGetPackage Package, PackageChannel Channel)>> ExcludeInstalledPackagesAsync(
        FileInfo appHostProjectFile,
        IAppHostProject project,
        IEnumerable<(string FriendlyName, NuGetPackage Package, PackageChannel Channel)> packages,
        CancellationToken cancellationToken)
    {
        var installedPackageIds = await GetInstalledPackageIdsAsync(appHostProjectFile, project, cancellationToken);
        if (installedPackageIds.Count == 0)
        {
            return packages.OrderBy(package => package.FriendlyName, StringComparer.OrdinalIgnoreCase);
        }

        return packages
            .Where(package => !installedPackageIds.Contains(package.Package.Id))
            .OrderBy(package => package.FriendlyName, StringComparer.OrdinalIgnoreCase);
    }

    private async Task<HashSet<string>> GetInstalledPackageIdsAsync(FileInfo appHostProjectFile, IAppHostProject project, CancellationToken cancellationToken)
    {
        if (project.LanguageId is not KnownLanguageId.CSharp)
        {
            return [];
        }

        var (exitCode, output) = await _runner.GetProjectItemsAndPropertiesAsync(
            appHostProjectFile,
            ["PackageReference"],
            [],
            [],
            new ProcessInvocationOptions { SuppressLogging = true },
            cancellationToken);

        if (exitCode != 0 || output is null)
        {
            return [];
        }

        var installedPackageIds = new HashSet<string>(StringComparers.NuGetPackageId);
        if (output.RootElement.TryGetProperty("Items", out var items) &&
            items.TryGetProperty("PackageReference", out var packageReferences))
        {
            foreach (var packageReference in packageReferences.EnumerateArray())
            {
                if (packageReference.TryGetProperty("Identity", out var identity) && identity.GetString() is { } packageId)
                {
                    installedPackageIds.Add(packageId);
                }
            }
        }

        return installedPackageIds;
    }

    private static IntegrationDiscoveryScope GetExactPackageIdDiscoveryScope(IntegrationDiscoveryScope discoveryScope, string integrationName)
    {
        if (discoveryScope is IntegrationDiscoveryScope.Official && integrationName.Contains('.', StringComparison.Ordinal))
        {
            return IntegrationDiscoveryScope.All;
        }

        return discoveryScope;
    }

    private async Task<bool> ConfirmThirdPartyIntegrationAsync(string packageId, CancellationToken cancellationToken)
    {
        if (!_hostEnvironment.SupportsInteractiveInput)
        {
            return false;
        }

        var choices = new[]
        {
            (Confirmed: false, Label: AddCommandStrings.ThirdPartyIntegrationConfirmationNo),
            (Confirmed: true, Label: AddCommandStrings.ThirdPartyIntegrationConfirmationYes)
        };

        InteractionService.DisplayEmptyLine();

        var selectedChoice = await InteractionService.PromptForSelectionAsync(
            string.Format(CultureInfo.CurrentCulture, AddCommandStrings.ThirdPartyIntegrationConfirmationPrompt, packageId),
            choices,
            static choice => choice.Label,
            cancellationToken: cancellationToken);

        return selectedChoice.Confirmed;
    }

    private static string? GetPackageSourceForInstall(string? requestedSource, NuGetPackage package, PackageChannel channel)
    {
        if (!string.IsNullOrWhiteSpace(requestedSource))
        {
            return requestedSource;
        }

        if (channel.Type is PackageChannelType.Implicit)
        {
            return null;
        }

        if (VersionHelper.IsLocalBuildChannel(channel.Name) || channel.IsBackedByLocalPackageDirectory)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(package.Source))
        {
            return package.Source;
        }

        return channel.Mappings?
            .Where(mapping => mapping.MatchesPackageId(package.Id))
            .Select(mapping => mapping.Source)
            .FirstOrDefault(source => !string.IsNullOrWhiteSpace(source));
    }

    private async Task EnsureLocalBuildChannelNuGetConfigAsync(string? requestedSource, PackageChannel channel, DirectoryInfo projectDirectory, CancellationToken cancellationToken)
    {
        // When installing from a PR/local channel, ensure the project has access to the local
        // package directory as a NuGet source so `dotnet add package` can resolve packages that
        // are not available from the ambient feeds.
        if (!string.IsNullOrEmpty(requestedSource) ||
            (!VersionHelper.IsLocalBuildChannel(channel.Name) && !channel.IsBackedByLocalPackageDirectory))
        {
            return;
        }

        var mappings = channel.Mappings;
        if (mappings is not { Length: > 0 })
        {
            return;
        }

        var hiveSources = mappings
            .Select(mapping => mapping.Source)
            .Where(source => !source.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (hiveSources.Length == 0)
        {
            return;
        }

        if (!projectDirectory.Exists)
        {
            projectDirectory.Create();
        }

        if (NuGetConfigMerger.TryFindNuGetConfigInDirectory(projectDirectory, out _))
        {
            return;
        }

        var nugetConfigFile = new FileInfo(Path.Combine(projectDirectory.FullName, "nuget.config"));
        var configXml = new System.Xml.Linq.XDocument(
            new System.Xml.Linq.XElement("configuration",
                new System.Xml.Linq.XElement("packageSources",
                    hiveSources.Select(source =>
                        new System.Xml.Linq.XElement("add",
                            new System.Xml.Linq.XAttribute("key", source),
                            new System.Xml.Linq.XAttribute("value", source))))));

        await using var stream = nugetConfigFile.Create();
        await configXml.SaveAsync(stream, System.Xml.Linq.SaveOptions.None, cancellationToken);
        InteractionService.DisplayMessage(KnownEmojis.Package, TemplatingStrings.NuGetConfigCreatedOrUpdatedConfirmationMessage);
    }

    private static bool TryGetBuiltInHostingPackageIdCandidate(string integrationName, out string packageId)
    {
        packageId = string.Empty;

        if (integrationName.Contains('.', StringComparison.Ordinal))
        {
            return false;
        }

        packageId = $"Aspire.Hosting.{integrationName.Replace('-', '.')}";

        return true;
    }

    private static bool TryGetMissingLocalNuGetSource(string? source, DirectoryInfo workingDirectory, out string missingSource)
    {
        missingSource = string.Empty;

        if (string.IsNullOrWhiteSpace(source))
        {
            return false;
        }

        var sourcePath = GetLocalNuGetSourcePath(source, workingDirectory);
        if (sourcePath is null || Directory.Exists(sourcePath))
        {
            return false;
        }

        missingSource = sourcePath;

        return true;
    }

    private static string? GetLocalNuGetSourcePath(string source, DirectoryInfo workingDirectory)
    {
        if (Path.IsPathFullyQualified(source))
        {
            return source;
        }

        if (Uri.TryCreate(source, UriKind.Absolute, out var sourceUri))
        {
            return sourceUri.IsFile ? sourceUri.LocalPath : null;
        }

        if (source.IndexOf(Path.DirectorySeparatorChar) < 0 && source.IndexOf(Path.AltDirectorySeparatorChar) < 0)
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(Path.Combine(workingDirectory.FullName, source));
        }
        catch (ArgumentException)
        {
            return source;
        }
        catch (NotSupportedException)
        {
            return source;
        }
        catch (PathTooLongException)
        {
            return source;
        }
    }

    private static async Task<IEnumerable<(string FriendlyName, NuGetPackage Package, PackageChannel Channel)>> GetAllPackageVersions(DirectoryInfo workingDirectory, IEnumerable<(string FriendlyName, NuGetPackage Package, PackageChannel Channel)> possiblePackages, CancellationToken cancellationToken)
    {
        var distinctPackageIds = possiblePackages.DistinctBy(package => package.Package.Id);
        var channels = possiblePackages.Select(package => package.Channel).Distinct();

        var versions = new List<(string FriendlyName, NuGetPackage Package, PackageChannel Channel)>();
        foreach (var channel in channels)
        {
            foreach (var package in distinctPackageIds)
            {
                var packages = await channel.GetPackageVersionsAsync(package.Package.Id, workingDirectory, cancellationToken);
                versions.AddRange(packages.Select(p => (FriendlyName: package.FriendlyName, Package: p, Channel: channel)));
            }
        }
        return versions;
    }

    private async Task<(string FriendlyName, NuGetPackage Package, PackageChannel Channel)> GetPackageByInteractiveFlow(
        DirectoryInfo workingDirectory,
        IEnumerable<(string FriendlyName, NuGetPackage Package, PackageChannel Channel)> possiblePackages,
        string? preferredVersion,
        string? configuredChannel,
        CancellationToken cancellationToken,
        bool promptForSinglePackage = false)
    {
        var distinctPackages = possiblePackages.DistinctBy(p => p.Package.Id).ToArray();

        // Exact matches can skip the package prompt when one package remains. Fuzzy/no-match
        // fallbacks opt into prompting so interactive users confirm the candidate first.
        // In non-interactive mode, auto-select the first package.
        var selectedPackage = distinctPackages.Length switch
        {
            1 when promptForSinglePackage && _hostEnvironment.SupportsInteractiveInput => await PromptForIntegrationAsync(distinctPackages, cancellationToken),
            1 => distinctPackages.First(),
            > 1 when !_hostEnvironment.SupportsInteractiveInput => distinctPackages.First(),
            > 1 => await PromptForIntegrationAsync(distinctPackages, cancellationToken),
            _ => throw new InvalidOperationException(AddCommandStrings.UnexpectedNumberOfPackagesFound)
        };

        var packageVersions = possiblePackages.Where(p => p.Package.Id == selectedPackage.Package.Id).ToArray();

        // If any of the package versions are an exact match for the preferred version
        // then we can skip the version prompt and just use that version.
        if (!string.IsNullOrEmpty(preferredVersion))
        {
            if (packageVersions.Any(p => p.Package.Version == preferredVersion))
            {
                var preferredVersionPackage = packageVersions.First(p => p.Package.Version == preferredVersion);
                return preferredVersionPackage;
            }

            var allVersions = await InteractionService.ShowStatusAsync(
                string.Format(CultureInfo.CurrentCulture, AddCommandStrings.SearchingForSpecifiedPackageVersion, selectedPackage.Package.Id, preferredVersion),
                async () => await GetAllPackageVersions(workingDirectory, packageVersions, cancellationToken));
            var matchedPreferredVersionPackage = allVersions.FirstOrDefault(packageVersion => packageVersion.Package.Version == preferredVersion);
            if (matchedPreferredVersionPackage.Package is not null)
            {
                return matchedPreferredVersionPackage;
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

        // When local build hives are present, prefer the package that exactly matches the
        // installed CLI/SDK version so template- and add-generated projects stay on the same
        // build. Apply this before the interactive prompt so pressing Enter does not
        // accidentally downgrade to the implicit feed when a matching hive package exists.
        if (VersionHelper.TryGetCurrentCliVersionMatch(
            prChannelPackageVersions,
            p => p.Package.Version,
            ExecutionContext.IdentitySdkVersion,
            out var cliVersionPackage,
            channelName: null,
            hasPrHives: ExecutionContext.GetHiveCount() > 0 || ExecutionContext.IdentityPackagesDirectory is not null))
        {
            return cliVersionPackage;
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
            return orderedPackageVersions.First();
        }

        // ... otherwise we had better prompt.
        var version = await PromptForIntegrationVersionAsync(orderedPackageVersions, configuredChannel, cancellationToken);

        return version;
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

    private async Task<(string FriendlyName, NuGetPackage Package, PackageChannel Channel)> GetPackageByInteractiveFlowWithNoMatchesMessage(
        DirectoryInfo workingDirectory,
        IEnumerable<(string FriendlyName, NuGetPackage Package, PackageChannel Channel)> possiblePackages,
        string? searchTerm,
        string? preferredVersion,
        string? configuredChannel,
        CancellationToken cancellationToken,
        bool promptForSinglePackage = false)
    {
        if (searchTerm is not null)
        {
            InteractionService.DisplaySubtleMessage(string.Format(CultureInfo.CurrentCulture, AddCommandStrings.NoPackagesMatchedSearchTerm, searchTerm));
        }

        return await GetPackageByInteractiveFlow(workingDirectory, possiblePackages, preferredVersion, configuredChannel, cancellationToken, promptForSinglePackage);
    }

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
        var firstPackage = packages.First();

        // Helper to keep labels consistently formatted: "Version (source)"
        static string FormatVersionLabel((string FriendlyName, NuGetPackage Package, PackageChannel Channel) item)
        {
            return $"{item.Package.Version.EscapeMarkup()} ({item.Channel.SourceDetails.EscapeMarkup()})";
        }

        async Task<(string FriendlyName, NuGetPackage Package, PackageChannel Channel)> PromptForChannelPackagesAsync(
            PackageChannel channel,
            IEnumerable<(string FriendlyName, NuGetPackage Package, PackageChannel Channel)> items,
            CancellationToken ct)
        {
            var choices = items
                .Select(i => (
                    Label: FormatVersionLabel(i),
                    Result: i))
                .ToArray();

            if (choices.Length == 1)
            {
                return choices[0].Result;
            }

            var selection = await interactionService.PromptForSelectionAsync(
                string.Format(CultureInfo.CurrentCulture, AddCommandStrings.SelectAVersionOfPackage, firstPackage.Package.Id),
                choices,
                c => c.Label,
                cancellationToken: ct);

            return selection.Result;
        }

        var byChannel = packages
            .GroupBy(p => p.Channel)
            .Select(g => new
            {
                Channel = g.Key,
                HighestVersion = g.OrderByDescending(p => SemVersion.Parse(p.Package.Version), SemVersion.PrecedenceComparer).First()
            })
            .ToArray();

        var implicitGroup = byChannel.FirstOrDefault(g => g.Channel.Type is PackageChannelType.Implicit);
        var explicitGroups = byChannel
            .Where(g => g.Channel.Type is PackageChannelType.Explicit)
            .ToArray();

        if (explicitGroups.Length == 0 && implicitGroup is not null)
        {
            return implicitGroup.HighestVersion;
        }

        // Build the root menu. When the apphost has pinned a channel (e.g. a polyglot apphost that
        // persists `channel: daily` in aspire.config.json), surface that channel's package as the first
        // option so it becomes the default selection — the pinned channel reflects explicit user intent
        // and its feed is the one the project will actually restore from. Otherwise the implicit/ambient
        // channel can shadow the pinned feed and offer a version the project can't restore. See
        // https://github.com/microsoft/aspire/issues/18114.
        var rootChoices = new List<(string Label, Func<CancellationToken, Task<(string FriendlyName, NuGetPackage Package, PackageChannel Channel)>> Action)>();

        var configuredGroup = string.IsNullOrEmpty(configuredChannel)
            ? null
            : explicitGroups.FirstOrDefault(g => string.Equals(g.Channel.Name, configuredChannel, StringComparisons.ChannelName));

        if (configuredGroup is not null)
        {
            var channel = configuredGroup.Channel;
            var item = configuredGroup.HighestVersion;

            rootChoices.Add((
                Label: channel.Name.EscapeMarkup(),
                Action: ct => PromptForChannelPackagesAsync(channel, new[] { item }, ct)
            ));
        }

        if (implicitGroup is not null)
        {
            var captured = implicitGroup.HighestVersion;
            rootChoices.Add((
                Label: FormatVersionLabel(captured),
                Action: ct => Task.FromResult(captured)));
        }

        foreach (var channelGroup in explicitGroups)
        {
            // The pinned channel (if any) was already added above as the first/default choice.
            if (ReferenceEquals(channelGroup, configuredGroup))
            {
                continue;
            }

            var channel = channelGroup.Channel;
            var item = channelGroup.HighestVersion;

            rootChoices.Add((
                Label: channel.Name.EscapeMarkup(),
                Action: ct => PromptForChannelPackagesAsync(channel, [item], ct)));
        }

        if (rootChoices.Count == 0)
        {
            return firstPackage;
        }

        if (rootChoices.Count == 1)
        {
            return await rootChoices[0].Action(cancellationToken);
        }

        var topSelection = await interactionService.PromptForSelectionAsync(
            string.Format(CultureInfo.CurrentCulture, AddCommandStrings.SelectAVersionOfPackage, firstPackage.Package.Id),
            rootChoices,
            c => c.Label,
            cancellationToken: cancellationToken);

        return await topSelection.Action(cancellationToken);
    }

    public virtual async Task<(string FriendlyName, NuGetPackage Package, PackageChannel Channel)> PromptForIntegrationAsync(IEnumerable<(string FriendlyName, NuGetPackage Package, PackageChannel Channel)> packages, CancellationToken cancellationToken)
    {
        // Filter to show only the highest version for each package ID
        var filteredPackages = packages
            .GroupBy(p => p.Package.Id)
            .Select(g => g.OrderByDescending(p => SemVersion.Parse(p.Package.Version), SemVersion.PrecedenceComparer).First())
            .OrderBy(static p => IntegrationDisplayHelpers.GetIntegrationGroup(p.Package.Id))
            .ThenBy(static p => p.FriendlyName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var selectedIntegration = await interactionService.PromptForSelectionAsync(
            CreateIntegrationSelectionPromptTitle(),
            filteredPackages,
            FormatIntegrationSelectionLabel,
            cancellationToken: cancellationToken);
        return selectedIntegration;
    }

    private static string CreateIntegrationSelectionPromptTitle()
    {
        return string.Create(CultureInfo.CurrentCulture, $"{AddCommandStrings.SelectAnIntegrationToAdd}{Environment.NewLine}[dim]{FormatPromptColumn(AddCommandStrings.HeaderGroup, 17)}  {FormatPromptColumn(AddCommandStrings.HeaderName, 30)}  {FormatPromptColumn(AddCommandStrings.HeaderPackage, 44)}  {AddCommandStrings.HeaderVersion}[/]");
    }

    private static string FormatIntegrationSelectionLabel((string FriendlyName, NuGetPackage Package, PackageChannel Channel) packageWithFriendlyName)
    {
        var group = IntegrationDisplayHelpers.GetIntegrationGroup(packageWithFriendlyName.Package.Id);
        var groupName = IntegrationDisplayHelpers.GetIntegrationGroupTitle(group);

        return string.Create(
            CultureInfo.CurrentCulture,
            $"[dim]{FormatPromptColumn(groupName, 17).EscapeMarkup()}[/]  [bold]{FormatPromptColumn(packageWithFriendlyName.FriendlyName, 30).EscapeMarkup()}[/]  [dim]{FormatPromptColumn(packageWithFriendlyName.Package.Id, 44).EscapeMarkup()}[/]  {packageWithFriendlyName.Package.Version.EscapeMarkup()}");
    }

    private static string FormatPromptColumn(string value, int width)
    {
        const string ellipsis = "...";

        var formattedValue = value.Length > width
            ? string.Concat(value.AsSpan(0, width - ellipsis.Length), ellipsis)
            : value;

        return formattedValue.PadRight(width);
    }
}
