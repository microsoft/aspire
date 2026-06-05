// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.Globalization;
using Aspire.Cli.Certificates;
using Aspire.Cli.Configuration;
using Aspire.Cli.DotNet;
using Aspire.Cli.Exceptions;
using Aspire.Cli.Interaction;
using Aspire.Cli.NuGet;
using Aspire.Cli.Packaging;
using Aspire.Cli.Projects;
using Aspire.Cli.Resources;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Templating;
using Aspire.Cli.Utils;
using NuGetPackage = Aspire.Shared.NuGetPackageCli;
using Semver;
using Spectre.Console;

namespace Aspire.Cli.Commands;

internal sealed class InitCommand : BaseCommand, IPackageMetaPrefetchingCommand
{
    private readonly IDotNetCliRunner _runner;
    private readonly ICertificateService _certificateService;
    private readonly INewCommandPrompter _prompter;
    private readonly ITemplateFactory _templateFactory;
    private readonly IPackagingService _packagingService;
    private readonly ISolutionLocator _solutionLocator;
    private readonly AspireCliTelemetry _telemetry;
    private readonly IDotNetSdkInstaller _sdkInstaller;
    private readonly ICliHostEnvironment _hostEnvironment;
    private readonly IFeatures _features;
    private readonly ICliUpdateNotifier _updateNotifier;
    private readonly CliExecutionContext _executionContext;
    private readonly IConfigurationService _configurationService;

    /// <summary>
    /// InitCommand prefetches template package metadata.
    /// </summary>
    public bool PrefetchesTemplatePackageMetadata => true;

    /// <summary>
    /// InitCommand prefetches CLI package metadata for update notifications.
    /// </summary>
    public bool PrefetchesCliPackageMetadata => true;

    public InitCommand(
        IDotNetCliRunner runner,
        ICertificateService certificateService,
        INewCommandPrompter prompter,
        ITemplateFactory templateFactory,
        IPackagingService packagingService,
        ISolutionLocator solutionLocator,
        AspireCliTelemetry telemetry,
        IDotNetSdkInstaller sdkInstaller,
        IFeatures features,
        ICliUpdateNotifier updateNotifier,
        CliExecutionContext executionContext, ICliHostEnvironment hostEnvironment,
        IInteractionService interactionService,
        IConfigurationService configurationService)
        : base("init", InitCommandStrings.Description, features, updateNotifier, executionContext, interactionService)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(certificateService);
        ArgumentNullException.ThrowIfNull(prompter);
        ArgumentNullException.ThrowIfNull(templateFactory);
        ArgumentNullException.ThrowIfNull(packagingService);
        ArgumentNullException.ThrowIfNull(solutionLocator);
        ArgumentNullException.ThrowIfNull(telemetry);
        ArgumentNullException.ThrowIfNull(sdkInstaller);
        ArgumentNullException.ThrowIfNull(hostEnvironment);
        ArgumentNullException.ThrowIfNull(configurationService);

        _runner = runner;
        _certificateService = certificateService;
        _prompter = prompter;
        _templateFactory = templateFactory;
        _packagingService = packagingService;
        _solutionLocator = solutionLocator;
        _telemetry = telemetry;
        _sdkInstaller = sdkInstaller;
        _hostEnvironment = hostEnvironment;
        _features = features;
        _updateNotifier = updateNotifier;
        _executionContext = executionContext;
        _configurationService = configurationService;

        var sourceOption = new Option<string?>("--source", "-s");
        sourceOption.Description = NewCommandStrings.SourceArgumentDescription;
        sourceOption.Recursive = true;
        Options.Add(sourceOption);

        var templateVersionOption = new Option<string?>("--version", "-v");
        templateVersionOption.Description = NewCommandStrings.VersionArgumentDescription;
        templateVersionOption.Recursive = true;
        Options.Add(templateVersionOption);

        // Customize description based on whether staging channel is enabled
        var isStagingEnabled = features.IsFeatureEnabled(KnownFeatures.StagingChannelEnabled, false);

        var channelOption = new Option<string?>("--channel")
        {
            Description = isStagingEnabled
                ? NewCommandStrings.ChannelOptionDescriptionWithStaging
                : NewCommandStrings.ChannelOptionDescription,
            Recursive = true
        };
        Options.Add(channelOption);
    }

    protected override async Task<int> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        // Check if the .NET SDK is available
        if (!await SdkInstallHelper.EnsureSdkInstalledAsync(_sdkInstaller, InteractionService, _features, _hostEnvironment, cancellationToken))
        {
            return ExitCodeConstants.SdkNotInstalled;
        }

        using var activity = _telemetry.ActivitySource.StartActivity(this.Name);

        // Create the init context to build up a model of the operation
        var initContext = new InitContext();

        // Use SolutionLocator to find solution files, walking up the directory tree
        initContext.SelectedSolutionFile = await _solutionLocator.FindSolutionFileAsync(_executionContext.WorkingDirectory, cancellationToken);

        if (initContext.SelectedSolutionFile is not null)
        {
            InteractionService.DisplayEmptyLine();
            InteractionService.DisplayMessage("information", string.Format(CultureInfo.CurrentCulture, InitCommandStrings.SolutionDetected, initContext.SelectedSolutionFile.Name));
            InteractionService.DisplayEmptyLine();
            return await InitializeExistingSolutionAsync(initContext, parseResult, cancellationToken);
        }
        else
        {
            InteractionService.DisplayEmptyLine();
            InteractionService.DisplayMessage("information", InitCommandStrings.NoSolutionFoundCreatingSingleFileAppHost);
            InteractionService.DisplayEmptyLine();
            return await CreateEmptyAppHostAsync(parseResult, cancellationToken);
        }
    }

    private async Task<int> InitializeExistingSolutionAsync(InitContext initContext, ParseResult parseResult, CancellationToken cancellationToken)
    {
        var solutionFile = initContext.SelectedSolutionFile!;

        initContext.GetSolutionProjectsOutputCollector = new OutputCollector();
        var (getSolutionExitCode, solutionProjects) = await InteractionService.ShowStatusAsync("Reading solution...", async () =>
        {
            var options = new DotNetCliRunnerInvocationOptions
            {
                StandardOutputCallback = initContext.GetSolutionProjectsOutputCollector.AppendOutput,
                StandardErrorCallback = initContext.GetSolutionProjectsOutputCollector.AppendError
            };

            return await _runner.GetSolutionProjectsAsync(
                solutionFile,
                options,
                cancellationToken);
        });

        if (getSolutionExitCode != 0)
        {
            InteractionService.DisplayLines(initContext.GetSolutionProjectsOutputCollector.GetLines());
            InteractionService.DisplayError("Failed to get projects from solution.");
            return getSolutionExitCode;
        }

        initContext.SolutionProjects = solutionProjects;

        _ = await InteractionService.ShowStatusAsync("Evaluating existing projects...", async () =>
        {
            await EvaluateSolutionProjectsAsync(initContext, cancellationToken);

            // HACK: Need to fix up InteractionService to support Task return from status operations.
            return 0;
        });

        if (initContext.AlreadyHasAppHost)
        {
            InteractionService.DisplayMessage("check_mark", InitCommandStrings.SolutionAlreadyInitialized);
            return ExitCodeConstants.Success;
        }

        // If there are executable projects, prompt user to select which ones to add to appHost
        if (initContext.ExecutableProjects.Count > 0)
        {
            var addExecutableProjectsMessage = """
                                               # Add existing projects to AppHost?

                                               The following projects were found in the solution that can be
                                               hosted in Aspire. Select the ones that you would like to be
                                               added to the AppHost project. You can add or remove them
                                               later as needed.
                                               """;

            InteractionService.DisplayEmptyLine();
            InteractionService.DisplayMarkdown(addExecutableProjectsMessage);
            InteractionService.DisplayEmptyLine();

            var selectedProjects = await InteractionService.PromptForSelectionsAsync(
                "Select projects to add to the AppHost:",
                initContext.ExecutableProjects,
                project => Path.GetFileNameWithoutExtension(project.ProjectFile.Name),
                cancellationToken);

            initContext.ExecutableProjectsToAddToAppHost = selectedProjects;

            // If projects were selected, prompt for which should have ServiceDefaults added
            if (initContext.ExecutableProjectsToAddToAppHost.Count > 0)
            {
                InteractionService.DisplayEmptyLine();
                InteractionService.DisplayMessage("information", "The following projects will be added to the AppHost:");
                InteractionService.DisplayEmptyLine();

                foreach (var project in initContext.ExecutableProjectsToAddToAppHost)
                {
                    InteractionService.DisplayMessage("check_box_with_check", project.ProjectFile.Name);
                }

                var addServiceDefaultsMessage = """
                                # Add ServiceDefaults reference to selected projects?

                                Do you want to add a reference to the ServiceDefaults project to
                                the executable projects that will be added to the AppHost? The 
                                ServiceDefaults project contains helper code to make it easier
                                for you to configure telemetry and service discovery in Aspire.
                                """;

                InteractionService.DisplayEmptyLine();
                InteractionService.DisplayMarkdown(addServiceDefaultsMessage);
                InteractionService.DisplayEmptyLine();

                var serviceDefaultsActions = new Dictionary<string, string>
                {
                    { "all", "Add to all previously added projects" },
                    { "choose", "Let me choose" },
                    { "none", "Do not add to any projects" }
                };

                var selection = await InteractionService.PromptForSelectionAsync(
                    "Add ServiceDefaults reference?",
                    serviceDefaultsActions,
                    (action) => action.Value,
                    cancellationToken
                );

                switch (selection.Key)
                {
                    case "all":
                        initContext.ProjectsToAddServiceDefaultsTo = initContext.ExecutableProjectsToAddToAppHost;
                        break;
                    case "choose":
                        initContext.ProjectsToAddServiceDefaultsTo = await InteractionService.PromptForSelectionsAsync(
                            "Select projects to add ServiceDefaults reference to:",
                            initContext.ExecutableProjectsToAddToAppHost,
                            project => Path.GetFileNameWithoutExtension(project.ProjectFile.Name),
                            cancellationToken);
                        break;
                    case "none":
                        initContext.ProjectsToAddServiceDefaultsTo = Array.Empty<ExecutableProjectInfo>();
                        break;
                }
            }
        }

        // Get template version/channel selection using the same logic as NewCommand
        var selectedTemplateDetails = await GetProjectTemplatesVersionAsync(parseResult, cancellationToken);

        // Create or update NuGet.config for explicit channels in the solution directory
        // This matches the behavior of 'aspire new' when creating in-place
        var nugetConfigPrompter = new NuGetConfigPrompter(InteractionService);
        await nugetConfigPrompter.PromptToCreateOrUpdateAsync(
            ExecutionContext.WorkingDirectory,
            selectedTemplateDetails.Channel,
            cancellationToken);

        // Create a temporary directory for the template output
        var tempProjectDir = Path.Combine(Path.GetTempPath(), $"aspire-init-{Guid.NewGuid()}");
        Directory.CreateDirectory(tempProjectDir);

        try
        {
            // Create temporary NuGet config if using explicit channel
            using var temporaryConfig = selectedTemplateDetails.Channel.Type == PackageChannelType.Explicit ? await TemporaryNuGetConfig.CreateAsync(selectedTemplateDetails.Channel.Mappings!) : null;

            // Install templates first if needed
            initContext.InstallTemplateOutputCollector = new OutputCollector();
            var templateInstallResult = await InteractionService.ShowStatusAsync(
                "Getting templates...",
                async () =>
                {
                    var options = new DotNetCliRunnerInvocationOptions
                    {
                        StandardOutputCallback = initContext.InstallTemplateOutputCollector.AppendOutput,
                        StandardErrorCallback = initContext.InstallTemplateOutputCollector.AppendError
                    };

                    return await _runner.InstallTemplateAsync(
                        packageName: "Aspire.ProjectTemplates",
                        version: selectedTemplateDetails.Package.Version,
                        nugetConfigFile: temporaryConfig?.ConfigFile,
                        nugetSource: selectedTemplateDetails.Package.Source,
                        force: true,
                        options: options,
                        cancellationToken: cancellationToken);
                });

            if (templateInstallResult.ExitCode != 0)
            {
                InteractionService.DisplayLines(initContext.InstallTemplateOutputCollector.GetLines());
                InteractionService.DisplayError("Failed to install Aspire templates.");
                return ExitCodeConstants.FailedToInstallTemplates;
            }

            initContext.NewProjectOutputCollector = new OutputCollector();
            var createResult = await InteractionService.ShowStatusAsync(
                "Creating Aspire projects from template...",
                async () =>
                {
                    var options = new DotNetCliRunnerInvocationOptions
                    {
                        StandardOutputCallback = initContext.NewProjectOutputCollector.AppendOutput,
                        StandardErrorCallback = initContext.NewProjectOutputCollector.AppendError
                    };

                    return await _runner.NewProjectAsync(
                        "aspire",
                        initContext.SolutionName,
                        tempProjectDir,
                        ["--framework", initContext.RequiredAppHostFramework],
                        options,
                        cancellationToken);
                });

            if (createResult != 0)
            {
                InteractionService.DisplayLines(initContext.NewProjectOutputCollector.GetLines());
                InteractionService.DisplayError($"Failed to create Aspire projects. Exit code: {createResult}");
                return createResult;
            }

            // Find the created projects in the temporary directory
            var tempDir = new DirectoryInfo(tempProjectDir);
            var appHostProjects = tempDir.GetDirectories("*.AppHost", SearchOption.TopDirectoryOnly);
            var serviceDefaultsProjects = tempDir.GetDirectories("*.ServiceDefaults", SearchOption.TopDirectoryOnly);

            if (appHostProjects.Length == 0 || serviceDefaultsProjects.Length == 0)
            {
                InteractionService.DisplayError("Failed to find created AppHost or ServiceDefaults projects in template output.");
                return ExitCodeConstants.FailedToCreateNewProject;
            }

            var appHostProjectDir = appHostProjects[0];
            var serviceDefaultsProjectDir = serviceDefaultsProjects[0];

            // Copy the projects to the solution directory
            // Using copy instead of move to support cross-drive operations on Windows
            var finalAppHostDir = Path.Combine(initContext.SolutionDirectory.FullName, appHostProjectDir.Name);
            var finalServiceDefaultsDir = Path.Combine(initContext.SolutionDirectory.FullName, serviceDefaultsProjectDir.Name);

            FileSystemHelper.CopyDirectory(appHostProjectDir.FullName, finalAppHostDir);
            FileSystemHelper.CopyDirectory(serviceDefaultsProjectDir.FullName, finalServiceDefaultsDir);

            // Delete the temporary directory
            Directory.Delete(tempProjectDir, recursive: true);

            // Add AppHost project to solution
            var appHostProjectFile = new FileInfo(Path.Combine(finalAppHostDir, $"{appHostProjectDir.Name}.csproj"));
            var serviceDefaultsProjectFile = new FileInfo(Path.Combine(finalServiceDefaultsDir, $"{serviceDefaultsProjectDir.Name}.csproj"));
            initContext.AddAppHostToSolutionOutputCollector = new OutputCollector();
            var addAppHostResult = await InteractionService.ShowStatusAsync(
                InitCommandStrings.AddingAppHostProjectToSolution,
                async () =>
                {
                    var options = new DotNetCliRunnerInvocationOptions
                    {
                        StandardOutputCallback = initContext.AddAppHostToSolutionOutputCollector.AppendOutput,
                        StandardErrorCallback = initContext.AddAppHostToSolutionOutputCollector.AppendError
                    };

                    return await _runner.AddProjectToSolutionAsync(
                        solutionFile,
                        appHostProjectFile,
                        options,
                        cancellationToken);
                });

            if (addAppHostResult != 0)
            {
                InteractionService.DisplayLines(initContext.AddAppHostToSolutionOutputCollector.GetLines());
                InteractionService.DisplayError($"Failed to add AppHost project to solution. Exit code: {addAppHostResult}");
                return addAppHostResult;
            }

            // Add ServiceDefaults project to solution
            initContext.AddServiceDefaultsToSolutionOutputCollector = new OutputCollector();
            var addServiceDefaultsResult = await InteractionService.ShowStatusAsync(
                InitCommandStrings.AddingServiceDefaultsProjectToSolution,
                async () =>
                {
                    var options = new DotNetCliRunnerInvocationOptions
                    {
                        StandardOutputCallback = initContext.AddServiceDefaultsToSolutionOutputCollector.AppendOutput,
                        StandardErrorCallback = initContext.AddServiceDefaultsToSolutionOutputCollector.AppendError
                    };

                    return await _runner.AddProjectToSolutionAsync(
                        solutionFile,
                        serviceDefaultsProjectFile,
                        options,
                        cancellationToken);
                });

            if (addServiceDefaultsResult != 0)
            {
                InteractionService.DisplayLines(initContext.AddServiceDefaultsToSolutionOutputCollector.GetLines());
                InteractionService.DisplayError($"Failed to add ServiceDefaults project to solution. Exit code: {addServiceDefaultsResult}");
                return addServiceDefaultsResult;
            }

            // Add selected projects to appHost
            if (initContext.ExecutableProjectsToAddToAppHost.Count > 0)
            {
                initContext.AddProjectReferenceOutputCollectors = new List<OutputCollector>();
                foreach(var project in initContext.ExecutableProjectsToAddToAppHost)
                {
                    var outputCollector = new OutputCollector();
                    initContext.AddProjectReferenceOutputCollectors.Add(outputCollector);

                    var addRefResult = await InteractionService.ShowStatusAsync(
                        $"Adding {project.ProjectFile.Name} to AppHost...", async () =>
                        {
                            var options = new DotNetCliRunnerInvocationOptions
                            {
                                StandardOutputCallback = outputCollector.AppendOutput,
                                StandardErrorCallback = outputCollector.AppendError
                            };

                            return await _runner.AddProjectReferenceAsync(
                                appHostProjectFile,
                                project.ProjectFile,
                                options,
                                cancellationToken);
                        });

                    if (addRefResult != 0)
                    {
                        InteractionService.DisplayLines(outputCollector.GetLines());
                        InteractionService.DisplayError($"Failed to add reference to {Path.GetFileNameWithoutExtension(project.ProjectFile.Name)}.");
                        return addRefResult;
                    }
                }
            }

            // Add ServiceDefaults references to selected projects
            if (initContext.ProjectsToAddServiceDefaultsTo.Count > 0)
            {
                initContext.AddServiceDefaultsReferenceOutputCollectors = new List<OutputCollector>();
                foreach (var project in initContext.ProjectsToAddServiceDefaultsTo)
                {
                    var outputCollector = new OutputCollector();
                    initContext.AddServiceDefaultsReferenceOutputCollectors.Add(outputCollector);

                    var addRefResult = await InteractionService.ShowStatusAsync(
                        $"Adding ServiceDefaults reference to {project.ProjectFile.Name}...", async () =>
                        {
                            var options = new DotNetCliRunnerInvocationOptions
                            {
                                StandardOutputCallback = outputCollector.AppendOutput,
                                StandardErrorCallback = outputCollector.AppendError
                            };

                            return await _runner.AddProjectReferenceAsync(
                                project.ProjectFile,
                                serviceDefaultsProjectFile,
                                options,
                                cancellationToken);
                        });

                    if (addRefResult != 0)
                    {
                        InteractionService.DisplayLines(outputCollector.GetLines());
                        InteractionService.DisplayError($"Failed to add ServiceDefaults reference to {Path.GetFileNameWithoutExtension(project.ProjectFile.Name)}.");
                        return addRefResult;
                    }
                }
            }

            await _certificateService.EnsureCertificatesTrustedAsync(_runner, cancellationToken);

            InteractionService.DisplaySuccess(InitCommandStrings.AspireInitializationComplete);
            return ExitCodeConstants.Success;
        }
        finally
        {
            // Clean up temporary directory
            if (Directory.Exists(tempProjectDir))
            {
                Directory.Delete(tempProjectDir, recursive: true);
            }
        }
    }

    private async Task<int> CreateEmptyAppHostAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        // Use single-file AppHost template
        var singleFileTemplate = _templateFactory.GetInitTemplates().FirstOrDefault(t => t.Name == "aspire-apphost-singlefile");
        if (singleFileTemplate is null)
        {
            InteractionService.DisplayError("Single-file AppHost template not found.");
            return ExitCodeConstants.FailedToCreateNewProject;
        }
        var template = singleFileTemplate;

        var result = await template.ApplyTemplateAsync(parseResult, cancellationToken);

        if (result.ExitCode == 0)
        {
            await _certificateService.EnsureCertificatesTrustedAsync(_runner, cancellationToken);
            InteractionService.DisplaySuccess(InitCommandStrings.AspireInitializationComplete);
        }

        return result.ExitCode;
    }

    private async Task EvaluateSolutionProjectsAsync(InitContext initContext, CancellationToken cancellationToken)
    {
        var executableProjects = new List<ExecutableProjectInfo>();

        initContext.EvaluateSolutionProjectsOutputCollector = new OutputCollector();

        foreach (var project in initContext.SolutionProjects)
        {
            var options = new DotNetCliRunnerInvocationOptions
            {
                StandardOutputCallback = initContext.EvaluateSolutionProjectsOutputCollector.AppendOutput,
                StandardErrorCallback = initContext.EvaluateSolutionProjectsOutputCollector.AppendError
            };

            // Get IsAspireHost, OutputType, and TargetFramework properties in a single call
            var (exitCode, jsonDoc) = await _runner.GetProjectItemsAndPropertiesAsync(
                project,
                [],
                ["IsAspireHost", "OutputType", "TargetFramework"],
                options,
                cancellationToken);

            if (exitCode == 0 && jsonDoc != null)
            {
                var rootElement = jsonDoc.RootElement;
                if (rootElement.TryGetProperty("Properties", out var properties))
                {
                    // Check if this project is an AppHost
                    if (properties.TryGetProperty("IsAspireHost", out var isAspireHostElement))
                    {
                        var isAspireHost = isAspireHostElement.GetString();
                        if (isAspireHost?.Equals("true", StringComparison.OrdinalIgnoreCase) == true)
                        {
                            initContext.AlreadyHasAppHost = true;
                            return;
                        }
                    }

                    // Check if this project is executable
                    if (properties.TryGetProperty("OutputType", out var outputTypeElement))
                    {
                        var outputType = outputTypeElement.GetString();
                        if (outputType == "Exe" || outputType == "WinExe")
                        {
                            // Get the target framework
                            var targetFramework = "net9.0"; // Default if not found
                            if (properties.TryGetProperty("TargetFramework", out var targetFrameworkElement))
                            {
                                targetFramework = targetFrameworkElement.GetString() ?? "net9.0";
                            }

                            // Only add projects with supported TFMs
                            if (IsSupportedTfm(targetFramework))
                            {
                                executableProjects.Add(new ExecutableProjectInfo
                                {
                                    ProjectFile = project,
                                    TargetFramework = targetFramework
                                });
                            }
                        }
                    }
                }
            }
        }

        initContext.ExecutableProjects = executableProjects;
    }

    /// <summary>
    /// Determines if the specified target framework moniker is supported.
    /// </summary>
    /// <param name="tfm">The target framework moniker to check.</param>
    /// <returns>True if the TFM is supported; otherwise, false.</returns>
    private static bool IsSupportedTfm(string tfm)
    {
        return tfm switch
        {
            "net8.0" => true,
            "net9.0" => true,
            "net10.0" => true,
            _ => false
        };
    }

    private async Task<(NuGetPackage Package, PackageChannel Channel)> GetProjectTemplatesVersionAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        var allChannels = await _packagingService.GetChannelsAsync(cancellationToken);

        // Check if --channel option was provided (highest priority)
        var channelName = parseResult.GetValue<string?>("--channel");
        
        // If no --channel option, check for global channel setting
        if (string.IsNullOrEmpty(channelName))
        {
            channelName = await _configurationService.GetConfigurationAsync("channel", cancellationToken);
        }
        
        IEnumerable<PackageChannel> channels;
        bool hasChannelSetting = !string.IsNullOrEmpty(channelName);
        
        if (hasChannelSetting)
        {
            // If --channel option is provided or global channel setting exists, find the matching channel
            // (--channel option takes precedence over global setting)
            var matchingChannel = allChannels.FirstOrDefault(c => string.Equals(c.Name, channelName, StringComparison.OrdinalIgnoreCase));
            if (matchingChannel is null)
            {
                throw new ChannelNotFoundException($"No channel found matching '{channelName}'. Valid options are: {string.Join(", ", allChannels.Select(c => c.Name))}");
            }
            channels = new[] { matchingChannel };
        }
        else
        {
            // No channel specified, use all channels for prompting
            channels = allChannels;
        }

        var packagesFromChannels = await InteractionService.ShowStatusAsync("Searching for available template versions...", async () =>
        {
            var results = new List<(NuGetPackage Package, PackageChannel Channel)>();
            var packagesFromChannelsLock = new object();

            await Parallel.ForEachAsync(channels, cancellationToken, async (channel, ct) =>
            {
                var templatePackages = await channel.GetTemplatePackagesAsync(_executionContext.WorkingDirectory, ct);
                lock (packagesFromChannelsLock)
                {
                    results.AddRange(templatePackages.Select(p => (p, channel)));
                }
            });

            return results;
        });

        if (!packagesFromChannels.Any())
        {
            throw new InvalidOperationException("No template versions found");
        }

        var orderedPackagesFromChannels = packagesFromChannels.OrderByDescending(p => SemVersion.Parse(p.Package.Version), SemVersion.PrecedenceComparer);

        // Check for explicit version specified via command line
        if (parseResult.GetValue<string>("--version") is { } version)
        {
            var explicitPackageFromChannel = orderedPackagesFromChannels.FirstOrDefault(p => p.Package.Version == version);
            if (explicitPackageFromChannel.Package is not null)
            {
                return explicitPackageFromChannel;
            }
        }

        // If channel was specified via --channel option or global setting (but no --version), 
        // automatically select the highest version from that channel without prompting
        if (hasChannelSetting)
        {
            return orderedPackagesFromChannels.First();
        }

        var latestStable = orderedPackagesFromChannels.FirstOrDefault(p => !SemVersion.Parse(p.Package.Version).IsPrerelease);

        var templateSelectionMessage = $$"""
                                       # Which version of Aspire do you want to use?

                                       Multiple versions of Aspire are available. If you want to use
                                       the latest stable version choose ***{{latestStable.Package.Version}}***.
                                       """;

        InteractionService.DisplayEmptyLine();
        InteractionService.DisplayMarkdown(templateSelectionMessage);
        InteractionService.DisplayEmptyLine();

        // Prompt user to select from available versions/channels
        var selectedPackageFromChannel = await _prompter.PromptForTemplatesVersionAsync(orderedPackagesFromChannels, cancellationToken);
        return selectedPackageFromChannel;
    }
}

/// <summary>
/// Represents information about an executable project including its file and target framework.
/// </summary>
internal sealed class ExecutableProjectInfo
{
    /// <summary>
    /// Gets the project file.
    /// </summary>
    public required FileInfo ProjectFile { get; init; }

    /// <summary>
    /// Gets the target framework moniker (e.g., "net9.0", "net10.0").
    /// </summary>
    public required string TargetFramework { get; init; }
}

/// <summary>
/// Context class for building up a model of the init operation before executing changes.
/// </summary>
internal sealed class InitContext
{
    /// <summary>
    /// The solution file selected for initialization, or null if no solution was found.
    /// </summary>
    public FileInfo? SelectedSolutionFile { get; set; }

    /// <summary>
    /// Gets the solution name (without extension) derived from the selected solution file.
    /// </summary>
    public string SolutionName => Path.GetFileNameWithoutExtension(SelectedSolutionFile!.Name);

    /// <summary>
    /// Gets the directory containing the solution file.
    /// </summary>
    public DirectoryInfo SolutionDirectory => SelectedSolutionFile!.Directory!;

    /// <summary>
    /// Gets the expected directory path for the AppHost project.
    /// </summary>
    public string ExpectedAppHostDirectory => Path.Combine(SolutionDirectory.FullName, $"{SolutionName}.AppHost");

    /// <summary>
    /// Gets the expected directory path for the ServiceDefaults project.
    /// </summary>
    public string ExpectedServiceDefaultsDirectory => Path.Combine(SolutionDirectory.FullName, $"{SolutionName}.ServiceDefaults");

    /// <summary>
    /// All projects in the solution.
    /// </summary>
    public IReadOnlyList<FileInfo> SolutionProjects { get; set; } = Array.Empty<FileInfo>();

    /// <summary>
    /// Indicates whether the solution already has an AppHost project.
    /// </summary>
    public bool AlreadyHasAppHost { get; set; }

    /// <summary>
    /// List of executable projects found in the solution (excluding the AppHost).
    /// </summary>
    public IReadOnlyList<ExecutableProjectInfo> ExecutableProjects { get; set; } = Array.Empty<ExecutableProjectInfo>();

    /// <summary>
    /// Executable projects selected by the user to add to the AppHost.
    /// </summary>
    public IReadOnlyList<ExecutableProjectInfo> ExecutableProjectsToAddToAppHost { get; set; } = Array.Empty<ExecutableProjectInfo>();

    /// <summary>
    /// Projects selected by the user to add ServiceDefaults reference to.
    /// </summary>
    public IReadOnlyList<ExecutableProjectInfo> ProjectsToAddServiceDefaultsTo { get; set; } = Array.Empty<ExecutableProjectInfo>();

    /// <summary>
    /// Gets the required AppHost framework based on the highest TFM of all selected executable projects.
    /// </summary>
    public string RequiredAppHostFramework
    {
        get
        {
            if (ExecutableProjectsToAddToAppHost.Count == 0)
            {
                return "net9.0"; // Default framework if no projects selected
            }

            // Parse and compare TFMs to find the highest one using SemVersion
            SemVersion? highestVersion = null;
            var highestTfm = "net9.0";

            foreach (var project in ExecutableProjectsToAddToAppHost)
            {
                var tfm = project.TargetFramework;
                if (tfm.StartsWith("net", StringComparison.OrdinalIgnoreCase))
                {
                    var versionString = tfm[3..];
                    // Add patch version if not present for SemVersion parsing
                    // TFMs are in format "8.0", "9.0", "10.0", need to make them "8.0.0", "9.0.0", "10.0.0"
                    var dotCount = versionString.Count(c => c == '.');
                    if (dotCount == 1)
                    {
                        versionString += ".0";
                    }

                    if (SemVersion.TryParse(versionString, SemVersionStyles.Strict, out var version))
                    {
                        if (highestVersion is null || SemVersion.ComparePrecedence(version, highestVersion) > 0)
                        {
                            highestVersion = version;
                            highestTfm = tfm;
                        }
                    }
                }
            }

            return highestTfm;
        }
    }

    /// <summary>
    /// OutputCollector for GetSolutionProjects operation.
    /// </summary>
    public OutputCollector? GetSolutionProjectsOutputCollector { get; set; }

    /// <summary>
    /// OutputCollector for EvaluateSolutionProjects operation.
    /// </summary>
    public OutputCollector? EvaluateSolutionProjectsOutputCollector { get; set; }

    /// <summary>
    /// OutputCollector for InstallTemplate operation.
    /// </summary>
    public OutputCollector? InstallTemplateOutputCollector { get; set; }

    /// <summary>
    /// OutputCollector for NewProject operation.
    /// </summary>
    public OutputCollector? NewProjectOutputCollector { get; set; }

    /// <summary>
    /// OutputCollector for AddAppHostToSolution operation.
    /// </summary>
    public OutputCollector? AddAppHostToSolutionOutputCollector { get; set; }

    /// <summary>
    /// OutputCollector for AddServiceDefaultsToSolution operation.
    /// </summary>
    public OutputCollector? AddServiceDefaultsToSolutionOutputCollector { get; set; }

    /// <summary>
    /// OutputCollectors for AddProjectReference operations (one per project reference added).
    /// </summary>
    public List<OutputCollector>? AddProjectReferenceOutputCollectors { get; set; }

    /// <summary>
    /// OutputCollectors for AddServiceDefaultsReference operations (one per ServiceDefaults reference added).
    /// </summary>
    public List<OutputCollector>? AddServiceDefaultsReferenceOutputCollectors { get; set; }
}
