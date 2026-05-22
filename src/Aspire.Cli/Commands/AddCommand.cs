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

    public AddCommand(IInteractionService interactionService, IProjectLocator projectLocator, IntegrationPackageSearchService integrationPackageSearchService, IAddCommandPrompter prompter, AspireCliTelemetry telemetry, IDotNetSdkInstaller sdkInstaller, IFeatures features, ICliUpdateNotifier updateNotifier, CliExecutionContext executionContext, ICliHostEnvironment hostEnvironment, IAppHostProjectFactory projectFactory, ProfilingTelemetry profilingTelemetry)
        : base("add", AddCommandStrings.Description, features, updateNotifier, executionContext, interactionService, telemetry)
    {
        _projectLocator = projectLocator;
        _integrationPackageSearchService = integrationPackageSearchService;
        _prompter = prompter;
        _sdkInstaller = sdkInstaller;
        _hostEnvironment = hostEnvironment;
        _projectFactory = projectFactory;
        _profilingTelemetry = profilingTelemetry;

        Arguments.Add(s_integrationArgument);
        Options.Add(s_appHostOption);
        Options.Add(s_versionOption);
        Options.Add(s_sourceOption);
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

            List<(NuGetPackage Package, PackageChannel Channel)> packagesWithChannels;
            using (var searchPackagesActivity = _profilingTelemetry.StartAddSearchPackages(configuredChannel))
            {
                var discoveredPackages = await InteractionService.ShowStatusAsync(
                    AddCommandStrings.SearchingForAspirePackages,
                    async () => await _integrationPackageSearchService.GetIntegrationPackagesWithChannelsAsync(effectiveAppHostProjectFile.Directory!, configuredChannel, cancellationToken));
                packagesWithChannels = discoveredPackages as List<(NuGetPackage Package, PackageChannel Channel)> ?? discoveredPackages.ToList();
                var packageCount = packagesWithChannels.Count;
                searchPackagesActivity.SetAddPackageSearchResultCount(packageCount);
                addActivity.SetAddPackageSearchResultCount(packageCount);
            }

            if (packagesWithChannels.Count == 0)
            {
                throw new EmptyChoicesException(AddCommandStrings.NoIntegrationPackagesFound);
            }

            var packagesWithShortName = packagesWithChannels.Select(IntegrationPackageSearchService.GenerateFriendlyName).OrderBy(p => p.FriendlyName, new CommunityToolkitFirstComparer()).ToList();

            if (packagesWithShortName.Count == 0)
            {
                return AddCommandFailure(CliExitCodes.FailedToAddPackage, AddCommandStrings.NoPackagesFound);
            }

            var filteredPackagesWithShortName = packagesWithShortName
                .Where(p => p.FriendlyName == integrationName || p.Package.Id == integrationName)
                .ToList();
            var packageMatchKind = filteredPackagesWithShortName.Count > 0
                ? ProfilingTelemetry.Values.AddPackageMatchKindExact
                : ProfilingTelemetry.Values.AddPackageMatchKindNone;

            if (filteredPackagesWithShortName.Count == 0 && integrationName is not null && version is not null && !_hostEnvironment.SupportsInteractiveInput)
            {
                throw new EmptyChoicesException(string.Format(CultureInfo.CurrentCulture, AddCommandStrings.SpecifiedVersionRequiresExactPackageMatch, integrationName));
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

            // If we didn't match any, show a complete list. If we matched one, and its
            // an exact match, then we still prompt, but it will only prompt for
            // the version. If there is more than one match then we prompt.
            (string FriendlyName, NuGetPackage Package, PackageChannel Channel) selectedNuGetPackage;
            selectedNuGetPackage = filteredPackagesWithShortName.Count switch
            {
                0 => await GetPackageByInteractiveFlowWithNoMatchesMessage(effectiveAppHostProjectFile.Directory!, packagesWithShortName, integrationName, version, cancellationToken),
                1 when filteredPackagesWithShortName[0].Package.Version == version
                    => filteredPackagesWithShortName[0],
                _ => await GetPackageByInteractiveFlow(effectiveAppHostProjectFile.Directory!, filteredPackagesWithShortName, version, cancellationToken)
            };
            using (var selectPackageActivity = _profilingTelemetry.StartAddSelectPackage(integrationName, version))
            {
                selectPackageActivity.SetAddPackageMatch(filteredPackagesWithShortName.Count, packageMatchKind);
                selectPackageActivity.SetAddSelectedPackage(selectedNuGetPackage.Package.Id, selectedNuGetPackage.Package.Version, selectedNuGetPackage.Channel.Name);
                addActivity.SetAddSelectedPackage(selectedNuGetPackage.Package.Id, selectedNuGetPackage.Package.Version, selectedNuGetPackage.Channel.Name);
            }

            // When installing from a PR channel, ensure the project has access to
            // the PR hive as a NuGet source so `dotnet add package` can resolve the
            // PR-version package. We add the hive source to the project's nuget.config
            // WITHOUT package source mapping restrictions, so that transitive deps
            // (including RID-specific and stable-versioned packages) can still resolve
            // from NuGet.org via the normal NuGet source hierarchy.
            if (string.IsNullOrEmpty(source) && VersionHelper.IsLocalBuildChannel(selectedNuGetPackage.Channel.Name))
            {
                var mappings = selectedNuGetPackage.Channel.Mappings;
                if (mappings is { Length: > 0 })
                {
                    var hiveSources = mappings
                        .Select(m => m.Source)
                        .Where(s => !s.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                        .Distinct(StringComparer.OrdinalIgnoreCase);

                    var projectDir = effectiveAppHostProjectFile.Directory!;
                    var nugetConfigPath = Path.Combine(projectDir.FullName, "nuget.config");
                    if (!File.Exists(nugetConfigPath))
                    {
                        projectDir.Create(); // ensure directory exists
                        var configXml = new System.Xml.Linq.XDocument(
                            new System.Xml.Linq.XElement("configuration",
                                new System.Xml.Linq.XElement("packageSources",
                                    hiveSources.Select(s =>
                                        new System.Xml.Linq.XElement("add",
                                            new System.Xml.Linq.XAttribute("key", s),
                                            new System.Xml.Linq.XAttribute("value", s))))));
                        configXml.Save(nugetConfigPath);
                        InteractionService.DisplayMessage(KnownEmojis.Package, Aspire.Cli.Resources.TemplatingStrings.NuGetConfigCreatedOrUpdatedConfirmationMessage);
                    }
                }
            }

            context = new AddPackageContext
            {
                AppHostFile = effectiveAppHostProjectFile,
                PackageId = selectedNuGetPackage.Package.Id,
                PackageVersion = selectedNuGetPackage.Package.Version,
                Source = source
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

    private async Task<(string FriendlyName, NuGetPackage Package, PackageChannel Channel)> GetPackageByInteractiveFlow(DirectoryInfo workingDirectory, IEnumerable<(string FriendlyName, NuGetPackage Package, PackageChannel Channel)> possiblePackages, string? preferredVersion, CancellationToken cancellationToken)
    {
        var distinctPackages = possiblePackages.DistinctBy(p => p.Package.Id).ToArray();

        // If there is only one package, we can skip the prompt and just use it.
        // In non-interactive mode, auto-select the first package.
        var selectedPackage = distinctPackages.Length switch
        {
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
        var prChannelPackageVersions = packageVersions
            .Where(p => VersionHelper.IsLocalBuildChannel(p.Channel.Name))
            .ToArray();

        if (VersionHelper.TryGetCurrentCliVersionMatch(
            prChannelPackageVersions,
            p => p.Package.Version,
            out var cliVersionPackage,
            channelName: null,
            hasPrHives: ExecutionContext.GetHiveCount() > 0))
        {
            return cliVersionPackage;
        }

        // In non-interactive mode, prefer the implicit/default channel first to keep
        // package selection aligned with the project's configured feeds. Then select
        // the latest version within the chosen channel.
        var orderedPackageVersions = packageVersions
            .OrderByDescending(p => p.Channel.Type is PackageChannelType.Implicit)
            .ThenByDescending(p => SemVersion.Parse(p.Package.Version), SemVersion.PrecedenceComparer);
        if (!_hostEnvironment.SupportsInteractiveInput)
        {
            return orderedPackageVersions.First();
        }

        // ... otherwise we had better prompt.
        var version = await PromptForIntegrationVersionAsync(orderedPackageVersions, cancellationToken);

        return version;
    }

    private async Task<(string FriendlyName, NuGetPackage Package, PackageChannel Channel)> PromptForIntegrationAsync(IEnumerable<(string FriendlyName, NuGetPackage Package, PackageChannel Channel)> packages, CancellationToken cancellationToken)
    {
        using var promptActivity = _profilingTelemetry.StartAddSelectPackagePrompt();
        return await _prompter.PromptForIntegrationAsync(packages, cancellationToken);
    }

    private async Task<(string FriendlyName, NuGetPackage Package, PackageChannel Channel)> PromptForIntegrationVersionAsync(IEnumerable<(string FriendlyName, NuGetPackage Package, PackageChannel Channel)> packages, CancellationToken cancellationToken)
    {
        using var promptActivity = _profilingTelemetry.StartAddSelectPackagePrompt();
        return await _prompter.PromptForIntegrationVersionAsync(packages, cancellationToken);
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
                    Result: i
                ))
                .ToArray();

            // Auto-select when there's only one version in the channel
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

        // Group the incoming package versions by channel and filter to highest version per channel
        var byChannel = packages
            .GroupBy(p => p.Channel)
            .Select(g => new
            {
                Channel = g.Key,
                // Keep only the highest version in each channel
                HighestVersion = g.OrderByDescending(p => SemVersion.Parse(p.Package.Version), SemVersion.PrecedenceComparer).First()
            })
            .ToArray();

        var implicitGroup = byChannel.FirstOrDefault(g => g.Channel.Type is Packaging.PackageChannelType.Implicit);
        var explicitGroups = byChannel
            .Where(g => g.Channel.Type is Packaging.PackageChannelType.Explicit)
            .ToArray();

        // If there are no explicit channels, automatically select from the implicit channel
        if (explicitGroups.Length == 0 && implicitGroup is not null)
        {
            return implicitGroup.HighestVersion;
        }

        // Build the root menu: implicit channel packages directly, explicit channels as submenus
        var rootChoices = new List<(string Label, Func<CancellationToken, Task<(string FriendlyName, NuGetPackage Package, PackageChannel Channel)>> Action)>();

        if (implicitGroup is not null)
        {
            var captured = implicitGroup.HighestVersion;
            rootChoices.Add((
                Label: FormatVersionLabel(captured),
                Action: ct => Task.FromResult(captured)
            ));
        }

        foreach (var channelGroup in explicitGroups)
        {
            var channel = channelGroup.Channel;
            var item = channelGroup.HighestVersion;

            rootChoices.Add((
                Label: channel.Name.EscapeMarkup(),
                // For explicit channels, we still show submenu but with only the highest version
                Action: ct => PromptForChannelPackagesAsync(channel, new[] { item }, ct)
            ));
        }

        // Fallback if no choices for some reason
        if (rootChoices.Count == 0)
        {
            return firstPackage;
        }

        // Auto-select when there's only one option (e.g., single explicit channel)
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
            .ToArray();

        var selectedIntegration = await interactionService.PromptForSelectionAsync(
            AddCommandStrings.SelectAnIntegrationToAdd,
            filteredPackages,
            PackageNameWithFriendlyNameIfAvailable,
            cancellationToken: cancellationToken);
        return selectedIntegration;
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

internal sealed class CommunityToolkitFirstComparer : IComparer<string>
{
    public int Compare(string? x, string? y)
    {
        ArgumentNullException.ThrowIfNull(x);
        ArgumentNullException.ThrowIfNull(y);

        var prefix = "communitytoolkit-";
        var xStarts = x.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        var yStarts = y.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

        return (xStarts, yStarts) switch
        {
            (true, false) => 1,
            (false, true) => -1,
            _ => string.Compare(x, y, StringComparison.OrdinalIgnoreCase)
        };
    }
}
