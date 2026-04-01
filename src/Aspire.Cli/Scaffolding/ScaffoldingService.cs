// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Configuration;
using Aspire.Cli.Interaction;
using Aspire.Cli.Projects;
using Aspire.Cli.Utils;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Scaffolding;

/// <summary>
/// Service for scaffolding new AppHost projects.
/// Handles both C# (via templates) and guest languages (via RPC).
/// </summary>
internal sealed class ScaffoldingService : IScaffoldingService
{
    private readonly IAppHostServerProjectFactory _appHostServerProjectFactory;
    private readonly ILanguageDiscovery _languageDiscovery;
    private readonly IInteractionService _interactionService;
    private readonly ILogger<ScaffoldingService> _logger;

    public ScaffoldingService(
        IAppHostServerProjectFactory appHostServerProjectFactory,
        ILanguageDiscovery languageDiscovery,
        IInteractionService interactionService,
        ILogger<ScaffoldingService> logger)
    {
        _appHostServerProjectFactory = appHostServerProjectFactory;
        _languageDiscovery = languageDiscovery;
        _interactionService = interactionService;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<bool> ScaffoldAsync(ScaffoldContext context, CancellationToken cancellationToken)
    {
        if (context.Language.LanguageId.Value.Equals(KnownLanguageId.CSharp, StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException("C# projects should be created using the template system via NewCommand.");
        }

        return await ScaffoldGuestLanguageAsync(context, cancellationToken);
    }

    private async Task<bool> ScaffoldGuestLanguageAsync(ScaffoldContext context, CancellationToken cancellationToken)
    {
        var directory = context.TargetDirectory;
        var language = context.Language;

        // Step 1: Resolve SDK and package strategy
        var sdkVersion = VersionHelper.GetDefaultSdkVersion();
        var config = AspireConfigFile.LoadOrCreate(directory.FullName, sdkVersion);

        // Include the code generation package for scaffolding and code gen
        var codeGenPackage = await _languageDiscovery.GetPackageForLanguageAsync(language.LanguageId, cancellationToken);
        var integrations = config.GetIntegrationReferences(sdkVersion, directory.FullName).ToList();
        if (codeGenPackage is not null)
        {
            var codeGenVersion = config.GetEffectiveSdkVersion(sdkVersion);
            integrations.Add(IntegrationReference.FromPackage(codeGenPackage, codeGenVersion));
        }

        var appHostServerProject = await _appHostServerProjectFactory.CreateAsync(directory.FullName, cancellationToken);
        var prepareSdkVersion = config.GetEffectiveSdkVersion(sdkVersion);

        var prepareResult = await _interactionService.ShowStatusAsync(
            "Preparing Aspire server...",
            () => appHostServerProject.PrepareAsync(prepareSdkVersion, integrations, cancellationToken),
            emoji: KnownEmojis.Gear);
        if (!prepareResult.Success)
        {
            if (prepareResult.Output is not null)
            {
                _interactionService.DisplayLines(prepareResult.Output.GetLines());
            }
            _interactionService.DisplayError("Failed to build AppHost server.");
            return false;
        }

        // Step 2: Start the server temporarily for scaffolding and code generation
        var currentPid = Environment.ProcessId;
        var (socketPath, serverProcess, _) = appHostServerProject.Run(currentPid, new Dictionary<string, string>());

        try
        {
            // Step 3: Connect to server and get scaffold templates via RPC
            await using var rpcClient = await AppHostRpcClient.ConnectAsync(socketPath, cancellationToken);

            var scaffoldFiles = await rpcClient.ScaffoldAppHostAsync(
                language.LanguageId,
                directory.FullName,
                context.ProjectName,
                cancellationToken);

            // Step 4: Write scaffold files to disk
            foreach (var (fileName, content) in scaffoldFiles)
            {
                var filePath = Path.Combine(directory.FullName, fileName);
                var fileDirectory = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(fileDirectory))
                {
                    Directory.CreateDirectory(fileDirectory);
                }
                await File.WriteAllTextAsync(filePath, content, cancellationToken);
            }

            _logger.LogDebug("Wrote {Count} scaffold files", scaffoldFiles.Count);

            // Step 5: Generate SDK code via RPC (must happen before dependency installation
            // because pylock.toml/requirements.txt reference the generated code directory)
            await GenerateCodeViaRpcAsync(
                directory.FullName,
                rpcClient,
                language,
                cancellationToken);

            // Step 6: Install dependencies using GuestRuntime
            var installResult = await _interactionService.ShowStatusAsync(
                $"Installing {language.DisplayName} dependencies...",
                () => InstallDependenciesAsync(directory, language, rpcClient, cancellationToken),
                emoji: KnownEmojis.Package);
            if (installResult != 0)
            {
                return false;
            }

            // Save channel and language to aspire.config.json (new format)
            // Read profiles from apphost.run.json (created by codegen) and merge into aspire.config.json
            var appHostRunPath = Path.Combine(directory.FullName, "apphost.run.json");
            var profiles = AspireConfigFile.ReadApphostRunProfiles(appHostRunPath, _logger);

            if (profiles is not null && File.Exists(appHostRunPath))
            {
                try
                {
                    // Delete apphost.run.json since profiles are now in aspire.config.json
                    File.Delete(appHostRunPath);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to delete apphost.run.json after reading profiles");
                }
            }

            config.Profiles = profiles;
            if (prepareResult.ChannelName is not null)
            {
                config.Channel = prepareResult.ChannelName;
            }
            config.AppHost ??= new AspireConfigAppHost();
            config.AppHost.Path ??= language.AppHostFileName;
            config.AppHost.Language = language.LanguageId;
            config.Save(directory.FullName);
            return true;
        }
        finally
        {
            // Step 7: Stop the server
            if (!serverProcess.HasExited)
            {
                try
                {
                    serverProcess.Kill(entireProcessTree: true);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Error killing AppHost server process after scaffolding");
                }
            }
        }
    }

    private async Task<int> InstallDependenciesAsync(
        DirectoryInfo directory,
        LanguageInfo language,
        IAppHostRpcClient rpcClient,
        CancellationToken cancellationToken)
    {
        var runtimeSpec = await rpcClient.GetRuntimeSpecAsync(language.LanguageId.Value, cancellationToken);
        var runtime = new GuestRuntime(runtimeSpec, _logger);

        var (initResult, initOutput) = await runtime.InitializeAsync(directory, cancellationToken);
        if (initResult != 0)
        {
            var lines = initOutput.GetLines().ToArray();
            if (lines.Length > 0)
            {
                _interactionService.DisplayLines(lines);
            }
            else
            {
                _interactionService.DisplayError($"Failed to initialize {language.DisplayName} environment.");
            }
            return initResult;
        }

        var (result, output) = await runtime.InstallDependenciesAsync(directory, cancellationToken);
        if (result != 0)
        {
            var lines = output.GetLines().ToArray();
            if (lines.Length > 0)
            {
                _interactionService.DisplayLines(lines);
            }
            else
            {
                _interactionService.DisplayError($"Failed to install {language.DisplayName} dependencies.");
            }
        }

        return result;
    }

    private async Task GenerateCodeViaRpcAsync(
        string directoryPath,
        IAppHostRpcClient rpcClient,
        LanguageInfo language,
        CancellationToken cancellationToken)
    {
        var generatedFiles = await rpcClient.GenerateCodeAsync(language.CodeGenerator, cancellationToken);

        // Write generated files to the output directory
        var outputPath = Path.Combine(directoryPath, LanguageInfo.GeneratedFolderName);
        Directory.CreateDirectory(outputPath);

        foreach (var (fileName, content) in generatedFiles)
        {
            var filePath = Path.Combine(outputPath, fileName);
            var fileDirectory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(fileDirectory))
            {
                Directory.CreateDirectory(fileDirectory);
            }
            await File.WriteAllTextAsync(filePath, content, cancellationToken);
        }

        _logger.LogDebug("Generated {Count} code files in {Path}", generatedFiles.Count, outputPath);
    }
}
