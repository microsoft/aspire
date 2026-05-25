// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Cli.Configuration;
using Aspire.Cli.Interaction;
using Aspire.Cli.Projects;
using Aspire.Cli.Resources;
using Aspire.Cli.Utils;
using Aspire.Hosting.Utils;
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
    internal const string BrownfieldTypeScriptAppHostDirectoryName = "aspire-apphost";

    private static readonly JsonSerializerOptions s_packageJsonSerializerOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        IndentSize = 2
    };

    private static readonly JsonDocumentOptions s_packageJsonDocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

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
        var scaffoldDirectory = GetScaffoldDirectory(directory, language);

        // Step 1: Resolve SDK and package strategy
        var sdkVersion = string.IsNullOrWhiteSpace(context.SdkVersion)
            ? VersionHelper.GetDefaultSdkVersion()
            : context.SdkVersion;
        var config = AspireConfigFile.LoadOrCreate(directory.FullName, sdkVersion);
        if (!string.IsNullOrWhiteSpace(context.SdkVersion))
        {
            config.SdkVersion = context.SdkVersion;
        }

        // Persist the channel only when the caller explicitly resolved one (Explicit `--channel`,
        // or NewCommand's identity-match against a registered Explicit channel — see
        // `CliTemplateFactory.EmptyTemplate.cs` for how `ScaffoldContext.Channel` is sourced).
        // Do NOT fall back to `CliExecutionContext.IdentityChannel`: an identity that isn't a
        // registered channel (e.g. `staging` on a CLI without the staging feature flag, or `pr-<N>`
        // on a machine without the matching hive) would otherwise pin a channel name that no
        // PSM rule can satisfy. When unset, `PrebuiltAppHostServer` aggregates sources from
        // every registered channel so `aspire add` / `aspire restore` still find the right
        // packages without a per-project pin.
        if (!string.IsNullOrEmpty(context.Channel))
        {
            config.Channel = context.Channel;
        }

        PreAddJavaScriptHostingForBrownfieldTypeScript(config, directory, language, sdkVersion);
        if (!string.IsNullOrWhiteSpace(context.SdkVersion) ||
            !string.IsNullOrEmpty(context.Channel))
        {
            config.Save(directory.FullName);
        }

        // Include the code generation package for scaffolding and code gen
        var codeGenPackage = await _languageDiscovery.GetPackageForLanguageAsync(language.LanguageId, cancellationToken);
        var integrations = config.GetIntegrationReferences(sdkVersion, directory.FullName).ToList();
        if (codeGenPackage is not null)
        {
            var codeGenVersion = config.GetEffectiveSdkVersion(sdkVersion);
            integrations.Add(IntegrationReference.FromPackage(codeGenPackage, codeGenVersion));
        }

        Directory.CreateDirectory(scaffoldDirectory.FullName);

        var appHostServerProject = await _appHostServerProjectFactory.CreateAsync(scaffoldDirectory.FullName, cancellationToken);
        var prepareSdkVersion = config.GetEffectiveSdkVersion(sdkVersion);

        var prepareResult = await _interactionService.ShowStatusAsync(
            "Preparing Aspire server...",
            () => appHostServerProject.PrepareAsync(prepareSdkVersion, integrations, requestedChannel: context.Channel, packageSourceOverride: context.PackageSourceOverride, cancellationToken: cancellationToken),
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
            scaffoldDirectory.FullName,
            context.ProjectName,
            cancellationToken);
        var appHostRelativePath = GetScaffoldedAppHostRelativePath(directory, scaffoldDirectory, language, scaffoldFiles.Keys);

        var conflictingFiles = GetConflictingScaffoldFiles(scaffoldDirectory.FullName, scaffoldFiles.Keys);
        if (conflictingFiles.Count > 0)
        {
            _logger.LogWarning(
                "Scaffolding in '{Directory}' would overwrite existing files: {Files}",
                scaffoldDirectory.FullName,
                string.Join(", ", conflictingFiles));
            _interactionService.DisplayError(TemplatingStrings.ProjectAlreadyExists);
            return false;
        }

        // Step 4: Write scaffold files to disk, merging package.json and .gitignore when they already exist.
        foreach (var (fileName, content) in scaffoldFiles)
        {
            var filePath = Path.Combine(scaffoldDirectory.FullName, fileName);
            var fileDirectory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(fileDirectory))
            {
                Directory.CreateDirectory(fileDirectory);
            }

            var contentToWrite = content;
            if (fileName.Equals(PackageJsonFileName, StringComparison.OrdinalIgnoreCase) && File.Exists(filePath))
            {
                var existingContent = await File.ReadAllTextAsync(filePath, cancellationToken);
                contentToWrite = PackageJsonMerger.Merge(
                    existingContent,
                    content,
                    _logger,
                    toolchainCommand: GetPackageManagerCommand(scaffoldDirectory, language));
            }
            else if (IsGitIgnoreFile(fileName) && File.Exists(filePath))
            {
                var existingContent = await File.ReadAllTextAsync(filePath, cancellationToken);
                contentToWrite = MergeGitIgnoreContent(existingContent, content);
            }

            await File.WriteAllTextAsync(filePath, contentToWrite, cancellationToken);
        }

        _logger.LogDebug("Wrote {Count} scaffold files", scaffoldFiles.Count);

        if (IsNestedBrownfieldTypeScriptAppHost(directory, scaffoldDirectory, language))
        {
            await AddRootTypeScriptAppHostScriptsAsync(directory, scaffoldDirectory, cancellationToken);
        }

        // Step 5: Generate SDK code via RPC (must happen before dependency installation
        // because pylock.toml/requirements.txt reference the generated code directory)
        await GenerateCodeViaRpcAsync(
            scaffoldDirectory.FullName,
            rpcClient,
            language,
            cancellationToken);

        // Step 6: Install dependencies using GuestRuntime
        var installResult = await _interactionService.ShowStatusAsync(
            $"Installing {language.DisplayName} dependencies...",
            () => InstallDependenciesAsync(scaffoldDirectory, language, rpcClient, cancellationToken),
            emoji: KnownEmojis.Package);
        if (installResult != 0)
        {
            return false;
        }

        // Save channel and language to aspire.config.json (new format)
        // Read profiles from apphost.run.json (created by codegen) and merge into aspire.config.json
        var appHostRunPath = Path.Combine(scaffoldDirectory.FullName, "apphost.run.json");
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
        config.AppHost ??= new AspireConfigAppHost();
        config.AppHost.Path ??= appHostRelativePath;
        config.AppHost.Language = language.LanguageId;
        config.Save(directory.FullName);
        return true;
    }

    internal static DirectoryInfo GetScaffoldDirectory(DirectoryInfo directory, LanguageInfo language)
    {
        if (IsTypeScriptLanguage(language) && File.Exists(Path.Combine(directory.FullName, PackageJsonFileName)))
        {
            // Brownfield JS/TS apps already have package-level module, script, lint, and engine semantics.
            // Keep the Aspire AppHost in its own package boundary so scaffolding cannot change how the app runs.
            return new DirectoryInfo(Path.Combine(directory.FullName, BrownfieldTypeScriptAppHostDirectoryName));
        }

        return directory;
    }

    internal static string GetAppHostPath(DirectoryInfo directory, LanguageInfo language)
    {
        var scaffoldDirectory = GetScaffoldDirectory(directory, language);
        var appHostFileName = language.AppHostFileName ?? throw new NotSupportedException($"AppHost file not defined for language: {language.LanguageId}");
        return Path.Combine(scaffoldDirectory.FullName, appHostFileName);
    }

    private static bool IsNestedBrownfieldTypeScriptAppHost(DirectoryInfo rootDirectory, DirectoryInfo scaffoldDirectory, LanguageInfo language)
        => IsTypeScriptLanguage(language) &&
           !string.Equals(
               rootDirectory.FullName.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
               scaffoldDirectory.FullName.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
               StringComparison.Ordinal);

    private async Task AddRootTypeScriptAppHostScriptsAsync(DirectoryInfo rootDirectory, DirectoryInfo appHostDirectory, CancellationToken cancellationToken)
    {
        var packageJsonPath = Path.Combine(rootDirectory.FullName, PackageJsonFileName);
        var existingContent = await File.ReadAllTextAsync(packageJsonPath, cancellationToken);

        JsonObject packageJson;
        try
        {
            packageJson = JsonNode.Parse(existingContent, documentOptions: s_packageJsonDocumentOptions) as JsonObject
                ?? throw new JsonException("The root package.json is not a JSON object.");
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse root package.json at '{PackageJsonPath}'.", packageJsonPath);
            _interactionService.DisplayError($"Failed to parse root package.json: {ex.Message}");
            throw;
        }

        var scripts = EnsureJsonObject(packageJson, "scripts");
        var toolchain = TypeScriptAppHostToolchainResolver.Resolve(rootDirectory, _logger);
        var relativeAppHostDirectory = PathNormalizer.NormalizePathForStorage(Path.GetRelativePath(rootDirectory.FullName, appHostDirectory.FullName));

        scripts["aspire:start"] = CreateRootDelegateScript(toolchain, relativeAppHostDirectory, "aspire:start");
        scripts["aspire:build"] = CreateRootDelegateScript(toolchain, relativeAppHostDirectory, "aspire:build");
        scripts["aspire:dev"] = CreateRootDelegateScript(toolchain, relativeAppHostDirectory, "aspire:dev");

        var serializedPackageJson = SerializePackageJson(packageJson, existingContent);
        await File.WriteAllTextAsync(packageJsonPath, serializedPackageJson, cancellationToken);
    }

    internal static string SerializePackageJson(JsonObject packageJson, string existingContent)
    {
        var serializedPackageJson = packageJson.ToJsonString(s_packageJsonSerializerOptions);
        var trailingNewLine = existingContent.EndsWith("\r\n", StringComparison.Ordinal)
            ? "\r\n"
            : existingContent.EndsWith('\n') ? "\n" : null;

        if (trailingNewLine is not null)
        {
            serializedPackageJson += trailingNewLine;
        }

        return serializedPackageJson;
    }

    internal static string GetScaffoldedAppHostRelativePath(
        DirectoryInfo rootDirectory,
        DirectoryInfo scaffoldDirectory,
        LanguageInfo language,
        IEnumerable<string> scaffoldFileNames)
    {
        var appHostFileName = scaffoldFileNames.FirstOrDefault(fileName =>
            language.MatchesFile(Path.GetFileName(fileName)));

        appHostFileName ??= language.AppHostFileName ?? throw new NotSupportedException($"AppHost file not defined for language: {language.LanguageId}");

        return PathNormalizer.NormalizePathForStorage(
            Path.GetRelativePath(rootDirectory.FullName, Path.Combine(scaffoldDirectory.FullName, appHostFileName)));
    }

    private static JsonObject EnsureJsonObject(JsonObject parent, string propertyName)
    {
        if (parent[propertyName] is JsonObject obj)
        {
            return obj;
        }

        obj = new JsonObject();
        parent[propertyName] = obj;
        return obj;
    }

    private static string CreateRootDelegateScript(TypeScriptAppHostToolchain toolchain, string relativeAppHostDirectory, string scriptName)
    {
        return toolchain switch
        {
            TypeScriptAppHostToolchain.Npm => $"npm --prefix {relativeAppHostDirectory} run {scriptName}",
            TypeScriptAppHostToolchain.Pnpm => $"pnpm --dir {relativeAppHostDirectory} run {scriptName}",
            TypeScriptAppHostToolchain.Yarn => $"yarn --cwd {relativeAppHostDirectory} run {scriptName}",
            TypeScriptAppHostToolchain.Bun => $"bun --cwd {relativeAppHostDirectory} run {scriptName}",
            _ => throw new ArgumentOutOfRangeException(nameof(toolchain), toolchain, null)
        };
    }

    private async Task<int> InstallDependenciesAsync(
        DirectoryInfo directory,
        LanguageInfo language,
        IAppHostRpcClient rpcClient,
        CancellationToken cancellationToken)
    {
        var runtimeSpec = await rpcClient.GetRuntimeSpecAsync(language.LanguageId.Value, cancellationToken);
        if (TypeScriptAppHostToolchainResolver.IsTypeScriptLanguage(language))
        {
            var toolchain = TypeScriptAppHostToolchainResolver.Resolve(directory, _logger);
            runtimeSpec = TypeScriptAppHostToolchainResolver.ApplyToRuntimeSpec(runtimeSpec, toolchain);
        }

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
            if (MissingJavaScriptToolWarning.IsMatch(lines))
            {
                if (lines.Length > 0)
                {
                    _interactionService.DisplayLines(lines);
                }

                _interactionService.DisplayMessage(
                    KnownEmojis.Warning,
                    MissingJavaScriptToolWarning.GetMessage(directory, language));
                return 0;
            }

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

    private string GetPackageManagerCommand(DirectoryInfo directory, LanguageInfo language)
    {
        if (!TypeScriptAppHostToolchainResolver.IsTypeScriptLanguage(language))
        {
            return "npm";
        }

        var toolchain = TypeScriptAppHostToolchainResolver.Resolve(directory, _logger);
        return TypeScriptAppHostToolchainResolver.GetCommandName(toolchain);
    }

    internal static IReadOnlyList<string> GetConflictingScaffoldFiles(string rootDirectory, IEnumerable<string> scaffoldFileNames)
    {
        ArgumentException.ThrowIfNullOrEmpty(rootDirectory);
        ArgumentNullException.ThrowIfNull(scaffoldFileNames);

        var conflicts = new List<string>();

        foreach (var fileName in scaffoldFileNames)
        {
            if (IsGitIgnoreFile(fileName) || IsPackageJsonFile(fileName))
            {
                continue;
            }

            var filePath = Path.Combine(rootDirectory, fileName);
            if (File.Exists(filePath) || Directory.Exists(filePath))
            {
                conflicts.Add(fileName);
            }
        }

        return conflicts;
    }

    internal static string MergeGitIgnoreContent(string existingContent, string scaffoldContent)
    {
        ArgumentNullException.ThrowIfNull(existingContent);
        ArgumentNullException.ThrowIfNull(scaffoldContent);

        if (string.IsNullOrEmpty(existingContent))
        {
            return scaffoldContent;
        }

        var existingEntries = ReadGitIgnoreEntries(existingContent).ToHashSet(StringComparer.Ordinal);
        var existingNormalized = existingEntries
            .Select(NormalizeGitIgnoreEntry)
            .ToHashSet(StringComparer.Ordinal);

        var missingEntries = ReadGitIgnoreEntries(scaffoldContent)
            .Where(entry => !existingEntries.Contains(entry)
                && !existingNormalized.Contains(NormalizeGitIgnoreEntry(entry)))
            .ToArray();

        if (missingEntries.Length == 0)
        {
            return existingContent;
        }

        var newline = existingContent.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var mergedContent = existingContent;
        if (!mergedContent.EndsWith("\n", StringComparison.Ordinal))
        {
            mergedContent += newline;
        }

        return mergedContent + string.Join(newline, missingEntries) + newline;
    }

    private static bool IsGitIgnoreFile(string fileName)
        => Path.GetFileName(fileName).Equals(".gitignore", StringComparison.Ordinal);

    private static bool IsPackageJsonFile(string fileName)
        => Path.GetFileName(fileName).Equals(PackageJsonFileName, StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> ReadGitIgnoreEntries(string content)
    {
        using var reader = new StringReader(content);
        string? line;

        while ((line = reader.ReadLine()) is not null)
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                yield return line.TrimEnd();
            }
        }
    }

    // Normalizes a .gitignore entry so rooted (`/foo/`) and unrooted (`foo/`) forms
    // are treated as equivalent when deciding whether to append a scaffold entry.
    private static string NormalizeGitIgnoreEntry(string entry)
        => entry.StartsWith('/') ? entry[1..] : entry;
}
