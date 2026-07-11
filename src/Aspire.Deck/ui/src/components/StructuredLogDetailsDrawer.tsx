import { useMemo, useState, type ReactNode } from "react";
import type { LogRecordSummary, TelemetryAttribute } from "../api/types";
import { dateFromUnixNano, formatTimeWithMillis } from "../lib/format";
import {
  Accordion,
  Divider,
  Drawer,
  Highlighter,
  PropertyGrid,
  SearchBox,
  type AccordionItem,
  type PropertyGridItem,
} from "../toolkit";
import { StructuredLogActions } from "./StructuredLogActions";

interface DetailProperty {
  id: string;
  label: string;
  value: string | ReactNode;
  searchableValue: string;
}

function textProperty(id: string, label: string, value: string): DetailProperty {
  return { id, label, value, searchableValue: value };
}

function attributeProperties(
  prefix: string,
  attributes: readonly TelemetryAttribute[],
): DetailProperty[] {
  return attributes.map((attribute, index) =>
    textProperty(`${prefix}-${index}-${attribute.key}`, attribute.key, attribute.value));
}

function hasTelemetryId(value: string | null): value is string {
  return value !== null && value.length > 0 && /[^0]/.test(value);
}

function filterProperties(properties: readonly DetailProperty[], query: string): DetailProperty[] {
  const normalized = query.trim().toLocaleLowerCase();
  if (normalized.length === 0) {
    return [...properties];
  }
  return properties.filter((property) =>
    property.label.toLocaleLowerCase().includes(normalized)
    || property.searchableValue.toLocaleLowerCase().includes(normalized));
}

function toGridItems(properties: readonly DetailProperty[], query: string): PropertyGridItem[] {
  return properties.map((property) => ({
    id: property.id,
    label: <Highlighter text={property.label} highlightedText={query} />,
    value: typeof property.value === "string"
      ? <Highlighter text={property.value} highlightedText={query} />
      : property.value,
  }));
}

export function StructuredLogDetailsDrawer({
  log,
  onClose,
  onViewMessage,
  onViewJson,
}: {
  log: LogRecordSummary;
  onClose: () => void;
  onViewMessage: () => void;
  onViewJson: () => void;
}) {
  const [query, setQuery] = useState("");
  const [openItems, setOpenItems] = useState(["log-entry", "context", "exception", "resource"]);

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
      ...(hasTelemetryId(log.traceId) ? [textProperty("trace-id", "TraceId", log.traceId)] : []),
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

    return { logEntry, context, exception, resource };
  }, [log]);

  const accordionItems = useMemo(() => {
    const createItem = (
      id: string,
      heading: string,
      properties: readonly DetailProperty[],
    ): AccordionItem => {
      const filtered = filterProperties(properties, query);
      return {
        id,
        heading,
        count: filtered.length,
        content: (
          <PropertyGrid
            ariaLabel={`${heading} properties`}
            items={toGridItems(filtered, query)}
          />
        ),
      };
    };

    return [
      createItem("log-entry", "Log entry", sections.logEntry),
      createItem("context", "Context", sections.context),
      ...(sections.exception.length > 0
        ? [createItem("exception", "Exception", sections.exception)]
        : []),
      createItem("resource", "Resource", sections.resource),
    ];
  }, [query, sections]);

  const timestamp = dateFromUnixNano(log.timeUnixNano);

  return (
    <Drawer
      title={log.eventName ?? "Structured log entry details"}
      subtitle={log.scopeName}
      ariaLabel="Structured log entry details"
      closeLabel="Close details"
      onClose={onClose}
    >
      <div className="structured-log-details">
        <div className="structured-log-details__toolbar" role="toolbar" aria-label="Structured log detail tools">
          <span className="structured-log-details__meta">
            Resource <strong>{log.resourceName ?? "unknown"}</strong>
          </span>
          <Divider label="Log metadata" />
          <span className="structured-log-details__meta" title={timestamp.toLocaleString()}>
            Timestamp <strong>{formatTimeWithMillis(timestamp)}</strong>
          </span>
          <SearchBox value={query} onChange={setQuery} placeholder="Filter properties…" />
          <StructuredLogActions
            placement="below-start"
            onViewMessage={onViewMessage}
            onViewJson={onViewJson}
          />
        </div>
        <Accordion
          className="structured-log-details__sections"
          items={accordionItems}
          openItems={openItems}
          onOpenItemsChange={setOpenItems}
        />
      </div>
    </Drawer>
  );
}
