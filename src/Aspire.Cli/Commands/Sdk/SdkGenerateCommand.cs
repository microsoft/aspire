// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using Aspire.Cli.Configuration;
using Aspire.Cli.Interaction;
using Aspire.Cli.Projects;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Utils;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Commands.Sdk;

/// <summary>
/// Command for generating SDKs from Aspire integration libraries.
/// Takes an integration class library (with [AspireExport] attributes) and generates
/// typed SDK code for other languages.
/// 
/// Usage: aspire sdk generate ./MyIntegration.csproj -l typescript -o ./output
/// </summary>
internal sealed class SdkGenerateCommand : BaseCommand
{
    private readonly ILanguageDiscovery _languageDiscovery;
    private readonly IAppHostServerProjectFactory _appHostServerProjectFactory;
    private readonly ILogger<SdkGenerateCommand> _logger;

    private static readonly Argument<FileInfo> s_integrationArgument = new("integration")
    {
        Description = "Path to the integration project (.csproj) to generate SDK from"
    };
    private static readonly Option<string> s_languageOption = new("--language", "-l")
    {
        Description = "Target language for SDK generation (e.g., typescript)",
        Required = true
    };
    private static readonly Option<DirectoryInfo> s_outputOption = new("--output", "-o")
    {
        Description = "Output directory for generated SDK files",
        Required = true
    };

    public SdkGenerateCommand(
        ILanguageDiscovery languageDiscovery,
        IAppHostServerProjectFactory appHostServerProjectFactory,
        IFeatures features,
        ICliUpdateNotifier updateNotifier,
        CliExecutionContext executionContext,
        IInteractionService interactionService,
        ILogger<SdkGenerateCommand> logger,
        AspireCliTelemetry telemetry)
        : base("generate", "Generate typed SDKs from an Aspire integration library for use in other languages.", features, updateNotifier, executionContext, interactionService, telemetry)
    {
        _languageDiscovery = languageDiscovery;
        _appHostServerProjectFactory = appHostServerProjectFactory;
        _logger = logger;

        Arguments.Add(s_integrationArgument);
        Options.Add(s_languageOption);
        Options.Add(s_outputOption);
    }

    protected override async Task<int> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        var integrationProject = parseResult.GetValue(s_integrationArgument)!;
        var language = parseResult.GetValue(s_languageOption)!;
        var outputDir = parseResult.GetValue(s_outputOption)!;

        // Validate the integration project exists
        if (!integrationProject.Exists)
        {
            InteractionService.DisplayError($"Integration project not found: {integrationProject.FullName}");
            return ExitCodeConstants.FailedToFindProject;
        }

        if (!integrationProject.Extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            InteractionService.DisplayError($"Expected a .csproj file, got: {integrationProject.Extension}");
            return ExitCodeConstants.InvalidCommand;
        }

        // Resolve the language info
        var languageInfo = await GetLanguageInfoAsync(language, cancellationToken);
        if (languageInfo is null)
        {
            InteractionService.DisplayError($"Unsupported language: {language}");
            return ExitCodeConstants.InvalidCommand;
        }

        // Create output directory if it doesn't exist
        if (!outputDir.Exists)
        {
            outputDir.Create();
        }

        return await InteractionService.ShowStatusAsync(
            $"Generating {languageInfo.DisplayName} SDK from {integrationProject.Name}...",
            async () => await GenerateSdkAsync(integrationProject, languageInfo, outputDir, cancellationToken),
            emoji: KnownEmojis.Hammer);
    }

    private async Task<LanguageInfo?> GetLanguageInfoAsync(string language, CancellationToken cancellationToken)
    {
        var languages = await _languageDiscovery.GetAvailableLanguagesAsync(cancellationToken);

        // Match by language ID or code generator name
        return languages.FirstOrDefault(l =>
            l.LanguageId.Value.StartsWith(language, StringComparison.OrdinalIgnoreCase) ||
            l.CodeGenerator.Equals(language, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<int> GenerateSdkAsync(
        FileInfo integrationProject,
        LanguageInfo languageInfo,
        DirectoryInfo outputDir,
        CancellationToken cancellationToken)
    {
        // Use a temporary directory for the AppHost server
        var tempDir = Path.Combine(Path.GetTempPath(), "aspire-sdk-gen", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);

        try
        {
            var appHostServerProject = await _appHostServerProjectFactory.CreateAsync(tempDir, cancellationToken);

            // Get code generation package for the target language
            var codeGenPackage = await _languageDiscovery.GetPackageForLanguageAsync(languageInfo.LanguageId, cancellationToken);

            // Build integrations list — the integration project brings Aspire.Hosting transitively;
            // we only need to add the codegen package and the project reference itself.
            var integrations = new List<IntegrationReference>();
            if (codeGenPackage is not null)
            {
                integrations.Add(IntegrationReference.FromPackage(codeGenPackage, VersionHelper.GetDefaultTemplateVersion()));
            }

            // Add the integration project as a project reference
            integrations.Add(IntegrationReference.FromProject(
                Path.GetFileNameWithoutExtension(integrationProject.FullName),
                integrationProject.FullName));

            _logger.LogDebug("Building AppHost server for SDK generation");

            var prepareResult = await appHostServerProject.PrepareAsync(
                VersionHelper.GetDefaultTemplateVersion(),
                integrations,
                cancellationToken);

            if (!prepareResult.Success)
            {
                InteractionService.DisplayError("Failed to build SDK generation server.");
                if (prepareResult.Output is not null)
                {
                    foreach (var (_, line) in prepareResult.Output.GetLines())
                    {
                        InteractionService.DisplayMessage(KnownEmojis.Wrench, line);
                    }
                }
                return ExitCodeConstants.FailedToBuildArtifacts;
            }

            // Start the server
            var currentPid = Environment.ProcessId;
            var (socketPath, serverProcess, _) = appHostServerProject.Run(currentPid, new Dictionary<string, string>());

            try
            {
                // Connect and generate code
                await using var rpcClient = await AppHostRpcClient.ConnectAsync(socketPath, cancellationToken);

                _logger.LogDebug("Generating {Language} SDK via RPC", languageInfo.CodeGenerator);
                var generatedFiles = await rpcClient.GenerateCodeAsync(languageInfo.CodeGenerator, cancellationToken);

                // Write generated files
                var outputDirFullPath = Path.GetFullPath(outputDir.FullName);
                foreach (var (fileName, content) in generatedFiles)
                {
                    var filePath = Path.GetFullPath(Path.Combine(outputDir.FullName, fileName));

                    // Validate path is within output directory (prevent path traversal)
                    if (!filePath.StartsWith(outputDirFullPath, StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogWarning("Skipping file with invalid path: {FileName}", fileName);
                        continue;
                    }

                    var fileDirectory = Path.GetDirectoryName(filePath);
                    if (!string.IsNullOrEmpty(fileDirectory))
                    {
                        Directory.CreateDirectory(fileDirectory);
                    }
                    await File.WriteAllTextAsync(filePath, content, cancellationToken);
                    _logger.LogDebug("Wrote {FileName}", fileName);
                }

                InteractionService.DisplaySuccess($"Generated {generatedFiles.Count} files in {outputDir.FullName}");

                return ExitCodeConstants.Success;
            }
            finally
            {
                // Stop the server - just try to kill, catch if already exited
                try
                {
                    serverProcess.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // Process already exited - this is fine
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Error killing AppHost server process");
                }
            }
        }
        finally
        {
            // Clean up temp directory
            try
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, recursive: true);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to clean up temp directory {TempDir}", tempDir);
            }
        }
    }
}
