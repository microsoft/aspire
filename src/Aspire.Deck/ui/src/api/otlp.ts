import type { LogRecordSummary } from "./types";

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
}

interface OtlpLogRecord {
  timeUnixNano?: string;
  severityNumber?: number;
  severityText?: string;
  body?: OtlpAnyValue;
  attributes?: OtlpKeyValue[];
  traceId?: string;
  spanId?: string;
}

interface OtlpScopeLogs {
  logRecords?: OtlpLogRecord[];
}

interface OtlpResourceLogs {
  resource?: OtlpResource;
  scopeLogs?: OtlpScopeLogs[];
}

export interface OtlpTelemetryData {
  resourceLogs?: OtlpResourceLogs[];
}

export interface TelemetryApiResponse {
  data?: OtlpTelemetryData;
  totalCount: number;
  returnedCount: number;
}

export interface OtlpLogRecordSummary extends LogRecordSummary {
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

export function getLogRecordSummaries(data: OtlpTelemetryData | undefined): OtlpLogRecordSummary[] {
  const summaries: OtlpLogRecordSummary[] = [];

  for (const resourceLogs of data?.resourceLogs ?? []) {
    const resourceName = resourceAttribute(resourceLogs.resource, "service.name");
    for (const scopeLogs of resourceLogs.scopeLogs ?? []) {
      for (const record of scopeLogs.logRecords ?? []) {
        const body = anyValueToText(record.body);
        const logId = record.attributes?.find((attribute) => attribute.key === "aspire.log_id")?.value;
        summaries.push({
          recordKey: logId === undefined
            ? [resourceName, record.timeUnixNano, record.traceId, record.spanId, record.severityNumber, body].join("\u0000")
            : `${resourceName}\u0000${anyValueToText(logId)}`,
          timeUnixNano: record.timeUnixNano ?? "0",
          severity: record.severityText ?? null,
          severityNumber: record.severityNumber ?? 0,
          body,
          resourceName,
          traceId: record.traceId ?? null,
          spanId: record.spanId ?? null,
        });
      }
    }
  }

  return summaries;
}
