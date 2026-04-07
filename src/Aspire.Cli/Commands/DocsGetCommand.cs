// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using Aspire.Cli.Configuration;
using Aspire.Cli.Interaction;
using Aspire.Cli.Mcp.Docs;
using Aspire.Cli.Resources;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Utils;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Commands;

/// <summary>
/// Command to get the full content of a documentation page by its slug.
/// </summary>
internal sealed partial class DocsGetCommand : BaseCommand
{
    private readonly IDocsIndexService _docsIndexService;
    private readonly ILogger<DocsGetCommand> _logger;

    private static readonly Argument<string> s_slugArgument = new("slug")
    {
        Description = DocsCommandStrings.SlugArgumentDescription
    };

    private static readonly Option<string?> s_sectionOption = new("--section")
    {
        Description = DocsCommandStrings.SectionOptionDescription
    };

    private static readonly Option<OutputFormat> s_formatOption = new("--format")
    {
        Description = DocsCommandStrings.FormatOptionDescription
    };

    public DocsGetCommand(
        IInteractionService interactionService,
        IDocsIndexService docsIndexService,
        IFeatures features,
        ICliUpdateNotifier updateNotifier,
        CliExecutionContext executionContext,
        AspireCliTelemetry telemetry,
        ILogger<DocsGetCommand> logger)
        : base("get", DocsCommandStrings.GetDescription, features, updateNotifier, executionContext, interactionService, telemetry)
    {
        _docsIndexService = docsIndexService;
        _logger = logger;

        Arguments.Add(s_slugArgument);
        Options.Add(s_sectionOption);
        Options.Add(s_formatOption);
    }

    protected override bool UpdateNotificationsEnabled => false;

    protected override async Task<int> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        using var activity = Telemetry.StartDiagnosticActivity(Name);

        var slug = parseResult.GetValue(s_slugArgument)!;
        var section = parseResult.GetValue(s_sectionOption);
        var format = parseResult.GetValue(s_formatOption);

        _logger.LogDebug("Getting documentation for slug '{Slug}' (section: {Section})", slug, section ?? "(all)");

        // Get doc with status indicator
        var doc = await InteractionService.ShowStatusAsync(
            DocsCommandStrings.LoadingDocumentation,
            async () => await _docsIndexService.GetDocumentAsync(slug, section, cancellationToken));

        if (doc is null)
        {
            InteractionService.DisplayError(string.Format(CultureInfo.CurrentCulture, DocsCommandStrings.DocumentNotFound, slug));
            return ExitCodeConstants.InvalidCommand;
        }

        if (format is OutputFormat.Json)
        {
            var json = JsonSerializer.Serialize(doc, JsonSourceGenerationContext.RelaxedEscaping.DocsContent);
            // Structured output always goes to stdout.
            InteractionService.DisplayRawText(json, ConsoleOutput.Standard);
        }
        else
        {
            // Format the markdown for better terminal readability
            var formatted = FormatMarkdownForTerminal(doc.Content);
            // Structured output always goes to stdout.
            InteractionService.DisplayRawText(formatted, ConsoleOutput.Standard);
        }

        return ExitCodeConstants.Success;
    }

    /// <summary>
    /// Formats minified markdown content for better terminal readability by inserting line breaks.
    /// </summary>
    private static string FormatMarkdownForTerminal(string content)
    {
        // The llms.txt format has markdown on single lines - insert breaks for readability
        // Add newline before headings (##, ###)
        content = HeadingRegex().Replace(content, "\n\n$0");

        // Add newlines around code blocks
        content = CodeBlockStartRegex().Replace(content, "\n$0\n");
        content = CodeBlockEndRegex().Replace(content, "\n$0\n");

        // Clean up excessive newlines
        content = ExcessiveNewlinesRegex().Replace(content, "\n\n");

        return content.Trim();
    }

    // Match markdown headings: ## or ### at start or after space (not C#)
    [System.Text.RegularExpressions.GeneratedRegex(@"(?<=\s)(#{2,6}\s)")]
    private static partial System.Text.RegularExpressions.Regex HeadingRegex();

    [System.Text.RegularExpressions.GeneratedRegex(@"(?<!\n)```\w*")]
    private static partial System.Text.RegularExpressions.Regex CodeBlockStartRegex();

    [System.Text.RegularExpressions.GeneratedRegex(@"```(?!\w)(?!\n)")]
    private static partial System.Text.RegularExpressions.Regex CodeBlockEndRegex();

    [System.Text.RegularExpressions.GeneratedRegex(@"\n{3,}")]
    private static partial System.Text.RegularExpressions.Regex ExcessiveNewlinesRegex();
}
