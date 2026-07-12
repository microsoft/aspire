import type { SpanSummary } from "../api/types";

export type SpanTypeId = "all" | "http" | "database" | "messaging" | "rpc" | "genai" | "cloud" | "other";

export const SPAN_TYPE_OPTIONS: ReadonlyArray<{ value: SpanTypeId; label: string }> = [
  { value: "all", label: "All span types" },
  { value: "http", label: "HTTP" },
  { value: "database", label: "Database" },
  { value: "messaging", label: "Messaging" },
  { value: "rpc", label: "RPC" },
  { value: "genai", label: "Generative AI" },
  { value: "cloud", label: "Cloud" },
  { value: "other", label: "Other" },
];

const ATTRIBUTE_TYPES: ReadonlyArray<{ type: Exclude<SpanTypeId, "all" | "cloud" | "other">; keys: readonly string[] }> = [
  { type: "http", keys: ["http.request.method"] },
  { type: "database", keys: ["db.system.name", "db.system"] },
  { type: "messaging", keys: ["messaging.system"] },
  { type: "rpc", keys: ["rpc.system"] },
  { type: "genai", keys: ["gen_ai.system", "gen_ai.provider.name", "gen_ai.operation.name"] },
];

function hasAttribute(span: SpanSummary, keys: readonly string[]): boolean {
  return span.attributes.some((attribute) => keys.includes(attribute.key) && attribute.value !== "");
}

function hasCloudScope(span: SpanSummary): boolean {
  const scope = span.scopeName.toLowerCase();
  return ["azure", "awssdk"].some((prefix) => scope === prefix || scope.startsWith(`${prefix}.`));
}

export function spanType(span: SpanSummary): Exclude<SpanTypeId, "all"> {
  for (const candidate of ATTRIBUTE_TYPES) {
    if (hasAttribute(span, candidate.keys)) {
      return candidate.type;
    }
  }
  return hasCloudScope(span) ? "cloud" : "other";
}

export function spanMatchesType(span: SpanSummary, type: SpanTypeId): boolean {
  return type === "all" || spanType(span) === type;
}
