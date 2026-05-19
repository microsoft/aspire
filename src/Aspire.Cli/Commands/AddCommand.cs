// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.Globalization;
using Aspire.Cli.Configuration;
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

    private readonly IProjectLocator _projectLocator;
    private readonly IntegrationPackageSearchService _integrationPackageSearchService;
    private readonly IAddCommandPrompter _prompter;
    private readonly IDotNetSdkInstaller _sdkInstaller;
    private readonly IDotNetCliRunner _runner;
    private readonly ICliHostEnvironment _hostEnvironment;
    private readonly IAppHostProjectFactory _projectFactory;

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

    public AddCommand(IInteractionService interactionService, IProjectLocator projectLocator, IntegrationPackageSearchService integrationPackageSearchService, IAddCommandPrompter prompter, AspireCliTelemetry telemetry, IDotNetSdkInstaller sdkInstaller, IDotNetCliRunner runner, IFeatures features, ICliUpdateNotifier updateNotifier, CliExecutionContext executionContext, ICliHostEnvironment hostEnvironment, IAppHostProjectFactory projectFactory)
        : base("add", AddCommandStrings.Description, features, updateNotifier, executionContext, interactionService, telemetry)
    {
        _projectLocator = projectLocator;
        _integrationPackageSearchService = integrationPackageSearchService;
        _prompter = prompter;
        _sdkInstaller = sdkInstaller;
        _runner = runner;
        _hostEnvironment = hostEnvironment;
        _projectFactory = projectFactory;

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

        try
        {
            var integrationName = parseResult.GetValue(s_integrationArgument);
            var passedAppHostProjectFile = parseResult.GetValue(s_appHostOption);
            var version = parseResult.GetValue(s_versionOption);
            var source = parseResult.GetValue(s_sourceOption);
            var requestedDiscoveryScope = parseResult.GetValue(s_discoveryScopeOption);

            var searchResult = await _projectLocator.UseOrFindAppHostProjectFileAsync(passedAppHostProjectFile, MultipleAppHostProjectsFoundBehavior.Prompt, createSettingsFile: false, cancellationToken);
            var effectiveAppHostProjectFile = searchResult.SelectedProjectFile;

            if (effectiveAppHostProjectFile is null)
            {
                return CommandResult.Failure(CliExitCodes.FailedToFindProject);
            }

            if (TryGetMissingLocalNuGetSource(source, effectiveAppHostProjectFile.Directory!, out var missingSource))
            {
                InteractionService.DisplayError(string.Format(CultureInfo.CurrentCulture, AddCommandStrings.SourceDoesNotExist, missingSource));
                return CommandResult.FromExitCode(CliExitCodes.FailedToAddPackage);
            }

            // Get the appropriate project handler
            var project = _projectFactory.GetProject(effectiveAppHostProjectFile);

            // Check if the .NET SDK is available (only needed for .NET projects)
            if (project.LanguageId == KnownLanguageId.CSharp)
            {
                if (!await SdkInstallHelper.EnsureSdkInstalledAsync(_sdkInstaller, InteractionService, Telemetry, cancellationToken: cancellationToken))
                {
                    return CommandResult.Failure(CliExitCodes.SdkNotInstalled);
                }
            }

            var (configuredChannel, configuredChannelExitCode) = _integrationPackageSearchService.GetConfiguredChannel(effectiveAppHostProjectFile, project);
            if (configuredChannelExitCode is { } exitCode)
            {
                return CommandResult.FromExitCode(exitCode);
            }

            var (discoveryScope, discoveryScopeExitCode) = await ResolveDiscoveryScopeAsync(
                effectiveAppHostProjectFile.Directory!,
                requestedDiscoveryScope,
                integrationName,
                cancellationToken);
            if (discoveryScopeExitCode is { } scopeExitCode)
            {
                return CommandResult.FromExitCode(scopeExitCode);
            }

            var packagesWithChannels = await InteractionService.ShowStatusAsync(
                AddCommandStrings.SearchingForAspirePackages,
                async () => await _integrationPackageSearchService.GetIntegrationPackagesWithChannelsAsync(effectiveAppHostProjectFile.Directory!, configuredChannel, discoveryScope, source, cancellationToken));

            if (!packagesWithChannels.Any() && integrationName is null)
            {
                throw new EmptyChoicesException(AddCommandStrings.NoIntegrationPackagesFound);
            }

            var packagesWithShortName = packagesWithChannels.Select(IntegrationPackageSearchService.GenerateFriendlyName).OrderBy(p => p.FriendlyName, StringComparer.OrdinalIgnoreCase);

            if (integrationName is null && _hostEnvironment.SupportsInteractiveInput)
            {
                packagesWithShortName = await ExcludeInstalledPackagesAsync(effectiveAppHostProjectFile, project, packagesWithShortName, cancellationToken);
            }

            if (!packagesWithShortName.Any() && integrationName is null)
            {
                return CommandResult.Failure(CliExitCodes.FailedToAddPackage, AddCommandStrings.NoPackagesFound);
            }

            if (integrationName is null && !_hostEnvironment.SupportsInteractiveInput)
            {
                InteractionService.DisplayError(AddCommandStrings.IntegrationNameRequiredInNonInteractiveMode);
                return CommandResult.FromExitCode(CliExitCodes.FailedToAddPackage);
            }

            var filteredPackagesWithShortName = packagesWithShortName
                .Where(p => p.FriendlyName == integrationName || p.Package.Id == integrationName);

            var exactPackageIdMatches = Array.Empty<(string FriendlyName, NuGetPackage Package, PackageChannel Channel)>();

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

                if (exactPackageIdMatches.Length > 0)
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
                    .ToArray();

                if (builtInPackageMatches.Length > 0)
                {
                    filteredPackagesWithShortName = builtInPackageMatches;
                }
            }

            if (!filteredPackagesWithShortName.Any() && integrationName is not null && version is not null && !_hostEnvironment.SupportsInteractiveInput)
            {
                throw new EmptyChoicesException(string.Format(CultureInfo.CurrentCulture, AddCommandStrings.SpecifiedVersionRequiresExactPackageMatch, integrationName));
            }

            if (!filteredPackagesWithShortName.Any() && integrationName is not null)
            {
                // If we didn't get an exact match on the friendly name or the package ID
                // then try a fuzzy search to create a broader filtered list.
                // Materialize the query with ToList() to avoid multiple enumerations
                // (which would recalculate fuzzy scores on each Count()/First() call).
                var fuzzySearchSource = _hostEnvironment.SupportsInteractiveInput
                    ? await ExcludeInstalledPackagesAsync(effectiveAppHostProjectFile, project, packagesWithShortName, cancellationToken)
                    : packagesWithShortName;

                filteredPackagesWithShortName = IntegrationPackageSearchService.GetIntegrationSearchMatches(fuzzySearchSource, integrationName)
                    .Select(x => (x.FriendlyName, x.Package, x.Channel))
                    .ToList();
            }

            if (!filteredPackagesWithShortName.Any() && integrationName is not null && !_hostEnvironment.SupportsInteractiveInput)
            {
                InteractionService.DisplayError(string.Format(CultureInfo.CurrentCulture, AddCommandStrings.NoPackagesMatchedSearchTerm, integrationName));
                return CommandResult.FromExitCode(CliExitCodes.FailedToAddPackage);
            }

            // If we didn't match any, show a complete list. If we matched one, and its
            // an exact match, then we still prompt, but it will only prompt for
            // the version. If there is more than one match then we prompt.
            var selectedNuGetPackage = filteredPackagesWithShortName.Count() switch
            {
                0 when integrationName is null => await GetPackageByInteractiveFlow(effectiveAppHostProjectFile.Directory!, packagesWithShortName, version, cancellationToken, forcePackageSelection: true),
                0 => await GetPackageByInteractiveFlowWithNoMatchesMessage(effectiveAppHostProjectFile.Directory!, packagesWithShortName, integrationName, version, cancellationToken),
                1 when filteredPackagesWithShortName.First().Package.Version == version
                    => filteredPackagesWithShortName.First(),
                _ => await GetPackageByInteractiveFlow(effectiveAppHostProjectFile.Directory!, filteredPackagesWithShortName, version, cancellationToken)
            };

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
            var runningInstanceResult = await project.FindAndStopRunningInstanceAsync(
                effectiveAppHostProjectFile,
                ExecutionContext.HomeDirectory,
                cancellationToken);

            if (runningInstanceResult == RunningInstanceResult.InstanceStopped)
            {
                InteractionService.DisplayMessage(KnownEmojis.Information, AddCommandStrings.StoppedRunningInstance);
            }
            else if (runningInstanceResult == RunningInstanceResult.StopFailed)
            {
                return CommandResult.Failure(CliExitCodes.FailedToAddPackage, AddCommandStrings.UnableToStopRunningInstances);
            }

            var success = await InteractionService.ShowStatusAsync(
                AddCommandStrings.AddingAspireIntegration,
                async () => await project.AddPackageAsync(context, cancellationToken)
            );

            if (!success)
            {
                if (context.OutputCollector is { } outputCollector)
                {
                    InteractionService.DisplayLines(outputCollector.GetLines());
                }
                return CommandResult.Failure(CliExitCodes.FailedToAddPackage, string.Format(CultureInfo.CurrentCulture, AddCommandStrings.PackageInstallationFailed, CliExitCodes.FailedToAddPackage));
            }

            InteractionService.DisplaySuccess(string.Format(CultureInfo.CurrentCulture, AddCommandStrings.PackageAddedSuccessfully, selectedNuGetPackage.Package.Id, selectedNuGetPackage.Package.Version));
            return CommandResult.Success();
        }
        catch (ProjectLocatorException ex)
        {
            return HandleProjectLocatorException(ex, InteractionService, Telemetry);
        }
        catch (OperationCanceledException)
        {
            return CommandResult.Cancelled();
        }
        catch (EmptyChoicesException ex)
        {
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
            Telemetry.RecordError(errorMessage, ex);
            return CommandResult.Failure(CliExitCodes.FailedToAddPackage, errorMessage);
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

        if (VersionHelper.IsLocalBuildChannel(channel.Name))
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
        if (!string.IsNullOrEmpty(requestedSource) || !VersionHelper.IsLocalBuildChannel(channel.Name))
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

    private async Task<(string FriendlyName, NuGetPackage Package, PackageChannel Channel)> GetPackageByInteractiveFlow(DirectoryInfo workingDirectory, IEnumerable<(string FriendlyName, NuGetPackage Package, PackageChannel Channel)> possiblePackages, string? preferredVersion, CancellationToken cancellationToken, bool forcePackageSelection = false)
    {
        var distinctPackages = possiblePackages.DistinctBy(p => p.Package.Id);

        // If there is only one package for a named integration, we can skip the package prompt and just use it.
        // Without a named integration, still prompt so nothing is added without an explicit selection.
        var selectedPackage = distinctPackages.Count() switch
        {
            1 when !forcePackageSelection => distinctPackages.First(),
            > 1 when !_hostEnvironment.SupportsInteractiveInput => distinctPackages.First(),
            >= 1 => await _prompter.PromptForIntegrationAsync(distinctPackages, cancellationToken),
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

        var hasHives = ExecutionContext.GetHiveCount() > 0;
        var localBuildPackageVersions = packageVersions
            .Where(p => VersionHelper.IsLocalBuildChannel(p.Channel.Name))
            .ToArray();

        // When local build hives are present, prefer the package that exactly matches the
        // installed CLI/SDK version so template- and add-generated projects stay on the same
        // build. Apply this before the interactive prompt so pressing Enter does not
        // accidentally downgrade to the implicit feed when a matching hive package exists.
        if (VersionHelper.TryGetCurrentCliVersionMatch(
            localBuildPackageVersions,
            p => p.Package.Version,
            out var cliVersionPackage,
            channelName: null,
            hasPrHives: hasHives))
        {
            return cliVersionPackage;
        }

        // Prefer the implicit/default channel first to keep package selection aligned with the
        // project's configured feeds. Then select the latest version within the chosen channel.
        var orderedPackageVersions = packageVersions
            .OrderByDescending(p => p.Channel.Type is PackageChannelType.Implicit)
            .ThenByDescending(p => SemVersion.Parse(p.Package.Version), SemVersion.PrecedenceComparer);
        if (!_hostEnvironment.SupportsInteractiveInput)
        {
            return orderedPackageVersions.First();
        }

        // ... otherwise we had better prompt.
        var version = await _prompter.PromptForIntegrationVersionAsync(orderedPackageVersions, cancellationToken);

        return version;
    }

    private async Task<(string FriendlyName, NuGetPackage Package, PackageChannel Channel)> GetPackageByInteractiveFlowWithNoMatchesMessage(DirectoryInfo workingDirectory, IEnumerable<(string FriendlyName, NuGetPackage Package, PackageChannel Channel)> possiblePackages, string? searchTerm, string? preferredVersion, CancellationToken cancellationToken)
    {
        if (searchTerm is not null)
        {
            InteractionService.DisplaySubtleMessage(string.Format(CultureInfo.CurrentCulture, AddCommandStrings.NoPackagesMatchedSearchTerm, searchTerm));
        }

        return await GetPackageByInteractiveFlow(workingDirectory, possiblePackages, preferredVersion, cancellationToken);
    }

}

internal interface IAddCommandPrompter
{
    Task<(string FriendlyName, NuGetPackage Package, PackageChannel Channel)> PromptForIntegrationAsync(IEnumerable<(string FriendlyName, NuGetPackage Package, PackageChannel Channel)> packages, CancellationToken cancellationToken);
    Task<(string FriendlyName, NuGetPackage Package, PackageChannel Channel)> PromptForIntegrationVersionAsync(IEnumerable<(string FriendlyName, NuGetPackage Package, PackageChannel Channel)> packages, CancellationToken cancellationToken);
}

internal class AddCommandPrompter(IInteractionService interactionService) : IAddCommandPrompter
{
    public virtual async Task<(string FriendlyName, NuGetPackage Package, PackageChannel Channel)> PromptForIntegrationVersionAsync(IEnumerable<(string FriendlyName, NuGetPackage Package, PackageChannel Channel)> packages, CancellationToken cancellationToken)
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

        var rootChoices = new List<(string Label, Func<CancellationToken, Task<(string FriendlyName, NuGetPackage Package, PackageChannel Channel)>> Action)>();

        if (implicitGroup is not null)
        {
            var captured = implicitGroup.HighestVersion;
            rootChoices.Add((
                Label: FormatVersionLabel(captured),
                Action: ct => Task.FromResult(captured)));
        }

        foreach (var channelGroup in explicitGroups)
        {
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

