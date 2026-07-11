import type { LogRecordSummary, SpanSummary, TelemetryAttribute } from "./types";

interface OtlpAnyValue {
  stringValue?: string;
  boolValue?: boolean;
  intValue?: string;
  doubleValue?: number;
  bytesValue?: string;
  arrayValue?: { values?: OtlpAnyValue[] };
  kvlistValue?: { values?: OtlpKeyValue[] };
}

interface OtlpKeyValue {
  key?: string;
  value?: OtlpAnyValue;
}

interface OtlpResource {
  attributes?: OtlpKeyValue[];
  droppedAttributesCount?: number;
}

interface OtlpLogRecord {
  timeUnixNano?: string;
  observedTimeUnixNano?: string;
  severityNumber?: number;
  severityText?: string;
  body?: OtlpAnyValue;
  attributes?: OtlpKeyValue[];
  droppedAttributesCount?: number;
  flags?: number;
  traceId?: string;
  spanId?: string;
  eventName?: string;
}

interface OtlpInstrumentationScope {
  name?: string;
  version?: string;
  attributes?: OtlpKeyValue[];
  droppedAttributesCount?: number;
}

interface OtlpScopeLogs {
  scope?: OtlpInstrumentationScope;
  logRecords?: OtlpLogRecord[];
}

interface OtlpResourceLogs {
  resource?: OtlpResource;
  scopeLogs?: OtlpScopeLogs[];
}

interface OtlpSpanStatus {
  code?: number;
  message?: string;
}

interface OtlpSpan {
  traceId?: string;
  spanId?: string;
  parentSpanId?: string;
  name?: string;
  kind?: number;
  startTimeUnixNano?: string;
  endTimeUnixNano?: string;
  status?: OtlpSpanStatus;
}

interface OtlpScopeSpans {
  scope?: OtlpInstrumentationScope;
  spans?: OtlpSpan[];
}

interface OtlpResourceSpans {
  resource?: OtlpResource;
  scopeSpans?: OtlpScopeSpans[];
}

export interface OtlpTelemetryData {
  resourceLogs?: OtlpResourceLogs[];
  resourceSpans?: OtlpResourceSpans[];
}

export interface TelemetryApiResponse {
  data?: OtlpTelemetryData;
  totalCount: number;
  returnedCount: number;
}

export interface OtlpLogRecordSummary extends LogRecordSummary {
  recordKey: string;
}

export interface OtlpSpanSummary extends SpanSummary {
  recordKey: string;
}

function anyValueToText(value: OtlpAnyValue | undefined): string {
  if (value === undefined) {
    return "";
  }
  if (value.stringValue !== undefined) {
    return value.stringValue;
  }
  if (value.boolValue !== undefined) {
    return String(value.boolValue);
  }
  if (value.intValue !== undefined) {
    return value.intValue;
  }
  if (value.doubleValue !== undefined) {
    return String(value.doubleValue);
  }
  if (value.bytesValue !== undefined) {
    return value.bytesValue;
  }
  if (value.arrayValue !== undefined) {
    return JSON.stringify((value.arrayValue.values ?? []).map(anyValueToJson));
  }
  if (value.kvlistValue !== undefined) {
    return JSON.stringify(Object.fromEntries(
      (value.kvlistValue.values ?? []).map((item) => [item.key ?? "", anyValueToJson(item.value)]),
    ));
  }
  return "";
}

function anyValueToJson(value: OtlpAnyValue | undefined): unknown {
  if (value?.arrayValue !== undefined) {
    return (value.arrayValue.values ?? []).map(anyValueToJson);
  }
  if (value?.kvlistValue !== undefined) {
    return Object.fromEntries(
      (value.kvlistValue.values ?? []).map((item) => [item.key ?? "", anyValueToJson(item.value)]),
    );
  }
  return anyValueToText(value);
}

function resourceAttribute(resource: OtlpResource | undefined, key: string): string | null {
  const value = resource?.attributes?.find((attribute) => attribute.key === key)?.value;
  return value === undefined ? null : anyValueToText(value);
}

function recordAttribute(record: OtlpLogRecord, key: string): string | null {
  const value = record.attributes?.find((attribute) => attribute.key === key)?.value;
  return value === undefined ? null : anyValueToText(value);
}

function toTelemetryAttributes(
  attributes: OtlpKeyValue[] | undefined,
  excludedKeys: ReadonlySet<string> = new Set<string>(),
): TelemetryAttribute[] {
  return (attributes ?? []).flatMap((attribute) => {
    if (!attribute.key || excludedKeys.has(attribute.key)) {
      return [];
    }
    return [{ key: attribute.key, value: anyValueToText(attribute.value) }];
  });
}

function severityLabel(number: number, text: string | undefined): string | null {
  if (number >= 1 && number <= 4) {
    return "Trace";
  }
  if (number >= 5 && number <= 8) {
    return "Debug";
  }
  if (number >= 9 && number <= 12) {
    return "Information";
  }
  if (number >= 13 && number <= 16) {
    return "Warning";
  }
  if (number >= 17 && number <= 20) {
    return "Error";
  }
  if (number >= 21 && number <= 24) {
    return "Critical";
  }
  return text?.trim() || null;
}

const hiddenLogAttributeKeys = new Set([
  "{OriginalFormat}",
  "ParentId",
  "SpanId",
  "TraceId",
  "aspire.log_id",
  "event.name",
  "logrecord.event.name",
]);

export function getLogRecordSummaries(data: OtlpTelemetryData | undefined): OtlpLogRecordSummary[] {
  const summaries: OtlpLogRecordSummary[] = [];

  for (const resourceLogs of data?.resourceLogs ?? []) {
    const resourceName = resourceAttribute(resourceLogs.resource, "service.name");
    for (const scopeLogs of resourceLogs.scopeLogs ?? []) {
      for (const record of scopeLogs.logRecords ?? []) {
        const body = anyValueToText(record.body);
        const logId = record.attributes?.find((attribute) => attribute.key === "aspire.log_id")?.value;
        const observedTimeUnixNano = record.observedTimeUnixNano ?? "0";
        const timeUnixNano = record.timeUnixNano && record.timeUnixNano !== "0"
          ? record.timeUnixNano
          : observedTimeUnixNano;
        const eventName = record.eventName?.trim()
          || recordAttribute(record, "event.name")
          || recordAttribute(record, "logrecord.event.name");
        const severityNumber = record.severityNumber ?? 0;
        summaries.push({
          recordKey: logId === undefined
            ? [resourceName, timeUnixNano, record.traceId, record.spanId, severityNumber, body].join("\u0000")
            : `${resourceName}\u0000${anyValueToText(logId)}`,
          timeUnixNano,
          observedTimeUnixNano,
          severity: severityLabel(severityNumber, record.severityText),
          severityNumber,
          body,
          resourceName,
          traceId: record.traceId ?? null,
          spanId: record.spanId ?? null,
          parentId: recordAttribute(record, "ParentId"),
          eventName: eventName || null,
          originalFormat: recordAttribute(record, "{OriginalFormat}"),
          scopeName: scopeLogs.scope?.name?.trim() || "unknown",
          scopeVersion: scopeLogs.scope?.version?.trim() || null,
          attributes: toTelemetryAttributes(record.attributes, hiddenLogAttributeKeys),
          scopeAttributes: toTelemetryAttributes(scopeLogs.scope?.attributes),
          resourceAttributes: toTelemetryAttributes(resourceLogs.resource?.attributes),
          flags: record.flags ?? 0,
          droppedAttributesCount: record.droppedAttributesCount ?? 0,
          scopeDroppedAttributesCount: scopeLogs.scope?.droppedAttributesCount ?? 0,
          resourceDroppedAttributesCount: resourceLogs.resource?.droppedAttributesCount ?? 0,
        });
      }
    }
  }

  return summaries;
}

function spanKind(kind: number | undefined): string {
  return ["Unspecified", "Internal", "Server", "Client", "Producer", "Consumer"][kind ?? 0]
    ?? "Unspecified";
}

function spanStatus(code: number | undefined): string | null {
  return [null, "Ok", "Error"][code ?? 0] ?? null;
}

function spanDuration(startUnixNano: string, endUnixNano: string): string {
  try {
    const duration = BigInt(endUnixNano) - BigInt(startUnixNano);
    return duration > 0n ? duration.toString() : "0";
  } catch {
    return "0";
  }
}

export function getSpanSummaries(data: OtlpTelemetryData | undefined): OtlpSpanSummary[] {
  const summaries: OtlpSpanSummary[] = [];

  for (const resourceSpans of data?.resourceSpans ?? []) {
    const resourceName = resourceAttribute(resourceSpans.resource, "service.name");
    for (const scopeSpans of resourceSpans.scopeSpans ?? []) {
      for (const span of scopeSpans.spans ?? []) {
        if (!span.traceId || !span.spanId) {
          continue;
        }

        const startUnixNano = span.startTimeUnixNano ?? "0";
        const endUnixNano = span.endTimeUnixNano ?? startUnixNano;
        summaries.push({
          recordKey: `${span.traceId}\u0000${span.spanId}`,
          traceId: span.traceId,
          spanId: span.spanId,
          parentSpanId: span.parentSpanId?.trim() || null,
          name: span.name?.trim() || "unknown span",
          kind: spanKind(span.kind),
          resourceName,
          startUnixNano,
          durationNanos: spanDuration(startUnixNano, endUnixNano),
          statusCode: spanStatus(span.status?.code),
        });
      }
    }
  }

  return summaries;
}
