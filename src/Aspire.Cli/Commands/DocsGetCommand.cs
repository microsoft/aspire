// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using Aspire.Cli.Configuration;
using Aspire.Cli.Interaction;
using Aspire.Cli.Documentation.Docs;
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
            var content = MarkdownToSpectreConverter.ConvertToPlainText(doc.Content)
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace("\r", "\n", StringComparison.Ordinal);

            InteractionService.DisplayRawText(WrapForConsole(content));
        }

        return ExitCodeConstants.Success;
    }

    private static string WrapForConsole(string text, int width = 100)
    {
        var wrappedLines = new List<string>();

        foreach (var line in text.Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                wrappedLines.Add(string.Empty);
                continue;
            }

            WrapLine(line.TrimEnd(), width, wrappedLines);
        }

        return string.Join("\n", wrappedLines);
    }

    private static void WrapLine(string line, int width, List<string> wrappedLines)
    {
        var prefixLength = GetPrefixLength(line);
        var prefix = line[..prefixLength];
        var remaining = line[prefixLength..].TrimStart();
        var continuationPrefix = new string(' ', prefixLength);
        var currentPrefix = prefix;

        while (!string.IsNullOrEmpty(remaining))
        {
            var availableWidth = Math.Max(1, width - currentPrefix.Length);

            if (remaining.Length <= availableWidth)
            {
                wrappedLines.Add(currentPrefix + remaining);
                return;
            }

            var splitIndex = remaining.LastIndexOf(' ', availableWidth);
            if (splitIndex <= 0)
            {
                splitIndex = availableWidth;
            }

            wrappedLines.Add(currentPrefix + remaining[..splitIndex].TrimEnd());
            remaining = remaining[splitIndex..].TrimStart();
            currentPrefix = continuationPrefix;
        }
    }

    private static int GetPrefixLength(string line)
    {
        if (line.StartsWith("* ", StringComparison.Ordinal))
        {
            return 2;
        }

        var index = 0;
        while (index < line.Length && char.IsDigit(line[index]))
        {
            index++;
        }

        if (index > 0 &&
            index + 1 < line.Length &&
            line[index] == '.' &&
            line[index + 1] == ' ')
        {
            return index + 2;
        }

        return 0;
    }
}
