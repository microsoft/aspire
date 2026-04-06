// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.Globalization;
using Aspire.Cli.Agents;
using Aspire.Cli.Configuration;
using Aspire.Cli.Interaction;
using Aspire.Cli.Projects;
using Aspire.Cli.Resources;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Utils;

namespace Aspire.Cli.Commands;

/// <summary>
/// Drops a skeleton AppHost and aspire.config.json, then installs the appropriate
/// init skill for an agent to complete the wiring. This is a thin launcher — the
/// heavy lifting (project discovery, dependency configuration, validation) is
/// delegated to the <c>aspire-init-typescript</c> or <c>aspire-init-csharp</c> skill.
/// </summary>
internal sealed class InitCommand : BaseCommand
{
    internal override HelpGroup HelpGroup => HelpGroup.AppCommands;

    private readonly CliExecutionContext _executionContext;
    private readonly ILanguageService _languageService;
    private readonly ISolutionLocator _solutionLocator;
    private readonly AgentInitCommand _agentInitCommand;
    private readonly ICliHostEnvironment _hostEnvironment;

    private readonly Option<string?> _languageOption;

    public InitCommand(
        ILanguageService languageService,
        ISolutionLocator solutionLocator,
        AspireCliTelemetry telemetry,
        IFeatures features,
        ICliUpdateNotifier updateNotifier,
        CliExecutionContext executionContext,
        IInteractionService interactionService,
        AgentInitCommand agentInitCommand,
        ICliHostEnvironment hostEnvironment)
        : base("init", InitCommandStrings.Description, features, updateNotifier, executionContext, interactionService, telemetry)
    {
        _executionContext = executionContext;
        _languageService = languageService;
        _solutionLocator = solutionLocator;
        _agentInitCommand = agentInitCommand;
        _hostEnvironment = hostEnvironment;

        _languageOption = new Option<string?>("--language")
        {
            Description = InitCommandStrings.LanguageOptionDescription
        };
        Options.Add(_languageOption);
    }

    protected override async Task<int> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        using var activity = Telemetry.StartDiagnosticActivity(this.Name);

        // Step 1: Get the language selection.
        var explicitLanguage = parseResult.GetValue(_languageOption);
        var selectedProject = await _languageService.GetOrPromptForProjectAsync(explicitLanguage, saveSelection: true, cancellationToken);

        var isCSharp = selectedProject.LanguageId == KnownLanguageId.CSharp;
        var workingDirectory = _executionContext.WorkingDirectory;

        // Step 2: Detect solution (C# only — determines single-file vs full project).
        FileInfo? solutionFile = null;
        if (isCSharp)
        {
            solutionFile = await _solutionLocator.FindSolutionFileAsync(workingDirectory, cancellationToken);
        }

        // Step 3: Drop the skeleton AppHost + aspire.config.json.
        var dropResult = isCSharp
            ? await DropCSharpSkeletonAsync(workingDirectory, solutionFile, cancellationToken)
            : await DropPolyglotSkeletonAsync(selectedProject.LanguageId, workingDirectory, cancellationToken);

        if (dropResult != ExitCodeConstants.Success)
        {
            InteractionService.DisplayError(string.Format(CultureInfo.CurrentCulture, InteractionServiceStrings.ProjectCouldNotBeCreated, ExecutionContext.LogFilePath));
            return dropResult;
        }

        // Step 4: Install the appropriate init skill.
        var initSkill = SkillDefinition.AspireInit;
        var skillInstalled = await InstallInitSkillAsync(workingDirectory, initSkill, cancellationToken);
        if (!skillInstalled)
        {
            InteractionService.DisplayError("Failed to install init skill.");
            return ExitCodeConstants.FailedToCreateNewProject;
        }

        InteractionService.DisplayEmptyLine();
        InteractionService.DisplayMessage(KnownEmojis.Sparkles, $"Init skill '{initSkill.Name}' installed. Ask your agent to run it to complete setup.");
        InteractionService.DisplayEmptyLine();

        // Step 5: Chain to aspire agent init for MCP server + evergreen skill configuration.
        var workspaceRoot = solutionFile?.Directory ?? workingDirectory;
        return await _agentInitCommand.PromptAndChainAsync(_hostEnvironment, InteractionService, ExitCodeConstants.Success, workspaceRoot, cancellationToken);
    }

    private async Task<int> DropCSharpSkeletonAsync(DirectoryInfo workingDirectory, FileInfo? solutionFile, CancellationToken cancellationToken)
    {
        if (solutionFile is not null)
        {
            return await DropCSharpProjectSkeletonAsync(solutionFile, cancellationToken);
        }

        return await DropCSharpSingleFileSkeletonAsync(workingDirectory, cancellationToken);
    }

    private Task<int> DropCSharpSingleFileSkeletonAsync(DirectoryInfo workingDirectory, CancellationToken cancellationToken)
    {
        _ = cancellationToken;

        var appHostPath = Path.Combine(workingDirectory.FullName, "apphost.cs");
        if (File.Exists(appHostPath))
        {
            InteractionService.DisplayMessage(KnownEmojis.CheckMark, "apphost.cs already exists — skipping.");
            return Task.FromResult(ExitCodeConstants.Success);
        }

        // Drop bare single-file apphost
        var appHostContent = """
            #:sdk Aspire.AppHost.Sdk

            var builder = DistributedApplication.CreateBuilder(args);

            // The aspire-init-csharp skill will wire up your projects here.

            builder.Build().Run();
            """;
        File.WriteAllText(appHostPath, appHostContent);
        InteractionService.DisplayMessage(KnownEmojis.CheckMark, "Created apphost.cs");

        // Drop aspire.config.json
        DropAspireConfig(workingDirectory, "apphost.cs", language: null);

        return Task.FromResult(ExitCodeConstants.Success);
    }

    private Task<int> DropCSharpProjectSkeletonAsync(FileInfo solutionFile, CancellationToken cancellationToken)
    {
        _ = cancellationToken;

        var solutionDir = solutionFile.Directory!;
        var solutionName = Path.GetFileNameWithoutExtension(solutionFile.Name);
        var appHostDirName = $"{solutionName}.AppHost";
        var appHostDirPath = Path.Combine(solutionDir.FullName, appHostDirName);

        if (Directory.Exists(appHostDirPath))
        {
            InteractionService.DisplayMessage(KnownEmojis.CheckMark, $"{appHostDirName}/ already exists — skipping.");
            return Task.FromResult(ExitCodeConstants.Success);
        }

        Directory.CreateDirectory(appHostDirPath);

        // Drop bare apphost.cs
        var appHostContent = """
            var builder = DistributedApplication.CreateBuilder(args);

            // The aspire-init-csharp skill will wire up your projects here.

            builder.Build().Run();
            """;
        File.WriteAllText(Path.Combine(appHostDirPath, "apphost.cs"), appHostContent);

        // Drop minimal .csproj
        var csprojContent = $"""
            <Project Sdk="Microsoft.NET.Sdk">

              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
                <IsAspireHost>true</IsAspireHost>
              </PropertyGroup>

              <ItemGroup>
                <PackageReference Include="Aspire.AppHost.Sdk" Version="*" />
              </ItemGroup>

            </Project>
            """;
        File.WriteAllText(Path.Combine(appHostDirPath, $"{appHostDirName}.csproj"), csprojContent);

        InteractionService.DisplayMessage(KnownEmojis.CheckMark, $"Created {appHostDirName}/");

        // Drop aspire.config.json at solution root
        var relativeAppHostPath = Path.Combine(appHostDirName, "apphost.cs");
        DropAspireConfig(solutionDir, relativeAppHostPath, language: null);

        return Task.FromResult(ExitCodeConstants.Success);
    }

    private Task<int> DropPolyglotSkeletonAsync(string languageId, DirectoryInfo workingDirectory, CancellationToken cancellationToken)
    {
        _ = cancellationToken;

        // Determine the apphost filename based on language
        var (appHostFileName, languageConfigValue) = languageId switch
        {
            KnownLanguageId.TypeScript => ("apphost.ts", "typescript/nodejs"),
            _ => throw new NotSupportedException($"Polyglot skeleton not yet supported for language: {languageId}")
        };

        var appHostPath = Path.Combine(workingDirectory.FullName, appHostFileName);
        if (File.Exists(appHostPath))
        {
            InteractionService.DisplayMessage(KnownEmojis.CheckMark, $"{appHostFileName} already exists — skipping.");
            return Task.FromResult(ExitCodeConstants.Success);
        }

        // Drop bare apphost.ts
        var appHostContent = """
            import { createBuilder } from './.modules/aspire.js';

            const builder = await createBuilder();

            // The aspire-init-typescript skill will wire up your projects here.

            await builder.build().run();
            """;
        File.WriteAllText(appHostPath, appHostContent);
        InteractionService.DisplayMessage(KnownEmojis.CheckMark, $"Created {appHostFileName}");

        // Drop aspire.config.json
        DropAspireConfig(workingDirectory, appHostFileName, languageConfigValue);

        return Task.FromResult(ExitCodeConstants.Success);
    }

    private void DropAspireConfig(DirectoryInfo directory, string appHostPath, string? language)
    {
        var configPath = Path.Combine(directory.FullName, AspireConfigFile.FileName);
        if (File.Exists(configPath))
        {
            InteractionService.DisplayMessage(KnownEmojis.CheckMark, $"{AspireConfigFile.FileName} already exists — skipping.");
            return;
        }

        var languageLine = language is not null
            ? $"""
                ,
                    "language": "{language}"
            """
            : string.Empty;

        var configContent = $$"""
            {
              "appHost": {
                "path": "{{appHostPath}}"{{languageLine}}
              }
            }
            """;

        File.WriteAllText(configPath, configContent);
        InteractionService.DisplayMessage(KnownEmojis.CheckMark, $"Created {AspireConfigFile.FileName}");
    }

    private async Task<bool> InstallInitSkillAsync(DirectoryInfo workspaceRoot, SkillDefinition skill, CancellationToken cancellationToken)
    {
        // Install the init skill to the standard .agents/skills/ location (workspace level only).
        var relativeSkillPath = Path.Combine(SkillLocation.Standard.RelativeSkillDirectory, skill.Name);
        var fullSkillDir = Path.Combine(workspaceRoot.FullName, relativeSkillPath);

        try
        {
            var skillFiles = await EmbeddedSkillResourceLoader.LoadTextFilesAsync(skill.EmbeddedResourceRoot!, cancellationToken);

            foreach (var skillFile in skillFiles)
            {
                var fullPath = Path.Combine(fullSkillDir, skillFile.RelativePath);
                var fileDir = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrEmpty(fileDir) && !Directory.Exists(fileDir))
                {
                    Directory.CreateDirectory(fileDir);
                }

                await File.WriteAllTextAsync(fullPath, skillFile.Content, cancellationToken);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            InteractionService.DisplayError($"Failed to install skill '{skill.Name}': {ex.Message}");
            return false;
        }
    }
}
