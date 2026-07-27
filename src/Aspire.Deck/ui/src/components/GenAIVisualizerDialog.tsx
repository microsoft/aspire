import type { LogRecordSummary, SpanSummary, TelemetryAttribute } from "../api/types";
import { Button, Dialog, NamedIcon } from "../toolkit";

interface GenAIMessage {
  role: string;
  content: string;
}

export function hasGenAIAttributes(attributes: readonly TelemetryAttribute[]): boolean {
  return attributes.some((attribute) => attribute.key.startsWith("gen_ai."));
}

function attributeMap(attributes: readonly TelemetryAttribute[]): Map<string, string> {
  return new Map(attributes.map((attribute) => [attribute.key, attribute.value]));
}

function collectMessages(value: unknown, fallbackRole: string, messages: GenAIMessage[]): void {
  if (Array.isArray(value)) {
    for (const item of value) collectMessages(item, fallbackRole, messages);
    return;
  }
  if (value === null || typeof value !== "object") return;
  const record = value as Record<string, unknown>;
  const role = typeof record.role === "string" ? record.role : fallbackRole;
  const content = typeof record.content === "string" ? record.content
    : typeof record.text === "string" ? record.text
      : null;
  if (content) messages.push({ role, content });
  for (const [key, child] of Object.entries(record)) {
    if (key !== "content" && key !== "text" && key !== "role") collectMessages(child, role, messages);
  }
}

function parseJsonMessages(value: string, fallbackRole: string): GenAIMessage[] {
  try {
    const messages: GenAIMessage[] = [];
    collectMessages(JSON.parse(value), fallbackRole, messages);
    return messages;
  } catch {
    return value.trim() ? [{ role: fallbackRole, content: value }] : [];
  }
}

function spanMessages(span: SpanSummary): GenAIMessage[] {
  const messages: GenAIMessage[] = [];
  for (const attribute of span.attributes) {
    if (attribute.key === "gen_ai.system_instructions") messages.push(...parseJsonMessages(attribute.value, "system"));
    if (attribute.key === "gen_ai.input.messages") messages.push(...parseJsonMessages(attribute.value, "user"));
    if (attribute.key === "gen_ai.output.messages") messages.push(...parseJsonMessages(attribute.value, "assistant"));
    const legacy = /^gen_ai\.(prompt|completion)\.\d+\.(?:message\.)?(role|content)$/.exec(attribute.key);
    if (legacy?.[2] === "content") messages.push({ role: legacy[1] === "prompt" ? "user" : "assistant", content: attribute.value });
  }
  for (const event of span.events.filter((item) => item.name.startsWith("gen_ai."))) {
    const attributes = attributeMap(event.attributes);
    const content = attributes.get("gen_ai.event.content");
    if (content) messages.push(...parseJsonMessages(content, event.name.split(".")[1] ?? "event"));
  }
  return messages;
}

function logMessages(log: LogRecordSummary): GenAIMessage[] {
  const role = (log.eventName ?? attributeMap(log.attributes).get("event.name") ?? "message").split(".")[1] ?? "message";
  return parseJsonMessages(log.body, role);
}

export function GenAIVisualizerDialog({ source, onClose }: { source: SpanSummary | LogRecordSummary | null; onClose: () => void }) {
  if (source === null) return null;
  const isSpan = "name" in source;
  const attributes = attributeMap(source.attributes);
  const messages = isSpan ? spanMessages(source) : logMessages(source);
  const system = attributes.get("gen_ai.system") ?? attributes.get("gen_ai.provider.name") ?? "Unknown provider";
  const model = attributes.get("gen_ai.request.model") ?? attributes.get("gen_ai.response.model") ?? "Unknown model";
  return (
    <Dialog
      open
      title={<span className="genai-dialog__title"><NamedIcon name="Sparkle" size={18} /> Generative AI details</span>}
      onClose={onClose}
      className="genai-dialog"
      actions={<Button variant="primary" onClick={onClose}>Close</Button>}
    >
      <div className="genai-dialog__metadata">
        <span>Provider <strong>{system}</strong></span>
        <span>Model <strong>{model}</strong></span>
        {isSpan ? <span>Operation <strong>{attributes.get("gen_ai.operation.name") ?? source.name}</strong></span> : null}
      </div>
      <div className="genai-dialog__conversation" role="region" aria-label="GenAI conversation">
        {messages.length > 0 ? messages.map((message, index) => (
          <section key={`${message.role}-${index}`} className={`genai-message genai-message--${message.role}`}>
            <h3>{message.role}</h3>
            <pre>{message.content}</pre>
          </section>
        )) : <p className="cell-muted">No captured message content.</p>}
      </div>
    </Dialog>
  );
}
