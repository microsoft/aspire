import type { LogRecordSummary, SpanSummary, TelemetryAttribute } from "../api/types";

export type TelemetryFilterCondition = "equals" | "contains" | "gt" | "lt" | "gte" | "lte" | "notEquals" | "notContains";

export interface TelemetryFilter {
  id: string;
  field: string;
  condition: TelemetryFilterCondition;
  value: string;
  enabled: boolean;
}

/**
 * Wire names for filter conditions, matching `TelemetryFilterFormatter.SerializeFilterToString`
 * in the Blazor dashboard. Negations use a `!` prefix rather than a camelCase name.
 */
const conditionToWire: Record<TelemetryFilterCondition, string> = {
  equals: "equals",
  contains: "contains",
  gt: "gt",
  lt: "lt",
  gte: "gte",
  lte: "lte",
  notEquals: "!equals",
  notContains: "!contains",
};

const wireToCondition = new Map<string, TelemetryFilterCondition>(
  Object.entries(conditionToWire).map(([condition, wire]) => [wire, condition as TelemetryFilterCondition]),
);

const DISABLED_TEXT = "disabled";

/**
 * Parses the `filters` query-string parameter shared with the Blazor dashboard.
 *
 * The format is a space-separated list of colon-separated triples, with an optional fourth part
 * marking the filter as disabled. Field names and values are URL-encoded, which is what keeps
 * embedded spaces and colons from breaking the split:
 *
 *   Severity:equals:Error Message:!contains:health+check url:equals:%2fapi%2fv1:disabled
 *
 * `+` decodes to a space because the Blazor side escapes with `HttpUtility.UrlEncode`, which is
 * form-urlencoding rather than RFC 3986 percent-encoding.
 * See `src/Aspire.Dashboard/Extensions/TelemetryFilterFormatter.cs`.
 *
 * Unrecognised entries are skipped individually - matching Blazor, which returns `null` for a bad
 * entry and filters it out - so one malformed filter in a hand-edited link cannot discard the
 * rest.
 */
export function parseTelemetryFilters(value: string | null): TelemetryFilter[] {
  if (!value) return [];

  return value.split(" ").flatMap((entry, index) => {
    if (entry.length === 0) return [];

    const [field, wire, rawValue, disabledMarker] = entry.split(":");
    if (field === undefined || wire === undefined || rawValue === undefined) return [];
    // A fifth part means an unescaped `:` slipped in, so the entry is not trustworthy.
    if (entry.split(":").length > 4) return [];

    const condition = wireToCondition.get(wire);
    if (!condition) return [];

    return [{
      id: `restored-${index}`,
      field: unescapeFilterPart(field),
      condition,
      value: unescapeFilterPart(rawValue),
      enabled: disabledMarker !== DISABLED_TEXT,
    }];
  });
}

export function serializeTelemetryFilters(filters: TelemetryFilter[]): string | undefined {
  if (filters.length === 0) return undefined;

  return filters
    .map((filter) => {
      const parts = [escapeFilterPart(filter.field), conditionToWire[filter.condition], escapeFilterPart(filter.value)];
      if (!filter.enabled) {
        parts.push(DISABLED_TEXT);
      }
      return parts.join(":");
    })
    .join(" ");
}

function escapeFilterPart(value: string): string {
  // `:` and ` ` are the delimiters, so they must always be encoded. encodeURIComponent covers both
  // and produces output that HttpUtility.UrlDecode round-trips correctly on the Blazor side.
  return encodeURIComponent(value);
}

function unescapeFilterPart(value: string): string {
  try {
    return decodeURIComponent(value.replace(/\+/g, " "));
  } catch {
    // A hand-edited link can contain a stray `%`; show it literally rather than dropping the filter.
    return value;
  }
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
