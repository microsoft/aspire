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
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Templating;

internal sealed partial class CliTemplateFactory : ITemplateFactory
{
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
        return Task.FromResult<IEnumerable<ITemplate>>([]);
    }

    private IEnumerable<ITemplate> GetTemplateDefinitions()
    {
        return
        [
            new CallbackTemplate(
                KnownTemplateId.TypeScriptStarter,
                "Starter App (Express/React)",
                projectName => $"./{projectName}",
                cmd => AddOptionIfMissing(cmd, _localhostTldOption),
                ApplyTypeScriptStarterTemplateAsync,
                runtime: TemplateRuntime.Cli,
                languageId: KnownLanguageId.TypeScript),

            new CallbackTemplate(
                KnownTemplateId.CSharpEmptyAppHost,
                "Empty (C# AppHost)",
                projectName => $"./{projectName}",
                cmd => AddOptionIfMissing(cmd, _localhostTldOption),
                ApplyEmptyAppHostTemplateAsync,
                runtime: TemplateRuntime.Cli,
                languageId: KnownLanguageId.CSharp,
                isEmpty: true),

            new CallbackTemplate(
                KnownTemplateId.TypeScriptEmptyAppHost,
                "Empty (TypeScript AppHost)",
                projectName => $"./{projectName}",
                cmd => AddOptionIfMissing(cmd, _localhostTldOption),
                ApplyEmptyAppHostTemplateAsync,
                runtime: TemplateRuntime.Cli,
                languageId: KnownLanguageId.TypeScript,
                isEmpty: true)
        ];
    }

    private static string ApplyTokens(string content, string projectName, string projectNameLower, string aspireVersion, TemplatePorts ports, string hostName = "localhost")
    {
        return content
            .Replace("{{projectName}}", projectName)
            .Replace("{{projectNameLower}}", projectNameLower)
            .Replace("{{aspireVersion}}", aspireVersion)
            .Replace("{{hostName}}", hostName)
            .Replace("{{httpPort}}", ports.HttpPort.ToString(CultureInfo.InvariantCulture))
            .Replace("{{httpsPort}}", ports.HttpsPort.ToString(CultureInfo.InvariantCulture))
            .Replace("{{otlpHttpPort}}", ports.OtlpHttpPort.ToString(CultureInfo.InvariantCulture))
            .Replace("{{otlpHttpsPort}}", ports.OtlpHttpsPort.ToString(CultureInfo.InvariantCulture))
            .Replace("{{mcpHttpPort}}", ports.McpHttpPort.ToString(CultureInfo.InvariantCulture))
            .Replace("{{mcpHttpsPort}}", ports.McpHttpsPort.ToString(CultureInfo.InvariantCulture))
            .Replace("{{resourceHttpPort}}", ports.ResourceHttpPort.ToString(CultureInfo.InvariantCulture))
            .Replace("{{resourceHttpsPort}}", ports.ResourceHttpsPort.ToString(CultureInfo.InvariantCulture));
    }

    private static TemplatePorts GenerateRandomPorts()
    {
        return new TemplatePorts(
            HttpPort: Random.Shared.Next(15000, 15300),
            HttpsPort: Random.Shared.Next(17000, 17300),
            OtlpHttpPort: Random.Shared.Next(19000, 19300),
            OtlpHttpsPort: Random.Shared.Next(21000, 21300),
            McpHttpPort: Random.Shared.Next(18000, 18300),
            McpHttpsPort: Random.Shared.Next(23000, 23300),
            ResourceHttpPort: Random.Shared.Next(20000, 20300),
            ResourceHttpsPort: Random.Shared.Next(22000, 22300));
    }

    private sealed record TemplatePorts(
        int HttpPort, int HttpsPort,
        int OtlpHttpPort, int OtlpHttpsPort,
        int McpHttpPort, int McpHttpsPort,
        int ResourceHttpPort, int ResourceHttpsPort);

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
