// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Nodes;
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
    private const string PackageJsonFileName = "package.json";
    private const string JavaScriptHostingPackageName = "Aspire.Hosting.JavaScript";

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
        PreAddJavaScriptHostingForBrownfieldTypeScript(config, directory, language, sdkVersion);

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
        await using var serverSession = AppHostServerSession.Start(
            appHostServerProject,
            environmentVariables: null,
            debug: false,
            _logger);

        // Step 3: Connect to server and get scaffold templates via RPC
        var rpcClient = await serverSession.GetRpcClientAsync(cancellationToken);

        var scaffoldFiles = await rpcClient.ScaffoldAppHostAsync(
            language.LanguageId,
            directory.FullName,
            context.ProjectName,
            cancellationToken);

        // Step 4: Write scaffold files to disk, merging package.json with existing content
        foreach (var (fileName, content) in scaffoldFiles)
        {
            var filePath = Path.Combine(directory.FullName, fileName);
            var fileDirectory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(fileDirectory))
            {
                Directory.CreateDirectory(fileDirectory);
            }

            var contentToWrite = content;
            if (fileName.Equals(PackageJsonFileName, StringComparison.OrdinalIgnoreCase) && File.Exists(filePath))
            {
                contentToWrite = MergePackageJson(filePath, content, _logger);
            }

            await File.WriteAllTextAsync(filePath, contentToWrite, cancellationToken);
        }

        _logger.LogDebug("Wrote {Count} scaffold files", scaffoldFiles.Count);

        // Step 5: Install dependencies using GuestRuntime
        var installResult = await _interactionService.ShowStatusAsync(
            $"Installing {language.DisplayName} dependencies...",
            () => InstallDependenciesAsync(directory, language, rpcClient, cancellationToken),
            emoji: KnownEmojis.Package);
        if (installResult != 0)
        {
            return false;
        }

        // Step 6: Generate SDK code via RPC
        await GenerateCodeViaRpcAsync(
            directory.FullName,
            rpcClient,
            language,
            cancellationToken);

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

    private async Task<int> InstallDependenciesAsync(
        DirectoryInfo directory,
        LanguageInfo language,
        IAppHostRpcClient rpcClient,
        CancellationToken cancellationToken)
    {
        var runtimeSpec = await rpcClient.GetRuntimeSpecAsync(language.LanguageId.Value, cancellationToken);
        var runtime = new GuestRuntime(runtimeSpec, _logger);

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

    private const string GeneratedFolderName = ".modules";

    private async Task GenerateCodeViaRpcAsync(
        string directoryPath,
        IAppHostRpcClient rpcClient,
        LanguageInfo language,
        CancellationToken cancellationToken)
    {
        var generatedFiles = await rpcClient.GenerateCodeAsync(language.CodeGenerator, cancellationToken);

        // Write generated files to the output directory
        var outputPath = Path.Combine(directoryPath, GeneratedFolderName);
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

    private static void PreAddJavaScriptHostingForBrownfieldTypeScript(
        AspireConfigFile config,
        DirectoryInfo directory,
        LanguageInfo language,
        string defaultSdkVersion)
    {
        if (!IsTypeScriptLanguage(language) ||
            !File.Exists(Path.Combine(directory.FullName, PackageJsonFileName)) ||
            config.Packages?.ContainsKey(JavaScriptHostingPackageName) == true)
        {
            return;
        }

        config.AddOrUpdatePackage(JavaScriptHostingPackageName, config.GetEffectiveSdkVersion(defaultSdkVersion));
    }

    private static bool IsTypeScriptLanguage(LanguageInfo language)
    {
        return language.LanguageId.Value.Equals(KnownLanguageId.TypeScript, StringComparison.OrdinalIgnoreCase) ||
            language.LanguageId.Value.Equals(KnownLanguageId.TypeScriptAlias, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Merges scaffold-generated package.json with an existing one on disk.
    /// Preserves all existing properties and only adds new ones from the scaffold output.
    /// For nested objects (scripts, dependencies), existing values are kept.
    /// </summary>
    private static string MergePackageJson(string existingFilePath, string scaffoldContent, ILogger logger)
    {
        try
        {
            var existingContent = File.ReadAllText(existingFilePath);
            if (string.IsNullOrWhiteSpace(existingContent))
            {
                return scaffoldContent;
            }

            var existingJson = JsonNode.Parse(existingContent)?.AsObject();
            var scaffoldJson = JsonNode.Parse(scaffoldContent)?.AsObject();

            if (existingJson is null || scaffoldJson is null)
            {
                return scaffoldContent;
            }

            DeepMerge(existingJson, scaffoldJson);
            return existingJson.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to merge existing package.json, using scaffold output as-is");
            return scaffoldContent;
        }
    }

    /// <summary>
    /// Deep merges properties from source into target. Existing target values are preserved.
    /// For nested objects, recursively merges. Scalar values in target are never overwritten.
    /// </summary>
    private static void DeepMerge(JsonObject target, JsonObject source)
    {
        foreach (var (key, sourceValue) in source)
        {
            if (sourceValue is null)
            {
                continue;
            }

            var targetValue = target[key];

            if (targetValue is null)
            {
                // Property doesn't exist in target — add it from scaffold
                target[key] = sourceValue.DeepClone();
            }
            else if (targetValue is JsonObject targetObj && sourceValue is JsonObject sourceObj)
            {
                // Both are objects — recursively merge (e.g., scripts, dependencies)
                DeepMerge(targetObj, sourceObj);
            }
            // else: target already has a non-object value — keep it
        }
    }
}
