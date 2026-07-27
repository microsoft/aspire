import type { LogRecordSummary, SpanSummary, TelemetryAttribute } from "../api/types";

export type TelemetryFilterCondition = "equals" | "contains" | "gt" | "lt" | "gte" | "lte" | "notEquals" | "notContains";

export interface TelemetryFilter {
  id: string;
  field: string;
  condition: TelemetryFilterCondition;
  value: string;
  enabled: boolean;
}

const conditions = new Set<TelemetryFilterCondition>(["equals", "contains", "gt", "lt", "gte", "lte", "notEquals", "notContains"]);

export function parseTelemetryFilters(value: string | null): TelemetryFilter[] {
  if (!value) return [];
  try {
    const parsed = JSON.parse(value) as unknown;
    if (!Array.isArray(parsed)) return [];
    return parsed.flatMap((item, index) => {
      if (typeof item !== "object" || item === null) return [];
      const candidate = item as Record<string, unknown>;
      if (typeof candidate.field !== "string" || typeof candidate.value !== "string" || !conditions.has(candidate.condition as TelemetryFilterCondition)) return [];
      return [{
        id: typeof candidate.id === "string" ? candidate.id : `restored-${index}`,
        field: candidate.field,
        condition: candidate.condition as TelemetryFilterCondition,
        value: candidate.value,
        enabled: candidate.enabled !== false,
      }];
    });
  } catch {
    return [];
  }
}

export function serializeTelemetryFilters(filters: TelemetryFilter[]): string | undefined {
  return filters.length === 0 ? undefined : JSON.stringify(filters);
}

function attributesToMap(attributes: TelemetryAttribute[]): Record<string, string> {
  return Object.fromEntries(attributes.map((attribute) => [attribute.key, attribute.value]));
}

export function logFilterFields(log: LogRecordSummary): Record<string, string> {
  return {
    Message: log.body,
    Severity: log.severity ?? "",
    Resource: log.resourceName ?? "",
    TraceId: log.traceId ?? "",
    SpanId: log.spanId ?? "",
    EventName: log.eventName ?? "",
    ScopeName: log.scopeName,
    ...attributesToMap(log.resourceAttributes),
    ...attributesToMap(log.scopeAttributes),
    ...attributesToMap(log.attributes),
  };
}

export function spanFilterFields(span: SpanSummary): Record<string, string> {
  return {
    Name: span.name,
    Kind: span.kind,
    Resource: span.resourceName ?? "",
    TraceId: span.traceId,
    SpanId: span.spanId,
    Status: span.statusCode ?? "",
    Duration: (Number(span.durationNanos) / 1_000_000).toString(),
    ScopeName: span.scopeName,
    ...attributesToMap(span.resourceAttributes),
    ...attributesToMap(span.scopeAttributes),
    ...attributesToMap(span.attributes),
  };
}

export function matchesTelemetryFilters(fields: Record<string, string>, filters: TelemetryFilter[]): boolean {
  return filters.filter((filter) => filter.enabled).every((filter) => {
    const actual = fields[filter.field] ?? "";
    const left = actual.toLocaleLowerCase();
    const right = filter.value.toLocaleLowerCase();
    switch (filter.condition) {
      case "equals": return left === right;
      case "contains": return left.includes(right);
      case "notEquals": return left !== right;
      case "notContains": return !left.includes(right);
      case "gt": return Number(actual) > Number(filter.value);
      case "lt": return Number(actual) < Number(filter.value);
      case "gte": return Number(actual) >= Number(filter.value);
      case "lte": return Number(actual) <= Number(filter.value);
    }
  });
}

export function telemetryFieldNames(records: Array<Record<string, string>>, known: string[]): string[] {
  return [...new Set([...known, ...records.flatMap((record) => Object.keys(record))])].sort((left, right) => left.localeCompare(right));
}
