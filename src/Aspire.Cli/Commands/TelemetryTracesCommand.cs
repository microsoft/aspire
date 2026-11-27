// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Interaction;
using Aspire.Cli.Mcp.Tools;
using Aspire.Cli.Resources;
using Aspire.Cli.Utils;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Utils;
using Aspire.Otlp.Serialization;
using Aspire.Shared.ConsoleLogs;
using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace Aspire.Cli.Commands;

/// <summary>
/// Command to view traces from the Dashboard telemetry API.
/// </summary>
internal sealed class TelemetryTracesCommand : BaseCommand
{
    private const int MaxSpanNameLength = 50;
    private const int MaxDbQueryTextLength = 200;

    private readonly AppHostConnectionResolver _connectionResolver;
    private readonly ILogger<TelemetryTracesCommand> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ResourceColorMap _resourceColorMap;
    private readonly TimeProvider _timeProvider;

    // Shared options from TelemetryCommandHelpers
    private static readonly Argument<string?> s_resourceArgument = TelemetryCommandHelpers.CreateResourceArgument();
    private static readonly OptionWithLegacy<FileInfo?> s_appHostOption = TelemetryCommandHelpers.CreateAppHostOption();
    private static readonly Option<OutputFormat> s_formatOption = TelemetryCommandHelpers.CreateFormatOption();
    private static readonly Option<int?> s_limitOption = TelemetryCommandHelpers.CreateLimitOption();
    private static readonly Option<string?> s_traceIdOption = TelemetryCommandHelpers.CreateTraceIdOption("--trace-id", "-t");
    private static readonly Option<bool?> s_hasErrorOption = TelemetryCommandHelpers.CreateHasErrorOption();
    private static readonly Option<string?> s_dashboardUrlOption = TelemetryCommandHelpers.CreateDashboardUrlOption();
    private static readonly Option<string?> s_apiKeyOption = TelemetryCommandHelpers.CreateApiKeyOption();
    private static readonly Option<string?> s_searchOption = TelemetryCommandHelpers.CreateSearchOption();

    public TelemetryTracesCommand(
        AppHostConnectionResolver connectionResolver,
        IHttpClientFactory httpClientFactory,
        ResourceColorMap resourceColorMap,
        TimeProvider timeProvider,
        ILogger<TelemetryTracesCommand> logger,
        CommonCommandServices services)
        : base("traces", TelemetryCommandStrings.TracesDescription, services)
    {
        _httpClientFactory = httpClientFactory;
        _resourceColorMap = resourceColorMap;
        _timeProvider = timeProvider;
        _logger = logger;
        _connectionResolver = connectionResolver;

        Arguments.Add(s_resourceArgument);
        Options.Add(s_appHostOption);
        Options.Add(s_formatOption);
        Options.Add(s_limitOption);
        Options.Add(s_traceIdOption);
        Options.Add(s_hasErrorOption);
        Options.Add(s_dashboardUrlOption);
        Options.Add(s_apiKeyOption);
        Options.Add(s_searchOption);
    }

    protected override async Task<CommandResult> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        using var activity = Telemetry.StartDiagnosticActivity(Name);

        var resourceName = parseResult.GetValue(s_resourceArgument);
        var passedAppHostProjectFile = parseResult.GetValue(s_appHostOption);
        var format = parseResult.GetValue(s_formatOption);
        var limit = parseResult.GetValue(s_limitOption);
        var traceId = parseResult.GetValue(s_traceIdOption);
        var hasError = parseResult.GetValue(s_hasErrorOption);
        var dashboardUrl = parseResult.GetValue(s_dashboardUrlOption);
        var apiKey = parseResult.GetValue(s_apiKeyOption);
        var search = parseResult.GetValue(s_searchOption);

        // Validate --limit value
        if (limit.HasValue && limit.Value < 1)
        {
            return CommandResult.Failure(CliExitCodes.InvalidCommand, TelemetryCommandStrings.LimitMustBePositive);
        }

        await using var dashboardApi = await TelemetryCommandHelpers.GetDashboardApiAsync(
            _connectionResolver, InteractionService, _httpClientFactory, _logger, passedAppHostProjectFile, dashboardUrl, apiKey, requireDashboard: true, cancellationToken);

        if (!dashboardApi.Success)
        {
            return CommandResult.FromExitCode(dashboardApi.ExitCode);
        }

        try
        {
            if (!string.IsNullOrEmpty(traceId))
            {
                return CommandResult.FromExitCode(await FetchSingleTraceAsync(dashboardApi.BaseUrl!, dashboardApi.ApiToken!, traceId, format, dashboardApi.DashboardUrl!, cancellationToken));
            }
            else
            {
                return CommandResult.FromExitCode(await FetchTracesAsync(dashboardApi.BaseUrl!, dashboardApi.ApiToken!, resourceName, hasError, limit, format, dashboardApi.DashboardUrl!, search, cancellationToken));
            }
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError("Failed to fetch traces from Dashboard API");
            var errorInfo = await TelemetryCommandHelpers.FormatTelemetryErrorAsync(ex, dashboardApi.BaseUrl!, dashboardUrl is not null, _httpClientFactory, _logger, cancellationToken);
            TelemetryCommandHelpers.DisplayTelemetryError(InteractionService, errorInfo);
            return CommandResult.Failure(CliExitCodes.DashboardFailure);
        }
    }

    private async Task<int> FetchSingleTraceAsync(
        string baseUrl,
        string apiToken,
        string traceId,
        OutputFormat format,
        string dashboardUrl,
        CancellationToken cancellationToken)
    {
        using var client = TelemetryCommandHelpers.CreateApiClient(_httpClientFactory, apiToken);

        // Fetch resources for name resolution
        var resources = await TelemetryCommandHelpers.GetAllResourcesAsync(client, baseUrl, cancellationToken).ConfigureAwait(false);
        var allOtlpResources = TelemetryCommandHelpers.ToOtlpResources(resources);

        // Pre-resolve colors so assignment is deterministic regardless of data order
        TelemetryCommandHelpers.ResolveResourceColors(_resourceColorMap, allOtlpResources);

        var url = DashboardUrls.TelemetryTraceDetailApiUrl(baseUrl, traceId);

        _logger.LogDebug(
            "Fetching trace {TraceId} from {Url}",
            traceId,
            McpToolHelpers.SanitizeDashboardRequestUrl(url));

        var response = await client.GetAsync(url, cancellationToken);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            InteractionService.DisplayError(string.Format(CultureInfo.CurrentCulture, TelemetryCommandStrings.TraceNotFound, traceId));
            return CliExitCodes.InvalidCommand;
        }

        TelemetryCommandHelpers.EnsureTelemetryApiResponse(response);

        var json = await response.Content.ReadAsStringAsync(cancellationToken);

        if (format == OutputFormat.Json)
        {
            var apiResponse = JsonSerializer.Deserialize(json, OtlpJsonSerializerContext.Default.TelemetryApiResponse);
            var resourceSpans = apiResponse?.Data?.ResourceSpans;
            var traces = SharedAIHelpers.GetTracesFromOtlpData(resourceSpans);
            var trace = traces.FirstOrDefault();
            if (trace is null)
            {
                // Shouldn't happen since API would return 404 if trace not found.
                InteractionService.DisplayRawText("[]", ConsoleOutput.Standard);
            }
            else
            {
                Func<IOtlpResource, string> getResourceName = s => OtlpHelpers.GetResourceName(s, allOtlpResources);
                InteractionService.DisplayRawText(SharedAIHelpers.SerializeTraceToJson(trace, getResourceName, dashboardUrl), ConsoleOutput.Standard);
            }
        }
        else
        {
            DisplayTraceDetails(json, traceId, allOtlpResources, dashboardUrl);
        }

        return CliExitCodes.Success;
    }

    private async Task<int> FetchTracesAsync(
        string baseUrl,
        string apiToken,
        string? resource,
        bool? hasError,
        int? limit,
        OutputFormat format,
        string dashboardUrl,
        string? search,
        CancellationToken cancellationToken)
    {
        using var client = TelemetryCommandHelpers.CreateApiClient(_httpClientFactory, apiToken);

        // Resolve resource name to specific instances (handles replicas)
        var resources = await TelemetryCommandHelpers.GetAllResourcesAsync(client, baseUrl, cancellationToken).ConfigureAwait(false);

        // If a resource was specified but not found, return error
        if (!TelemetryCommandHelpers.TryResolveResourceNames(resource, resources, out var resolvedResources))
        {
            InteractionService.DisplayError($"Resource '{resource}' not found.");
            return CliExitCodes.InvalidCommand;
        }

        var allOtlpResources = TelemetryCommandHelpers.ToOtlpResources(resources);

        // Pre-resolve colors so assignment is deterministic regardless of data order
        TelemetryCommandHelpers.ResolveResourceColors(_resourceColorMap, allOtlpResources);

        var url = DashboardUrls.TelemetryTracesApiUrl(baseUrl, resolvedResources, hasError: hasError, limit: limit, search: search);

        _logger.LogDebug(
            "Fetching traces from {Url}",
            McpToolHelpers.SanitizeDashboardRequestUrl(url));

        var response = await client.GetAsync(url, cancellationToken);
        TelemetryCommandHelpers.EnsureTelemetryApiResponse(response);

        var json = await response.Content.ReadAsStringAsync(cancellationToken);

        if (format == OutputFormat.Json)
        {
            var apiResponse = JsonSerializer.Deserialize(json, OtlpJsonSerializerContext.Default.TelemetryApiResponse);
            var resourceSpans = apiResponse?.Data?.ResourceSpans;
            Func<IOtlpResource, string> getResourceName = s => OtlpHelpers.GetResourceName(s, allOtlpResources);
            InteractionService.DisplayRawText(SharedAIHelpers.SerializeTracesToJson(resourceSpans, getResourceName, dashboardUrl), ConsoleOutput.Standard);
        }
        else
        {
            DisplayTracesTable(json, allOtlpResources, dashboardUrl);
        }

        return CliExitCodes.Success;
    }

    private void DisplayTracesTable(string json, IReadOnlyList<IOtlpResource> allResources, string dashboardUrl)
    {
        var response = JsonSerializer.Deserialize(json, OtlpJsonSerializerContext.Default.TelemetryApiResponse);
        var resourceSpans = response?.Data?.ResourceSpans;

        if (resourceSpans is null or { Length: 0 })
        {
            TelemetryCommandHelpers.DisplayNoData(InteractionService, "traces");
            return;
        }

        var rowLinkDashboardUrl = TelemetryCommandHelpers.GetRowLinkDashboardUrl(HostEnvironment, dashboardUrl);

        var table = HostEnvironment.CreateTable();
        table.AddBoldColumn(TelemetryCommandStrings.HeaderTimestamp);
        table.AddBoldColumn(TelemetryCommandStrings.HeaderName);
        table.AddBoldColumn(TelemetryCommandStrings.HeaderSpans);
        table.AddBoldColumn(TelemetryCommandStrings.HeaderDuration);
        table.AddBoldColumn(TelemetryCommandStrings.HeaderStatus);

        // Group by traceId to show trace summary
        var traceInfos = new Dictionary<string, (string Resource, string FirstSpanName, string TraceId, ulong? StartTimeNano, TimeSpan Duration, int SpanCount, bool HasError)>();

        foreach (var resourceSpan in resourceSpans)
        {
            var resourceName = TelemetryCommandHelpers.ResolveResourceName(resourceSpan.Resource, allResources);

            foreach (var scopeSpan in resourceSpan.ScopeSpans ?? [])
            {
                foreach (var span in scopeSpan.Spans ?? [])
                {
                    var traceIdValue = span.TraceId ?? "";

                    if (string.IsNullOrEmpty(traceIdValue))
                    {
                        continue;
                    }

                    var duration = OtlpHelpers.CalculateDuration(span.StartTimeUnixNano, span.EndTimeUnixNano);
                    var hasError = span.Status?.Code == 2; // ERROR status

                    if (traceInfos.TryGetValue(traceIdValue, out var info))
                    {
                        var maxDuration = info.Duration > duration ? info.Duration : duration;
                        // Track earliest start time across all spans in the trace
                        var earliestStart = info.StartTimeNano.HasValue && span.StartTimeUnixNano.HasValue
                            ? (info.StartTimeNano.Value < span.StartTimeUnixNano.Value ? info.StartTimeNano : span.StartTimeUnixNano)
                            : info.StartTimeNano ?? span.StartTimeUnixNano;
                        traceInfos[traceIdValue] = (info.Resource, info.FirstSpanName, info.TraceId, earliestStart, maxDuration, info.SpanCount + 1, info.HasError || hasError);
                    }
                    else
                    {
                        traceInfos[traceIdValue] = (resourceName, span.Name ?? "", traceIdValue, span.StartTimeUnixNano, duration, 1, hasError);
                    }
                }
            }
        }

        foreach (var (_, info) in traceInfos.OrderBy(x => x.Value.StartTimeNano ?? 0))
        {
            var statusText = info.HasError ? "[red]ERR[/]" : "[green]OK[/]";
            var durationStr = TelemetryCommandHelpers.FormatDuration(info.Duration);
            var resourceColor = _resourceColorMap.GetColor(info.Resource);
            var timestamp = info.StartTimeNano.HasValue
                ? FormatHelpers.FormatConsoleTime(_timeProvider, OtlpHelpers.UnixNanoSecondsToDateTime(info.StartTimeNano.Value))
                : "";
            var shortTraceId = OtlpHelpers.ToShortenedId(info.TraceId);
            var traceLink = TelemetryCommandHelpers.FormatTraceLink(InteractionService, rowLinkDashboardUrl, info.TraceId, shortTraceId);
            var nameColumn = $"[{resourceColor}]{info.Resource.EscapeMarkup()}[/]: {info.FirstSpanName.EscapeMarkup()} [grey]{traceLink}[/]";
            table.AddRow(timestamp, nameColumn, info.SpanCount.ToString(CultureInfo.InvariantCulture), durationStr, statusText);
        }

        InteractionService.DisplayRenderable(table);
        InteractionService.DisplayMarkupLine($"[grey]Showing {traceInfos.Count} of {response?.TotalCount ?? traceInfos.Count} traces[/]");
    }

    private void DisplayTraceDetails(string json, string traceId, IReadOnlyList<IOtlpResource> allResources, string dashboardUrl)
    {
        var response = JsonSerializer.Deserialize(json, OtlpJsonSerializerContext.Default.TelemetryApiResponse);
        var resourceSpans = response?.Data?.ResourceSpans;

        // Collect all spans with their metadata
        var spans = new List<SpanInfo>();

        foreach (var resourceSpan in resourceSpans ?? [])
        {
            var resourceName = TelemetryCommandHelpers.ResolveResourceName(resourceSpan.Resource, allResources);

            foreach (var scopeSpan in resourceSpan.ScopeSpans ?? [])
            {
                foreach (var span in scopeSpan.Spans ?? [])
                {
                    var spanId = span.SpanId ?? "";
                    var parentSpanId = span.ParentSpanId;
                    var name = span.Name ?? "";
                    var duration = OtlpHelpers.CalculateDuration(span.StartTimeUnixNano, span.EndTimeUnixNano);
                    var startNano = (long)(span.StartTimeUnixNano ?? 0);
                    var hasError = span.Status?.Code == 2; // ERROR status
                    var dbQueryText = GetDbQueryText(span);

                    spans.Add(new SpanInfo(spanId, parentSpanId, resourceName, name, duration, startNano, hasError, dbQueryText));
                }
            }
        }

        var shortTraceId = OtlpHelpers.ToShortenedId(traceId);
        var traceLink = TelemetryCommandHelpers.FormatTraceLink(InteractionService, dashboardUrl, traceId, shortTraceId);

        if (spans.Count == 0)
        {
            InteractionService.DisplayMarkupLine($"[bold]Trace:[/] {traceLink}");
            InteractionService.DisplayMarkupLine("[dim]No spans found[/]");
            return;
        }

        // Calculate total duration from root spans
        var rootSpans = spans.Where(s => string.IsNullOrEmpty(s.ParentSpanId)).ToList();
        var totalDuration = rootSpans.Count > 0 ? rootSpans.Max(s => s.Duration) : spans.Max(s => s.Duration);

        // Header
        InteractionService.DisplayMarkupLine($"[bold]Trace:[/] {traceLink}");
        InteractionService.DisplayMarkupLine($"[bold]Duration:[/] {TelemetryCommandHelpers.FormatDuration(totalDuration)}  [bold]Spans:[/] {spans.Count}");
        InteractionService.DisplayEmptyLine();

        // Build tree and display. The trace link in the header is shown once, but each span row
        // only links to the dashboard when the output is an interactive terminal.
        DisplaySpanTree(spans, TelemetryCommandHelpers.GetRowLinkDashboardUrl(HostEnvironment, dashboardUrl), traceId);
    }

    private void DisplaySpanTree(List<SpanInfo> spans, string? dashboardUrl, string traceId)
    {
        // Build a lookup of children by parent ID
        var childrenByParent = spans
            .Where(s => !string.IsNullOrEmpty(s.ParentSpanId))
            .GroupBy(s => s.ParentSpanId!)
            .ToDictionary(g => g.Key, g => g.OrderBy(s => s.StartNano).ToList());

        // Find root spans (no parent or parent not in this trace)
        var spanIds = spans.Select(s => s.SpanId).ToHashSet();
        var rootSpans = spans
            .Where(s => string.IsNullOrEmpty(s.ParentSpanId) || !spanIds.Contains(s.ParentSpanId!))
            .OrderBy(s => s.StartNano)
            .ToList();

        // Track which resources we've seen to show resource transitions
        string? lastResource = null;

        foreach (var root in rootSpans)
        {
            DisplaySpanNode(root, childrenByParent, "", true, dashboardUrl, traceId, ref lastResource);
        }
    }

    private void DisplaySpanNode(
        SpanInfo span,
        Dictionary<string, List<SpanInfo>> childrenByParent,
        string indent,
        bool isLast,
        string? dashboardUrl,
        string traceId,
        ref string? lastResource)
    {
        // Show resource name when it changes (indicates cross-service call)
        if (span.ResourceName != lastResource)
        {
            if (lastResource != null)
            {
                InteractionService.DisplayEmptyLine(); // Blank line between resources
            }
            var resourceColor = _resourceColorMap.GetColor(span.ResourceName);
            InteractionService.DisplayMarkupLine($"{indent}[bold {resourceColor}]{span.ResourceName.EscapeMarkup()}[/]");
            lastResource = span.ResourceName;
        }

        // Build the connector
        var connector = isLast ? "└─" : "├─";
        var childIndent = indent + (isLast ? "   " : "│  ");

        // Format span line with spanId
        var statusColor = span.HasError ? "red" : "green";
        var statusText = span.HasError ? "ERR" : "OK";
        var durationStr = TelemetryCommandHelpers.FormatDuration(span.Duration).PadLeft(8);
        var shortenedSpanId = OtlpHelpers.ToShortenedId(span.SpanId);
        var spanIdLink = TelemetryCommandHelpers.FormatTraceLink(InteractionService, dashboardUrl, traceId, shortenedSpanId, spanId: span.SpanId);
        // Truncate before escaping markup so the cut cannot split an escaped "[[" or "]]" pair,
        // which Spectre would reject as a malformed tag.
        var displayName = FormatHelpers.TruncateText(span.Name, MaxSpanNameLength, "...").EscapeMarkup();

        var dbQueryText = span.DbQueryText is { } queryText
            ? $" [dim]{FormatDbQueryText(queryText).EscapeMarkup()}[/]"
            : "";

        InteractionService.DisplayMarkupLine($"{indent}{connector} [dim]{spanIdLink}[/] {displayName} [{statusColor}]{statusText}[/] [dim]{durationStr}[/]{dbQueryText}");

        // Render children
        if (childrenByParent.TryGetValue(span.SpanId, out var children))
        {
            for (var i = 0; i < children.Count; i++)
            {
                DisplaySpanNode(children[i], childrenByParent, childIndent, i == children.Count - 1, dashboardUrl, traceId, ref lastResource);
            }
        }
    }

    private static string? GetDbQueryText(OtlpSpanJson span)
    {
        // "db.query.text" is the current OpenTelemetry semantic convention; "db.statement" is the
        // deprecated name still emitted by older instrumentation libraries.
        // https://opentelemetry.io/docs/specs/semconv/database/database-spans/
        return SharedAIHelpers.GetAttributeStringValue(span.Attributes, "db.query.text")
            ?? SharedAIHelpers.GetAttributeStringValue(span.Attributes, "db.statement");
    }

    /// <summary>
    /// Renders a database query on one line by escaping line breaks and tabs, and truncates it,
    /// so the span tree stays one row per span without changing the query's spaces.
    /// </summary>
    private static string FormatDbQueryText(string queryText)
    {
        // Queries are commonly multi-line and indented, and literals can contain significant spaces, e.g.
        //   SELECT o."Id", o."Total"
        //       FROM "Orders" AS o
        //       WHERE o."Name" = 'a  b'
        // is rendered as
        //   SELECT o."Id", o."Total"\n    FROM "Orders" AS o\n    WHERE o."Name" = 'a  b'
        // Spaces are kept exactly so quoted values are not altered; '\r', '\n' and '\t' become
        // visible two-character escapes. Leading and trailing whitespace of the whole query is trimmed
        // because it carries no meaning and would otherwise show up as escape noise.
        var trimmed = queryText.AsSpan().Trim();

        // Lengths are measured in UTF-16 code units, but the query is walked one Unicode scalar
        // value at a time so a non-BMP character (e.g. an emoji) is kept or dropped as a whole
        // surrogate pair.
        var escapedLength = 0;
        foreach (var rune in trimmed.EnumerateRunes())
        {
            escapedLength += GetEscape(rune)?.Length ?? rune.Utf16SequenceLength;
        }

        var maxLength = escapedLength > MaxDbQueryTextLength ? MaxDbQueryTextLength - 3 : escapedLength;
        var builder = new StringBuilder(MaxDbQueryTextLength);
        var position = 0;
        foreach (var rune in trimmed.EnumerateRunes())
        {
            var escape = GetEscape(rune);
            var length = escape?.Length ?? rune.Utf16SequenceLength;

            // Stop before a whole scalar value or escape that does not fit, so truncation never
            // leaves a dangling backslash from a split escape sequence or an unpaired surrogate.
            if (builder.Length + length > maxLength)
            {
                break;
            }

            if (escape is null)
            {
                builder.Append(trimmed.Slice(position, rune.Utf16SequenceLength));
            }
            else
            {
                builder.Append(escape);
            }

            position += rune.Utf16SequenceLength;
        }

        if (escapedLength > MaxDbQueryTextLength)
        {
            builder.Append("...");
        }

        return builder.ToString();

        static string? GetEscape(Rune rune) => rune.Value switch
        {
            '\r' => "\\r",
            '\n' => "\\n",
            '\t' => "\\t",
            _ => null
        };
    }

    private sealed record SpanInfo(
        string SpanId,
        string? ParentSpanId,
        string ResourceName,
        string Name,
        TimeSpan Duration,
        long StartNano,
        bool HasError,
        string? DbQueryText);
}
