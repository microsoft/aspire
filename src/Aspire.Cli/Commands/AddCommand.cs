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
using NuGetPackage = Aspire.Shared.NuGetPackageCli;

namespace Aspire.Cli.Commands;

internal sealed class AddCommand : BaseCommand
{
    private readonly IDotNetCliRunner _runner;
    private readonly IPackagingService _packagingService;
    private readonly IInteractionService _interactionService;
    private readonly IProjectLocator _projectLocator;
    private readonly IAddCommandPrompter _prompter;
    private readonly AspireCliTelemetry _telemetry;
    private readonly IDotNetSdkInstaller _sdkInstaller;
    private readonly IDotNetRuntimeSelector _runtimeSelector;

    public AddCommand(IDotNetCliRunner runner, IPackagingService packagingService, IInteractionService interactionService, IProjectLocator projectLocator, IAddCommandPrompter prompter, AspireCliTelemetry telemetry, IDotNetSdkInstaller sdkInstaller, IDotNetRuntimeSelector runtimeSelector, IFeatures features, ICliUpdateNotifier updateNotifier)
        : base("add", AddCommandStrings.Description, features, updateNotifier)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(packagingService);
        ArgumentNullException.ThrowIfNull(interactionService);
        ArgumentNullException.ThrowIfNull(projectLocator);
        ArgumentNullException.ThrowIfNull(prompter);
        ArgumentNullException.ThrowIfNull(telemetry);
        ArgumentNullException.ThrowIfNull(sdkInstaller);
        ArgumentNullException.ThrowIfNull(runtimeSelector);

        _runner = runner;
        _packagingService = packagingService;
        _interactionService = interactionService;
        _projectLocator = projectLocator;
        _prompter = prompter;
        _telemetry = telemetry;
        _sdkInstaller = sdkInstaller;
        _runtimeSelector = runtimeSelector;

        var integrationArgument = new Argument<string>("integration");
        integrationArgument.Description = AddCommandStrings.IntegrationArgumentDescription;
        integrationArgument.Arity = ArgumentArity.ZeroOrOne;
        Arguments.Add(integrationArgument);

        var projectOption = new Option<FileInfo?>("--project");
        projectOption.Description = AddCommandStrings.ProjectArgumentDescription;
        Options.Add(projectOption);

        var versionOption = new Option<string>("--version", "-v");
        versionOption.Description = AddCommandStrings.VersionArgumentDescription;
        Options.Add(versionOption);

        var sourceOption = new Option<string?>("--source", "-s");
        sourceOption.Description = AddCommandStrings.SourceArgumentDescription;
        Options.Add(sourceOption);
    }

    protected override async Task<int> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        // Check if the .NET SDK is available
        if (!await SdkInstallHelper.EnsureSdkInstalledAsync(_sdkInstaller, _interactionService, _runtimeSelector, cancellationToken))
        {
            return ExitCodeConstants.SdkNotInstalled;
        }

        using var activity = _telemetry.ActivitySource.StartActivity(this.Name);

        var outputCollector = new OutputCollector();

        try
        {
            var integrationName = parseResult.GetValue<string>("integration");

            var passedAppHostProjectFile = parseResult.GetValue<FileInfo?>("--project");
            var effectiveAppHostProjectFile = await _projectLocator.UseOrFindAppHostProjectFileAsync(passedAppHostProjectFile, cancellationToken);

            if (effectiveAppHostProjectFile is null)
            {
                return ExitCodeConstants.FailedToFindProject;
            }

            var source = parseResult.GetValue<string?>("--source");

            var packagesWithChannels = await _interactionService.ShowStatusAsync(
                AddCommandStrings.SearchingForAspirePackages,
                async () =>
                {
                    // Get channels and find the implicit channel, similar to how templates are handled
                    var channels = await _packagingService.GetChannelsAsync(cancellationToken);

                    var packages = new List<(NuGetPackage Package, PackageChannel Channel)>();
                    var packagesLock = new object();

                    await Parallel.ForEachAsync(channels, async (channel, ct) =>
                    {
                        var integrationPackages = await channel.GetIntegrationPackagesAsync(
                            workingDirectory: effectiveAppHostProjectFile.Directory!,
                            cancellationToken: ct);
                        lock (packagesLock)
                        {
                            packages.AddRange(integrationPackages.Select(p => (p, channel)));
                        }
                    });

                    return packages;

                });

            if (!packagesWithChannels.Any())
            {
                throw new EmptyChoicesException(AddCommandStrings.NoIntegrationPackagesFound);
            }

            var version = parseResult.GetValue<string?>("--version");

            var packagesWithShortName = packagesWithChannels.Select(GenerateFriendlyName).OrderBy(p => p.FriendlyName, new CommunityToolkitFirstComparer());

            if (!packagesWithShortName.Any())
            {
                _interactionService.DisplayError(AddCommandStrings.NoPackagesFound);
                return ExitCodeConstants.FailedToAddPackage;
            }

            var filteredPackagesWithShortName = packagesWithShortName.Where(p => p.FriendlyName == integrationName || p.Package.Id == integrationName);

            if (!filteredPackagesWithShortName.Any() && integrationName is not null)
            {
                // If we didn't get an exact match on the friendly name or the package ID
                // then try a contains search to created a broader filtered list.
                filteredPackagesWithShortName = packagesWithShortName.Where(
                    p => p.FriendlyName.Contains(integrationName, StringComparison.OrdinalIgnoreCase)
                    || p.Package.Id.Contains(integrationName, StringComparison.OrdinalIgnoreCase)
                    );
            }

            // If we didn't match any, show a complete list. If we matched one, and its
            // an exact match, then we still prompt, but it will only prompt for
            // the version. If there is more than one match then we prompt.
            var selectedNuGetPackage = filteredPackagesWithShortName.Count() switch {
                0 => await GetPackageByInteractiveFlowWithNoMatchesMessage(packagesWithShortName, integrationName, cancellationToken),
                1 => filteredPackagesWithShortName.First().Package.Version == version
                    ? filteredPackagesWithShortName.First()
                    : await GetPackageByInteractiveFlow(filteredPackagesWithShortName, null, cancellationToken),
                > 1 => await GetPackageByInteractiveFlow(filteredPackagesWithShortName, version, cancellationToken),
                _ => throw new InvalidOperationException(AddCommandStrings.UnexpectedNumberOfPackagesFound)
            };

            var addPackageResult = await _interactionService.ShowStatusAsync(
                AddCommandStrings.AddingAspireIntegration,
                async () => {

                    var addPackageOptions = new DotNetCliRunnerInvocationOptions
                    {
                        StandardOutputCallback = outputCollector.AppendOutput,
                        StandardErrorCallback = outputCollector.AppendError,
                    };
                    var addPackageResult = await _runner.AddPackageAsync(
                        effectiveAppHostProjectFile,
                        selectedNuGetPackage.Package.Id,
                        selectedNuGetPackage.Package.Version,
                        source,
                        addPackageOptions,
                        cancellationToken);

                    return addPackageResult == 0 ? ExitCodeConstants.Success : ExitCodeConstants.FailedToAddPackage;
                }
            );

            if (addPackageResult != 0)
            {
                _interactionService.DisplayLines(outputCollector.GetLines());
                _interactionService.DisplayError(string.Format(CultureInfo.CurrentCulture, AddCommandStrings.PackageInstallationFailed, addPackageResult));
                return ExitCodeConstants.FailedToAddPackage;
            }
            else
            {
                _interactionService.DisplaySuccess(string.Format(CultureInfo.CurrentCulture, AddCommandStrings.PackageAddedSuccessfully, selectedNuGetPackage.Package.Id, selectedNuGetPackage.Package.Version));
                return ExitCodeConstants.Success;
            }
        }
        catch (ProjectLocatorException ex) when (string.Equals(ex.Message, ErrorStrings.ProjectFileDoesntExist, StringComparisons.CliInputOrOutput))
        {
            _interactionService.DisplayError(InteractionServiceStrings.ProjectOptionDoesntExist);
            return ExitCodeConstants.FailedToFindProject;
        }
        catch (ProjectLocatorException ex) when (string.Equals(ex.Message, ErrorStrings.MultipleProjectFilesFound, StringComparisons.CliInputOrOutput))
        {
            _interactionService.DisplayError(InteractionServiceStrings.ProjectOptionNotSpecifiedMultipleAppHostsFound);
            return ExitCodeConstants.FailedToFindProject;
        }
        catch (ProjectLocatorException ex) when (string.Equals(ex.Message, ErrorStrings.NoProjectFileFound, StringComparisons.CliInputOrOutput))
        {
            _interactionService.DisplayError(InteractionServiceStrings.ProjectOptionNotSpecifiedNoCsprojFound);
            return ExitCodeConstants.FailedToFindProject;
        }
        catch (OperationCanceledException)
        {
            _interactionService.DisplayCancellationMessage();
            return ExitCodeConstants.FailedToAddPackage;
        }
        catch (EmptyChoicesException ex)
        {
            _interactionService.DisplayError(ex.Message);
            return ExitCodeConstants.FailedToAddPackage;
        }
        catch (Exception ex)
        {
            _interactionService.DisplayLines(outputCollector.GetLines());
            _interactionService.DisplayError(string.Format(CultureInfo.CurrentCulture, AddCommandStrings.ErrorOccurredWhileAddingPackage, ex.Message));
            return ExitCodeConstants.FailedToAddPackage;
        }
    }

    private async Task<(string FriendlyName, NuGetPackage Package, PackageChannel Channel)> GetPackageByInteractiveFlow(IEnumerable<(string FriendlyName, NuGetPackage Package, PackageChannel Channel)> possiblePackages, string? preferredVersion, CancellationToken cancellationToken)
    {
        var distinctPackages = possiblePackages.DistinctBy(p => p.Package.Id);

        // If there is only one package, we can skip the prompt and just use it.
        var selectedPackage = distinctPackages.Count() switch
        {
            1 => distinctPackages.First(),
            > 1 => await _prompter.PromptForIntegrationAsync(distinctPackages, cancellationToken),
            _ => throw new InvalidOperationException(AddCommandStrings.UnexpectedNumberOfPackagesFound)
        };

        var packageVersions = possiblePackages.Where(p => p.Package.Id == selectedPackage.Package.Id);

        // If any of the package versions are an exact match for the preferred version
        // then we can skip the version prompt and just use that version.
        if (packageVersions.Any(p => p.Package.Version == preferredVersion))
        {
            var preferredVersionPackage = packageVersions.First(p => p.Package.Version == preferredVersion);
            return preferredVersionPackage;
        }

            // ... otherwise we had better prompt.
        var orderedPackageVersions = packageVersions.OrderByDescending(p => SemVersion.Parse(p.Package.Version), SemVersion.PrecedenceComparer);
        var version = await _prompter.PromptForIntegrationVersionAsync(orderedPackageVersions, cancellationToken);

        return version;
    }

    private async Task<(string FriendlyName, NuGetPackage Package, PackageChannel Channel)> GetPackageByInteractiveFlowWithNoMatchesMessage(IEnumerable<(string FriendlyName, NuGetPackage Package, PackageChannel Channel)> possiblePackages, string? searchTerm, CancellationToken cancellationToken)
    {
        if (searchTerm is not null)
        {
            _interactionService.DisplaySubtleMessage(string.Format(CultureInfo.CurrentCulture, AddCommandStrings.NoPackagesMatchedSearchTerm, searchTerm));
        }

        return await GetPackageByInteractiveFlow(possiblePackages, null, cancellationToken);
    }

    internal static (string FriendlyName, NuGetPackage Package, PackageChannel Channel) GenerateFriendlyName((NuGetPackage Package, PackageChannel Channel) packageWithChannel)
    {
        // Remove 'Aspire.Hosting' segment from anywhere in the package name
        var packageId = packageWithChannel.Package.Id.Replace("Aspire.Hosting.", "", StringComparison.OrdinalIgnoreCase);
        var friendlyName = packageId.Replace('.', '-').ToLowerInvariant();
        
        return (friendlyName, packageWithChannel.Package, packageWithChannel.Channel);
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
            var pkg = item.Package;
            var source = pkg.Source is not null && pkg.Source.Length > 0 ? pkg.Source : item.Channel.Name;
            return $"{pkg.Version} ({source})";
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

            var selection = await interactionService.PromptForSelectionAsync(
                string.Format(CultureInfo.CurrentCulture, AddCommandStrings.SelectAVersionOfPackage, firstPackage.Package.Id),
                choices,
                c => c.Label,
                ct);

            return selection.Result;
        }

        // Group the incoming package versions by channel
        var byChannel = packages
            .GroupBy(p => p.Channel)
            .ToArray();

        var implicitGroup = byChannel.FirstOrDefault(g => g.Key.Type is Packaging.PackageChannelType.Implicit);
        var explicitGroups = byChannel
            .Where(g => g.Key.Type is Packaging.PackageChannelType.Explicit)
            .ToArray();

        // Build the root menu: implicit channel packages directly, explicit channels as submenus
        var rootChoices = new List<(string Label, Func<CancellationToken, Task<(string FriendlyName, NuGetPackage Package, PackageChannel Channel)>> Action)>();

        if (implicitGroup is not null)
        {
            foreach (var item in implicitGroup)
            {
                var captured = item;
                rootChoices.Add((
                    Label: FormatVersionLabel(captured),
                    Action: ct => Task.FromResult(captured)
                ));
            }
        }

        foreach (var channelGroup in explicitGroups)
        {
            var channel = channelGroup.Key;
            var items = channelGroup.ToArray();

            rootChoices.Add((
                Label: channel.Name,
                Action: ct => PromptForChannelPackagesAsync(channel, items, ct)
            ));
        }

        // Fallback if no choices for some reason
        if (rootChoices.Count == 0)
        {
            return firstPackage;
        }

        var topSelection = await interactionService.PromptForSelectionAsync(
            string.Format(CultureInfo.CurrentCulture, AddCommandStrings.SelectAVersionOfPackage, firstPackage.Package.Id),
            rootChoices,
            c => c.Label,
            cancellationToken);

        return await topSelection.Action(cancellationToken);
    }

    public virtual async Task<(string FriendlyName, NuGetPackage Package, PackageChannel Channel)> PromptForIntegrationAsync(IEnumerable<(string FriendlyName, NuGetPackage Package, PackageChannel Channel)> packages, CancellationToken cancellationToken)
    {
        var selectedIntegration = await interactionService.PromptForSelectionAsync(
            AddCommandStrings.SelectAnIntegrationToAdd,
            packages,
            PackageNameWithFriendlyNameIfAvailable,
            cancellationToken);
        return selectedIntegration;
    }

    private static string PackageNameWithFriendlyNameIfAvailable((string FriendlyName, NuGetPackage Package, PackageChannel Channel) packageWithFriendlyName)
    {
        if (packageWithFriendlyName.FriendlyName is { } friendlyName)
        {
            return $"[bold]{friendlyName}[/] ({packageWithFriendlyName.Package.Id})";
        }
        else
        {
            return packageWithFriendlyName.Package.Id;
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