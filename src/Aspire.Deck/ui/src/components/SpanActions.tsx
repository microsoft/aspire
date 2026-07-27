import type { SpanSummary, TelemetryAttribute } from "../api/types";
import { CommandMenu, LogsIcon, MoreIcon, NamedIcon } from "../toolkit";

function attributesToObject(attributes: readonly TelemetryAttribute[]): Record<string, string> {
  return Object.fromEntries(attributes.map((attribute) => [attribute.key, attribute.value]));
}

export function formatSpanJson(span: SpanSummary): string {
  return JSON.stringify({
    traceId: span.traceId,
    spanId: span.spanId,
    traceState: span.traceState,
    parentSpanId: span.parentSpanId,
    flags: span.flags,
    name: span.name,
    kind: span.kind,
    startTimeUnixNano: span.startUnixNano,
    durationNanos: span.durationNanos,
    status: {
      code: span.statusCode,
      message: span.statusMessage,
    },
    attributes: attributesToObject(span.attributes),
    droppedAttributesCount: span.droppedAttributesCount,
    droppedEventsCount: span.droppedEventsCount,
    droppedLinksCount: span.droppedLinksCount,
    events: span.events.map((event) => ({
      timeUnixNano: event.timeUnixNano,
      name: event.name,
      attributes: attributesToObject(event.attributes),
      droppedAttributesCount: event.droppedAttributesCount,
    })),
    links: span.links.map((link) => ({
      traceId: link.traceId,
      spanId: link.spanId,
      traceState: link.traceState,
      attributes: attributesToObject(link.attributes),
      droppedAttributesCount: link.droppedAttributesCount,
      flags: link.flags,
    })),
    scope: {
      name: span.scopeName,
      version: span.scopeVersion,
      attributes: attributesToObject(span.scopeAttributes),
      droppedAttributesCount: span.scopeDroppedAttributesCount,
    },
    resource: {
      name: span.resourceName,
      attributes: attributesToObject(span.resourceAttributes),
      droppedAttributesCount: span.resourceDroppedAttributesCount,
    },
  }, null, 2);
}

export function SpanActions({
  onViewDetails,
  onViewLogs,
  onViewJson,
  onViewGenAI,
  placement = "below-end",
}: {
  onViewDetails?: () => void;
  onViewLogs: () => void;
  onViewJson: () => void;
  onViewGenAI?: () => void;
  placement?: "below-start" | "below-end" | "above-start" | "above-end";
}) {
  return (
    <CommandMenu
      ariaLabel="Span actions"
      triggerContent={null}
      triggerIcon={<MoreIcon size={16} />}
      triggerSize="small"
      placement={placement}
      entries={[
        ...(onViewGenAI ? [{ id: "view-genai", label: "View Generative AI details", icon: <NamedIcon name="Sparkle" size={16} />, onSelect: onViewGenAI }] : []),
        ...(onViewDetails
          ? [{
              id: "view-details",
              label: "View details",
              icon: <NamedIcon name="Info" size={16} />,
              onSelect: onViewDetails,
            }]
          : []),
        {
          id: "view-logs",
          label: "View related structured logs",
          icon: <LogsIcon size={16} />,
          onSelect: onViewLogs,
        },
        {
          id: "view-json",
          label: "View JSON",
          icon: <NamedIcon name="Braces" size={16} />,
          onSelect: onViewJson,
        },
      ]}
    />
  );
}
