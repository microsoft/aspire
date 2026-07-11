import { useMemo, type ReactNode } from "react";
import type { LogRecordSummary, TelemetryAttribute } from "../api/types";
import { dateFromUnixNano, formatTimeWithMillis } from "../lib/format";
import {
  Divider,
  Drawer,
  PropertyExplorer,
  type PropertyExplorerItem,
  type PropertyExplorerSection,
} from "../toolkit";
import { StructuredLogActions } from "./StructuredLogActions";
import { TraceLink } from "./TraceLink";

function textProperty(id: string, label: string, value: string): PropertyExplorerItem {
  return { id, label, value };
}

function nodeProperty(id: string, label: string, value: ReactNode, searchableText: string): PropertyExplorerItem {
  return { id, label, value, searchableText };
}

function attributeProperties(
  prefix: string,
  attributes: readonly TelemetryAttribute[],
): PropertyExplorerItem[] {
  return attributes.map((attribute, index) =>
    textProperty(`${prefix}-${index}-${attribute.key}`, attribute.key, attribute.value));
}

function hasTelemetryId(value: string | null): value is string {
  return value !== null && value.length > 0 && /[^0]/.test(value);
}

export function StructuredLogDetailsDrawer({
  log,
  onClose,
  onViewMessage,
  onViewJson,
  onNavigateToTrace,
  canNavigateToTrace,
}: {
  log: LogRecordSummary;
  onClose: () => void;
  onViewMessage: () => void;
  onViewJson: () => void;
  onNavigateToTrace: (traceId: string, spanId: string | null) => void;
  canNavigateToTrace: boolean;
}) {
  const sections = useMemo(() => {
    const logEntry = [
      textProperty("level", "Level", log.severity ?? "Unknown"),
      textProperty("message", "Message", log.body),
      ...(log.originalFormat ? [textProperty("original-format", "Original format", log.originalFormat)] : []),
      ...attributeProperties("log", log.attributes.filter((attribute) =>
        !attribute.key.toLocaleLowerCase().startsWith("exception."))),
      ...(log.droppedAttributesCount > 0
        ? [textProperty("dropped-attributes", "Dropped attributes", String(log.droppedAttributesCount))]
        : []),
    ];
    const context = [
      textProperty("category", "Category", log.scopeName),
      ...(log.scopeVersion ? [textProperty("scope-version", "Scope version", log.scopeVersion)] : []),
      ...(log.eventName ? [textProperty("event-name", "EventName", log.eventName)] : []),
      ...(hasTelemetryId(log.traceId) && canNavigateToTrace
        ? [nodeProperty(
            "trace-id",
            "TraceId",
            <TraceLink
              traceId={log.traceId}
              spanId={log.spanId}
              onNavigate={onNavigateToTrace}
            />,
            log.traceId,
          )]
        : hasTelemetryId(log.traceId) ? [textProperty("trace-id", "TraceId", log.traceId)] : []),
      ...(hasTelemetryId(log.spanId) ? [textProperty("span-id", "SpanId", log.spanId)] : []),
      ...(hasTelemetryId(log.parentId) ? [textProperty("parent-id", "ParentId", log.parentId)] : []),
      ...attributeProperties("scope", log.scopeAttributes),
      ...(log.flags > 0 ? [textProperty("flags", "Flags", String(log.flags))] : []),
      ...(log.scopeDroppedAttributesCount > 0
        ? [textProperty("scope-dropped-attributes", "Dropped scope attributes", String(log.scopeDroppedAttributesCount))]
        : []),
    ];
    const exception = attributeProperties("exception", log.attributes.filter((attribute) =>
      attribute.key.toLocaleLowerCase().startsWith("exception.")));
    const resourceAttributes = log.resourceAttributes.some((attribute) => attribute.key === "service.name")
      ? log.resourceAttributes
      : [
          ...(log.resourceName ? [{ key: "service.name", value: log.resourceName }] : []),
          ...log.resourceAttributes,
        ];
    const resource = [
      ...attributeProperties("resource", resourceAttributes),
      ...(log.resourceDroppedAttributesCount > 0
        ? [textProperty("resource-dropped-attributes", "Dropped resource attributes", String(log.resourceDroppedAttributesCount))]
        : []),
    ];

    return [
      { id: "log-entry", heading: "Log entry", ariaLabel: "Log entry properties", items: logEntry },
      { id: "context", heading: "Context", ariaLabel: "Context properties", items: context },
      ...(exception.length > 0
        ? [{ id: "exception", heading: "Exception", ariaLabel: "Exception properties", items: exception }]
        : []),
      { id: "resource", heading: "Resource", ariaLabel: "Resource properties", items: resource },
    ] satisfies PropertyExplorerSection[];
  }, [canNavigateToTrace, log, onNavigateToTrace]);

  const timestamp = dateFromUnixNano(log.timeUnixNano);

  return (
    <Drawer
      title={log.eventName ?? "Structured log entry details"}
      subtitle={log.scopeName}
      ariaLabel="Structured log entry details"
      closeLabel="Close details"
      onClose={onClose}
    >
      <PropertyExplorer
        className="structured-log-details"
        ariaLabel="Structured log detail"
        sections={sections}
        defaultOpenItems={["log-entry", "context", "exception", "resource"]}
        searchPlaceholder="Filter properties…"
        toolbarStart={(
          <div className="structured-log-details__metadata">
            <span className="structured-log-details__meta">
              Resource <strong>{log.resourceName ?? "unknown"}</strong>
            </span>
            <Divider label="Log metadata" />
            <span className="structured-log-details__meta" title={timestamp.toLocaleString()}>
              Timestamp <strong>{formatTimeWithMillis(timestamp)}</strong>
            </span>
          </div>
        )}
        toolbarEnd={(
          <StructuredLogActions
            placement="below-start"
            onViewMessage={onViewMessage}
            onViewJson={onViewJson}
          />
        )}
      />
    </Drawer>
  );
}
