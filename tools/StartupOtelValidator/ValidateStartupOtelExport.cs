// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using System.Text.Json;

var exportDirectory = GetRequiredEnvironmentVariable("EXPORT_DIR");
var spanSummaryPath = GetRequiredEnvironmentVariable("SPAN_SUMMARY_PATH");
var runRoot = GetRequiredEnvironmentVariable("RUN_ROOT");
var requireDcpSpans = string.Equals(Environment.GetEnvironmentVariable("REQUIRE_DCP_SPANS"), "true", StringComparison.OrdinalIgnoreCase);

var spans = ReadExportedSpans(exportDirectory);
WriteSpanSummary(spanSummaryPath, spans);

var startupGroups = spans
    .Where(span => !string.IsNullOrEmpty(span.StartupOperationId))
    .GroupBy(span => span.StartupOperationId!);

if (!startupGroups.Any())
{
    Fail($"No exported spans contained aspire.startup.operation_id. See {spanSummaryPath}");
}

string? validStartupOperationId = null;
string? validTraceId = null;
List<ExportedSpan>? validTraceSpans = null;

foreach (var startupGroup in startupGroups)
{
    var traceGroups = startupGroup
        .Where(span => !string.IsNullOrEmpty(span.TraceId))
        .GroupBy(span => span.TraceId!);

    foreach (var traceGroup in traceGroups)
    {
        var traceSpans = traceGroup.ToList();
        var hasStartCommandSpan = traceSpans.Any(span =>
            span.Scope == "Aspire.Cli.Profiling" &&
            span.Name == "aspire/cli/start_apphost.spawn_child");
        var hasChildDiagnosticSpan = traceSpans.Any(span =>
            span.Scope == "Aspire.Cli.Profiling" &&
            Contains(span.Name,
                "aspire/cli/apphost.ensure_dev_certificates",
                "aspire/cli/backchannel.connect",
                "aspire/cli/backchannel.get_dashboard_urls",
                "aspire/cli/dotnet.build",
                "aspire/cli/run"));
        var hasHostingDcpSpan = traceSpans.Any(span =>
            span.Scope == "Aspire.Hosting.Profiling" &&
            Contains(span.Name,
                "aspire.hosting.dcp.run_application",
                "aspire.hosting.dcp.create_rendered_resources",
                "aspire.hosting.dcp.allocate_service_addresses"));
        var hasResourceCreateSpan = traceSpans.Any(span =>
            span.Scope == "Aspire.Hosting.Profiling" &&
            span.Name == "aspire.hosting.resource.create");
        var hasResourceWaitSpan = traceSpans.Any(span =>
            span.Scope == "Aspire.Hosting.Profiling" &&
            Contains(span.Name,
                "aspire.hosting.resource.before_start_wait",
                "aspire.hosting.resource.wait_for_dependencies",
                "aspire.hosting.resource.wait_for_dependency"));
        var hasDcpProcessSpan = traceSpans.Any(span =>
            span.Scope == "dcp.startup" &&
            Contains(span.Name,
                "dcp.command",
                "dcp.start_apiserver",
                "dcp.start_apiserver.fork",
                "dcp.run",
                "dcp.apiserver.start",
                "dcp.hosted_services.start",
                "dcp.controllers.run",
                "dcp.controllers.create_manager"));
        var hasDcpResourceSpan = traceSpans.Any(span =>
            span.Scope == "dcp.startup" &&
            Contains(span.Name,
                "dcp.controller.reconcile",
                "dcp.executable.manage",
                "dcp.service.ensure_effective_address",
                "dcp.container.manage"));
        var hasDcpResourceObservedSpan = traceSpans.Any(span =>
            span.Scope == "Aspire.Hosting.Profiling" &&
            span.Name == "aspire.hosting.dcp.resource_observed");
        var hasDcpCreateObjectLink = traceSpans.Any(span =>
            span.Scope == "dcp.startup" &&
            !string.IsNullOrEmpty(span.DcpCreateObjectId) &&
            !string.IsNullOrEmpty(span.DcpCreateObjectSpanId) &&
            span.LinkSpanIds.Contains(span.DcpCreateObjectSpanId, StringComparer.Ordinal));
        var hasResourceWaitEvents = traceSpans.Any(span =>
            span.Scope == "Aspire.Hosting.Profiling" &&
            span.EventNames.Contains("aspire.resource.wait.observed", StringComparer.Ordinal) &&
            span.EventNames.Contains("aspire.resource.wait.completed", StringComparer.Ordinal));
        var hasRequiredDcpSpans = !requireDcpSpans || (hasDcpProcessSpan && hasDcpResourceSpan);

        if (hasStartCommandSpan &&
            hasChildDiagnosticSpan &&
            hasHostingDcpSpan &&
            hasResourceCreateSpan &&
            hasResourceWaitSpan &&
            hasDcpResourceObservedSpan &&
            hasDcpCreateObjectLink &&
            hasResourceWaitEvents &&
            hasRequiredDcpSpans)
        {
            validStartupOperationId = startupGroup.Key;
            validTraceId = traceGroup.Key;
            validTraceSpans = traceSpans;
            break;
        }
    }

    if (validStartupOperationId is not null)
    {
        break;
    }
}

if (validStartupOperationId is null || validTraceId is null || validTraceSpans is null)
{
    var dcpRequirement = requireDcpSpans ? ", DCP process, and DCP resource/controller" : string.Empty;
    Fail($"No startup operation contained correlated CLI, Hosting DCP resource creation, DCP resource observation, resource wait events, Hosting-to-DCP links{dcpRequirement} spans in one trace. See {spanSummaryPath}");
}

var startJsonPath = GetRequiredEnvironmentVariable("START_JSON_PATH");
var startedDashboardUrl = ReadStartedDashboardUrl(startJsonPath);
var summary = new ValidationSummary(
    RunRoot: runRoot,
    TargetAspirePath: GetRequiredEnvironmentVariable("TARGET_ASPIRE_PATH"),
    ProfilerAspirePath: GetRequiredEnvironmentVariable("PROFILER_ASPIRE_PATH"),
    LayoutPath: GetOptionalEnvironmentVariable("LAYOUT_PATH"),
    DcpPath: GetOptionalEnvironmentVariable("DCP_PATH"),
    PostStartDelaySeconds: int.TryParse(Environment.GetEnvironmentVariable("POST_START_DELAY_SECONDS"), out var postStartDelaySeconds) ? postStartDelaySeconds : 0,
    RequireDcpSpans: requireDcpSpans,
    DashboardUrl: GetRequiredEnvironmentVariable("DASHBOARD_URL"),
    OtlpGrpcUrl: GetRequiredEnvironmentVariable("OTLP_GRPC_URL"),
    OtlpHttpUrl: GetRequiredEnvironmentVariable("OTLP_HTTP_URL"),
    AppHostPath: GetRequiredEnvironmentVariable("APPHOST_PATH"),
    StartedDashboardUrl: startedDashboardUrl,
    ExportZip: GetRequiredEnvironmentVariable("EXPORT_ZIP"),
    DotnetTraceDirectory: GetOptionalEnvironmentVariable("DOTNET_TRACE_DIR"),
    DotnetTraceFiles: GetOptionalPathList(Environment.GetEnvironmentVariable("DOTNET_TRACE_DIR"), ".nettrace"),
    DotnetBinlogDirectory: GetOptionalEnvironmentVariable("DOTNET_BINLOG_DIR"),
    DotnetBinlogFiles: GetOptionalPathList(Environment.GetEnvironmentVariable("DOTNET_BINLOG_DIR"), ".binlog"),
    SpanSummary: spanSummaryPath,
    StartupOperationId: validStartupOperationId!,
    CorrelatedSpanCount: validTraceSpans!.Count,
    TraceId: validTraceId!);

var summaryPath = Path.Combine(runRoot, "summary.json");
WriteSummary(summaryPath, summary);
Console.WriteLine(FormatSummary(summary));

static List<ExportedSpan> ReadExportedSpans(string exportDirectory)
{
    var tracesDirectory = Path.Combine(exportDirectory, "traces");
    if (!Directory.Exists(tracesDirectory))
    {
        return [];
    }

    var spans = new List<ExportedSpan>();
    foreach (var tracePath in Directory.EnumerateFiles(tracesDirectory, "*.json").Order(StringComparer.Ordinal))
    {
        using var traceStream = File.OpenRead(tracePath);
        using var traceDocument = JsonDocument.Parse(traceStream);

        // Dashboard trace export files use the OTLP JSON shape:
        // { resourceSpans: [{ scopeSpans: [{ scope: { name }, spans: [...] }] }] }
        foreach (var resourceSpan in EnumerateArrayProperty(traceDocument.RootElement, "resourceSpans"))
        {
            foreach (var scopeSpan in EnumerateArrayProperty(resourceSpan, "scopeSpans"))
            {
                var scopeName = TryGetProperty(scopeSpan, "scope", out var scope)
                    ? GetStringProperty(scope, "name")
                    : null;

                foreach (var span in EnumerateArrayProperty(scopeSpan, "spans"))
                {
                    spans.Add(new ExportedSpan(
                        File: Path.GetFileName(tracePath),
                        Scope: scopeName,
                        Name: GetStringProperty(span, "name"),
                        TraceId: GetStringProperty(span, "traceId"),
                        SpanId: GetStringProperty(span, "spanId"),
                        ParentSpanId: GetStringProperty(span, "parentSpanId"),
                        StartupOperationId: GetSpanAttributeValue(span, "aspire.startup.operation_id"),
                        CommandName: GetSpanAttributeValue(span, "aspire.cli.command.name"),
                        ProcessId: GetSpanAttributeValue(span, "process.pid"),
                        DcpCreateObjectId: GetSpanAttributeValue(span, "aspire.hosting.dcp.create_object.id"),
                        DcpCreateObjectKind: GetSpanAttributeValue(span, "aspire.hosting.dcp.create_object.kind"),
                        DcpCreateObjectName: GetSpanAttributeValue(span, "aspire.hosting.dcp.create_object.name"),
                        DcpCreateObjectSpanId: GetSpanAttributeValue(span, "aspire.hosting.dcp.create_object.span_id"),
                        LinkSpanIds: ReadLinkSpanIds(span),
                        EventNames: ReadEventNames(span)));
                }
            }
        }
    }

    return spans;
}

static List<string> ReadLinkSpanIds(JsonElement span)
{
    return EnumerateArrayProperty(span, "links")
        .Select(link => GetStringProperty(link, "spanId"))
        .Where(spanId => !string.IsNullOrEmpty(spanId))
        .Select(spanId => spanId!)
        .ToList();
}

static List<string> ReadEventNames(JsonElement span)
{
    return EnumerateArrayProperty(span, "events")
        .Select(@event => GetStringProperty(@event, "name"))
        .Where(name => !string.IsNullOrEmpty(name))
        .Select(name => name!)
        .ToList();
}

static string? GetSpanAttributeValue(JsonElement span, string key)
{
    foreach (var attribute in EnumerateArrayProperty(span, "attributes"))
    {
        if (GetStringProperty(attribute, "key") != key || !TryGetProperty(attribute, "value", out var value))
        {
            continue;
        }

        foreach (var propertyName in new[] { "stringValue", "intValue", "doubleValue", "boolValue" })
        {
            if (!TryGetProperty(value, propertyName, out var propertyValue))
            {
                continue;
            }

            return propertyValue.ValueKind switch
            {
                JsonValueKind.String => propertyValue.GetString(),
                JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => propertyValue.GetRawText(),
                _ => null
            };
        }
    }

    return null;
}

static string? ReadStartedDashboardUrl(string startJsonPath)
{
    using var startJsonStream = File.OpenRead(startJsonPath);
    using var startJsonDocument = JsonDocument.Parse(startJsonStream);

    return GetStringProperty(startJsonDocument.RootElement, "dashboardUrl");
}

static IEnumerable<JsonElement> EnumerateArrayProperty(JsonElement element, string propertyName)
{
    if (!TryGetProperty(element, propertyName, out var property) || property.ValueKind != JsonValueKind.Array)
    {
        yield break;
    }

    foreach (var item in property.EnumerateArray())
    {
        yield return item;
    }
}

static bool TryGetProperty(JsonElement element, string propertyName, out JsonElement property)
{
    if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(propertyName, out property))
    {
        return true;
    }

    property = default;
    return false;
}

static string? GetStringProperty(JsonElement element, string propertyName)
{
    return TryGetProperty(element, propertyName, out var property) && property.ValueKind == JsonValueKind.String
        ? property.GetString()
        : null;
}

static string GetRequiredEnvironmentVariable(string name)
{
    return Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
        ? value
        : throw new InvalidOperationException($"Required environment variable '{name}' is not set.");
}

static string? GetOptionalEnvironmentVariable(string name)
{
    return Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : null;
}

static List<string> GetOptionalPathList(string? directory, string extension)
{
    return !string.IsNullOrEmpty(directory) && Directory.Exists(directory)
        ? Directory.EnumerateFiles(directory, $"*{extension}").Order(StringComparer.Ordinal).ToList()
        : [];
}

static bool Contains(string? value, params string[] candidates)
{
    return value is not null && candidates.Contains(value, StringComparer.Ordinal);
}

static void Fail(string message)
{
    throw new InvalidOperationException(message);
}

static void WriteSpanSummary(string path, IReadOnlyList<ExportedSpan> spans)
{
    using var stream = File.Create(path);
    using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });

    writer.WriteStartArray();
    foreach (var span in spans)
    {
        WriteExportedSpan(writer, span);
    }
    writer.WriteEndArray();
}

static void WriteSummary(string path, ValidationSummary summary)
{
    using var stream = File.Create(path);
    using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });

    WriteValidationSummary(writer, summary);
}

static string FormatSummary(ValidationSummary summary)
{
    using var stream = new MemoryStream();
    using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
    {
        WriteValidationSummary(writer, summary);
    }

    return Encoding.UTF8.GetString(stream.ToArray());
}

static void WriteExportedSpan(Utf8JsonWriter writer, ExportedSpan span)
{
    writer.WriteStartObject();
    WriteString(writer, nameof(ExportedSpan.File), span.File);
    WriteString(writer, nameof(ExportedSpan.Scope), span.Scope);
    WriteString(writer, nameof(ExportedSpan.Name), span.Name);
    WriteString(writer, nameof(ExportedSpan.TraceId), span.TraceId);
    WriteString(writer, nameof(ExportedSpan.SpanId), span.SpanId);
    WriteString(writer, nameof(ExportedSpan.ParentSpanId), span.ParentSpanId);
    WriteString(writer, nameof(ExportedSpan.StartupOperationId), span.StartupOperationId);
    WriteString(writer, nameof(ExportedSpan.CommandName), span.CommandName);
    WriteString(writer, nameof(ExportedSpan.ProcessId), span.ProcessId);
    WriteString(writer, nameof(ExportedSpan.DcpCreateObjectId), span.DcpCreateObjectId);
    WriteString(writer, nameof(ExportedSpan.DcpCreateObjectKind), span.DcpCreateObjectKind);
    WriteString(writer, nameof(ExportedSpan.DcpCreateObjectName), span.DcpCreateObjectName);
    WriteString(writer, nameof(ExportedSpan.DcpCreateObjectSpanId), span.DcpCreateObjectSpanId);
    WriteStringArray(writer, nameof(ExportedSpan.LinkSpanIds), span.LinkSpanIds);
    WriteStringArray(writer, nameof(ExportedSpan.EventNames), span.EventNames);
    writer.WriteEndObject();
}

static void WriteValidationSummary(Utf8JsonWriter writer, ValidationSummary summary)
{
    writer.WriteStartObject();
    writer.WriteString(nameof(ValidationSummary.RunRoot), summary.RunRoot);
    writer.WriteString(nameof(ValidationSummary.TargetAspirePath), summary.TargetAspirePath);
    writer.WriteString(nameof(ValidationSummary.ProfilerAspirePath), summary.ProfilerAspirePath);
    WriteString(writer, nameof(ValidationSummary.LayoutPath), summary.LayoutPath);
    WriteString(writer, nameof(ValidationSummary.DcpPath), summary.DcpPath);
    writer.WriteNumber(nameof(ValidationSummary.PostStartDelaySeconds), summary.PostStartDelaySeconds);
    writer.WriteBoolean(nameof(ValidationSummary.RequireDcpSpans), summary.RequireDcpSpans);
    writer.WriteString(nameof(ValidationSummary.DashboardUrl), summary.DashboardUrl);
    writer.WriteString(nameof(ValidationSummary.OtlpGrpcUrl), summary.OtlpGrpcUrl);
    writer.WriteString(nameof(ValidationSummary.OtlpHttpUrl), summary.OtlpHttpUrl);
    writer.WriteString(nameof(ValidationSummary.AppHostPath), summary.AppHostPath);
    WriteString(writer, nameof(ValidationSummary.StartedDashboardUrl), summary.StartedDashboardUrl);
    writer.WriteString(nameof(ValidationSummary.ExportZip), summary.ExportZip);
    WriteString(writer, nameof(ValidationSummary.DotnetTraceDirectory), summary.DotnetTraceDirectory);
    WriteStringArray(writer, nameof(ValidationSummary.DotnetTraceFiles), summary.DotnetTraceFiles);
    WriteString(writer, nameof(ValidationSummary.DotnetBinlogDirectory), summary.DotnetBinlogDirectory);
    WriteStringArray(writer, nameof(ValidationSummary.DotnetBinlogFiles), summary.DotnetBinlogFiles);
    writer.WriteString(nameof(ValidationSummary.SpanSummary), summary.SpanSummary);
    writer.WriteString(nameof(ValidationSummary.StartupOperationId), summary.StartupOperationId);
    writer.WriteNumber(nameof(ValidationSummary.CorrelatedSpanCount), summary.CorrelatedSpanCount);
    writer.WriteString(nameof(ValidationSummary.TraceId), summary.TraceId);
    writer.WriteEndObject();
}

static void WriteString(Utf8JsonWriter writer, string propertyName, string? value)
{
    if (value is null)
    {
        writer.WriteNull(propertyName);
    }
    else
    {
        writer.WriteString(propertyName, value);
    }
}

static void WriteStringArray(Utf8JsonWriter writer, string propertyName, IReadOnlyList<string> values)
{
    writer.WriteStartArray(propertyName);
    foreach (var value in values)
    {
        writer.WriteStringValue(value);
    }
    writer.WriteEndArray();
}

internal sealed record ExportedSpan(
    string File,
    string? Scope,
    string? Name,
    string? TraceId,
    string? SpanId,
    string? ParentSpanId,
    string? StartupOperationId,
    string? CommandName,
    string? ProcessId,
    string? DcpCreateObjectId,
    string? DcpCreateObjectKind,
    string? DcpCreateObjectName,
    string? DcpCreateObjectSpanId,
    List<string> LinkSpanIds,
    List<string> EventNames);

internal sealed record ValidationSummary(
    string RunRoot,
    string TargetAspirePath,
    string ProfilerAspirePath,
    string? LayoutPath,
    string? DcpPath,
    int PostStartDelaySeconds,
    bool RequireDcpSpans,
    string DashboardUrl,
    string OtlpGrpcUrl,
    string OtlpHttpUrl,
    string AppHostPath,
    string? StartedDashboardUrl,
    string ExportZip,
    string? DotnetTraceDirectory,
    List<string> DotnetTraceFiles,
    string? DotnetBinlogDirectory,
    List<string> DotnetBinlogFiles,
    string SpanSummary,
    string StartupOperationId,
    int CorrelatedSpanCount,
    string TraceId);
