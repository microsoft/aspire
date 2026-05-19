// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.Globalization;
using System.Text;
using Aspire.Cli.Commands;
using Aspire.Cli.Interaction;
using Aspire.Cli.Projects;
using Aspire.Cli.Resources;
using Aspire.Cli.Scaffolding;
using Aspire.Cli.Utils;
using Aspire.Shared;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Templating;

internal sealed partial class CliTemplateFactory : ITemplateFactory
{
    private static readonly string[] s_emptyAppHostLanguages =
    [
        KnownLanguageId.CSharp,
        KnownLanguageId.TypeScript,
        KnownLanguageId.Python,
        KnownLanguageId.Go,
        KnownLanguageId.Java,
        KnownLanguageId.Rust
    ];

    private static readonly HashSet<string> s_binaryTemplateExtensions =
    [
        ".png",
        ".jpg",
        ".jpeg",
        ".gif",
        ".ico",
        ".bmp",
        ".webp",
        ".svg",
        ".woff",
        ".woff2",
        ".ttf",
        ".otf"
    ];

    private readonly Option<bool?> _localhostTldOption = new("--localhost-tld")
    {
        Description = TemplatingStrings.UseLocalhostTld_Description
    };

    private readonly Option<bool?> _useRedisCacheOption = new("--use-redis-cache")
    {
        Description = TemplatingStrings.UseRedisCache_Description
    };

    private readonly ILanguageDiscovery _languageDiscovery;
    private readonly IAppHostProjectFactory _projectFactory;
    private readonly IScaffoldingService _scaffoldingService;
    private readonly INewCommandPrompter _prompter;
    private readonly CliExecutionContext _executionContext;
    private readonly IInteractionService _interactionService;
    private readonly ICliHostEnvironment _hostEnvironment;
    private readonly TemplateNuGetConfigService _templateNuGetConfigService;
    private readonly ILogger<CliTemplateFactory> _logger;

    public CliTemplateFactory(
        ILanguageDiscovery languageDiscovery,
        IAppHostProjectFactory projectFactory,
        IScaffoldingService scaffoldingService,
        INewCommandPrompter prompter,
        CliExecutionContext executionContext,
        IInteractionService interactionService,
        ICliHostEnvironment hostEnvironment,
        TemplateNuGetConfigService templateNuGetConfigService,
        ILogger<CliTemplateFactory> logger)
    {
        _languageDiscovery = languageDiscovery;
        _projectFactory = projectFactory;
        _scaffoldingService = scaffoldingService;
        _prompter = prompter;
        _executionContext = executionContext;
        _interactionService = interactionService;
        _hostEnvironment = hostEnvironment;
        _templateNuGetConfigService = templateNuGetConfigService;
        _logger = logger;
    }

    public IEnumerable<ITemplate> GetTemplates()
    {
        return GetTemplateDefinitions();
    }

    public Task<IEnumerable<ITemplate>> GetTemplatesAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(GetTemplateDefinitions());
    }

    public Task<IEnumerable<ITemplate>> GetInitTemplatesAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IEnumerable<ITemplate>>(Array.Empty<ITemplate>());
    }

    private IEnumerable<ITemplate> GetTemplateDefinitions()
    {
        ITemplate[] templates =
        [
            new CallbackTemplate(
                KnownTemplateId.TypeScriptStarter,
                "Starter App (Express/React, TypeScript AppHost)",
                (ctx, projectName) => OutputPathHelper.GetUniqueDefaultOutputPath(projectName, ctx.WorkingDirectory.FullName),
                cmd => AddOptionIfMissing(cmd, _localhostTldOption),
                ApplyTypeScriptStarterTemplateAsync,
                runtime: TemplateRuntime.Cli,
                languageId: KnownLanguageId.TypeScript),

            new CallbackTemplate(
                KnownTemplateId.CSharpEmptyAppHost,
                "Empty AppHost (Choose language...)",
                (ctx, projectName) => OutputPathHelper.GetUniqueDefaultOutputPath(projectName, ctx.WorkingDirectory.FullName),
                cmd => AddOptionIfMissing(cmd, _localhostTldOption),
                ApplyEmptyAppHostTemplateAsync,
                runtime: TemplateRuntime.Cli,
                supportsLanguageCallback: IsSelectableEmptyAppHostLanguage,
                selectableAppHostLanguages: GetSelectableEmptyAppHostLanguages(),
                isEmpty: true),

            new CallbackTemplate(
                KnownTemplateId.TypeScriptEmptyAppHost,
                "Empty (TypeScript AppHost)",
                (ctx, projectName) => OutputPathHelper.GetUniqueDefaultOutputPath(projectName, ctx.WorkingDirectory.FullName),
                cmd => AddOptionIfMissing(cmd, _localhostTldOption),
                ApplyEmptyAppHostTemplateAsync,
                runtime: TemplateRuntime.Cli,
                languageId: KnownLanguageId.TypeScript,
                isEmpty: true,
                showInPrompt: false),

            new CallbackTemplate(
                KnownTemplateId.PythonEmptyAppHost,
                "Empty (Python AppHost)",
                (ctx, projectName) => OutputPathHelper.GetUniqueDefaultOutputPath(projectName, ctx.WorkingDirectory.FullName),
                cmd => AddOptionIfMissing(cmd, _localhostTldOption),
                ApplyEmptyAppHostTemplateAsync,
                runtime: TemplateRuntime.Cli,
                languageId: KnownLanguageId.Python,
                isEmpty: true,
                showInPrompt: false),

            new CallbackTemplate(
                KnownTemplateId.JavaEmptyAppHost,
                "Empty (Java AppHost)",
                (ctx, projectName) => OutputPathHelper.GetUniqueDefaultOutputPath(projectName, ctx.WorkingDirectory.FullName),
                cmd => AddOptionIfMissing(cmd, _localhostTldOption),
                ApplyEmptyAppHostTemplateAsync,
                runtime: TemplateRuntime.Cli,
                languageId: KnownLanguageId.Java,
                isEmpty: true,
                showInPrompt: false),

            new CallbackTemplate(
                KnownTemplateId.GoEmptyAppHost,
                "Empty (Go AppHost)",
                (ctx, projectName) => OutputPathHelper.GetUniqueDefaultOutputPath(projectName, ctx.WorkingDirectory.FullName),
                cmd => AddOptionIfMissing(cmd, _localhostTldOption),
                ApplyEmptyAppHostTemplateAsync,
                runtime: TemplateRuntime.Cli,
                languageId: KnownLanguageId.Go,
                isEmpty: true,
                showInPrompt: false),

            new CallbackTemplate(
                KnownTemplateId.RustEmptyAppHost,
                "Empty (Rust AppHost)",
                (ctx, projectName) => OutputPathHelper.GetUniqueDefaultOutputPath(projectName, ctx.WorkingDirectory.FullName),
                cmd => AddOptionIfMissing(cmd, _localhostTldOption),
                ApplyEmptyAppHostTemplateAsync,
                runtime: TemplateRuntime.Cli,
                languageId: KnownLanguageId.Rust,
                isEmpty: true,
                showInPrompt: false),

            new CallbackTemplate(
                KnownTemplateId.PythonStarter,
                "Starter App (FastAPI/React, TypeScript AppHost)",
                (ctx, projectName) => OutputPathHelper.GetUniqueDefaultOutputPath(projectName, ctx.WorkingDirectory.FullName),
                cmd =>
                {
                    AddOptionIfMissing(cmd, _localhostTldOption);
                    AddOptionIfMissing(cmd, _useRedisCacheOption);
                },
                ApplyPythonStarterTemplateAsync,
                runtime: TemplateRuntime.Cli,
                languageId: KnownLanguageId.TypeScript),

            new CallbackTemplate(
                KnownTemplateId.GoStarter,
                "Starter App (Go API + Redis, Go AppHost)",
                (ctx, projectName) => OutputPathHelper.GetUniqueDefaultOutputPath(projectName, ctx.WorkingDirectory.FullName),
                cmd => AddOptionIfMissing(cmd, _localhostTldOption),
                ApplyGoStarterTemplateAsync,
                runtime: TemplateRuntime.Cli,
                languageId: KnownLanguageId.Go)
        ];

        return templates.Where(IsTemplateAvailable);
    }

    private IReadOnlyList<string> GetSelectableEmptyAppHostLanguages()
    {
        return s_emptyAppHostLanguages
            .Where(IsSelectableEmptyAppHostLanguage)
            .ToArray();
    }

    private bool IsSelectableEmptyAppHostLanguage(string languageId)
    {
        return _languageDiscovery.GetLanguageById(new LanguageId(languageId)) is not null;
    }

    private bool IsTemplateAvailable(ITemplate template)
    {
        if (string.IsNullOrWhiteSpace(template.LanguageId))
        {
            return true;
        }

        return _languageDiscovery.GetLanguageById(new LanguageId(template.LanguageId)) is not null;
    }

    private async Task<string?> ResolveOutputPathAsync(TemplateInputs inputs, Func<CliExecutionContext, string, string> pathDeriver, string projectName, System.CommandLine.ParseResult parseResult, CancellationToken cancellationToken)
    {
        return await OutputPathHelper.ResolveOutputPathAsync(
            inputs.Output,
            _executionContext.WorkingDirectory.FullName,
            async () =>
            {
                var defaultOutputPath = pathDeriver(_executionContext, projectName);
                var outputPathValidator = OutputPathHelper.CreateOutputPathValidator(_executionContext.WorkingDirectory.FullName);
                return await _prompter.PromptForOutputPath(defaultOutputPath, parseResult, outputPathValidator, cancellationToken);
            },
            _interactionService);
    }

    private static string ApplyTokens(string content, string projectName, string projectNameLower, string aspireVersion, AppHostProfilePorts ports, string hostName = "localhost")
    {
        return content
            .Replace("{{projectName}}", projectName)
            .Replace("{{projectNameLower}}", projectNameLower)
            .Replace("{{aspireVersion}}", aspireVersion)
            .Replace("{{hostName}}", hostName)
            .Replace("{{httpPort}}", ports.DashboardHttpPort.ToString(CultureInfo.InvariantCulture))
            .Replace("{{httpsPort}}", ports.DashboardHttpsPort.ToString(CultureInfo.InvariantCulture))
            .Replace("{{otlpHttpPort}}", ports.OtlpHttpPort.ToString(CultureInfo.InvariantCulture))
            .Replace("{{otlpHttpsPort}}", ports.OtlpHttpsPort.ToString(CultureInfo.InvariantCulture))
            .Replace("{{resourceHttpPort}}", ports.ResourceServiceHttpPort.ToString(CultureInfo.InvariantCulture))
            .Replace("{{resourceHttpsPort}}", ports.ResourceServiceHttpsPort.ToString(CultureInfo.InvariantCulture));
    }

    private static AppHostProfilePorts GenerateRandomPorts()
    {
        return AppHostProfilePortGenerator.Generate(Random.Shared);
    }

    private static void AddOptionIfMissing(System.CommandLine.Command command, System.CommandLine.Option option)
    {
        if (!command.Options.Contains(option))
        {
            command.Options.Add(option);
        }
    }

    private async Task CopyTemplateTreeToDiskAsync(string templateRoot, string outputPath, Func<string, string> tokenReplacer, CancellationToken cancellationToken)
    {
        var assembly = typeof(CliTemplateFactory).Assembly;
        _logger.LogDebug("Copying embedded template tree '{TemplateRoot}' to '{OutputPath}'.", templateRoot, outputPath);

        var allResourceNames = assembly.GetManifestResourceNames();
        var resourcePrefix = $"{templateRoot}.";
        var resourceNames = allResourceNames
            .Where(name => name.StartsWith(resourcePrefix, StringComparison.Ordinal))
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();

        if (resourceNames.Length == 0)
        {
            _logger.LogDebug("No embedded resources found for template root '{TemplateRoot}'. Available manifest resources: {ManifestResources}", templateRoot, string.Join(", ", allResourceNames));
            throw new InvalidOperationException($"No embedded template resources found for '{templateRoot}'.");
        }

        _logger.LogDebug("Found {ResourceCount} embedded resources for template root '{TemplateRoot}': {TemplateResources}", resourceNames.Length, templateRoot, string.Join(", ", resourceNames));

        foreach (var resourceName in resourceNames)
        {
            var relativePath = resourceName[resourcePrefix.Length..].Replace('/', Path.DirectorySeparatorChar);
            var filePath = Path.Combine(outputPath, relativePath);
            var fileDirectory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(fileDirectory))
            {
                Directory.CreateDirectory(fileDirectory);
            }

            using var stream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException($"Embedded template resource not found: {resourceName}");

            _logger.LogDebug("Writing embedded template resource '{ResourceName}' to '{FilePath}'.", resourceName, filePath);
            if (s_binaryTemplateExtensions.Contains(Path.GetExtension(filePath)))
            {
                await using var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None);
                await stream.CopyToAsync(fileStream, cancellationToken);
            }
            else
            {
                using var reader = new StreamReader(stream);
                var content = await reader.ReadToEndAsync(cancellationToken);
                var transformedContent = tokenReplacer(content);
                await using var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None);
                await using var writer = new StreamWriter(fileStream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                await writer.WriteAsync(transformedContent.AsMemory(), cancellationToken);
                await writer.FlushAsync(cancellationToken);
            }
        }
    }

    private void DisplayPostCreationInstructions(string outputPath)
    {
        var currentDir = _executionContext.WorkingDirectory.FullName;
        var relativePath = Path.GetRelativePath(currentDir, outputPath);

        var pathComparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (!string.Equals(Path.GetFullPath(currentDir), Path.GetFullPath(outputPath), pathComparison))
        {
            _interactionService.DisplayMessage(KnownEmojis.Information, string.Format(CultureInfo.CurrentCulture, TemplatingStrings.RunCdThenAspireRun, relativePath));
        }
        else
        {
            _interactionService.DisplayMessage(KnownEmojis.Information, TemplatingStrings.RunAspireRun);
        }
    }
}
