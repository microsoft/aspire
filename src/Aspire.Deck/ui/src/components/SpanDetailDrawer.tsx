import { useMemo, type ReactNode } from "react";
import type {
  SpanEventSummary,
  SpanLinkSummary,
  SpanSummary,
  TelemetryAttribute,
} from "../api/types";
import { dateFromUnixNano, formatDateTime, formatDurationNanos, formatTimeWithMillis, shortId } from "../lib/format";
import {
  Badge,
  Divider,
  Drawer,
  PropertyExplorer,
  PropertyGrid,
  type PropertyExplorerItem,
  type PropertyExplorerSection,
} from "../toolkit";
import { SpanActions } from "./SpanActions";
import { TraceLink } from "./TraceLink";

interface SpanBacklink {
  source: SpanSummary;
  link: SpanLinkSummary;
}

function statusTone(code: string | null): "neutral" | "success" | "error" {
  switch (code) {
    case "Ok":
      return "success";
    case "Error":
      return "error";
    default:
      return "neutral";
  }
}

function textProperty(
  id: string,
  label: string,
  value: string,
  valueClassName?: string,
): PropertyExplorerItem {
  return { id, label, value, valueClassName };
}

function nodeProperty(
  id: string,
  label: string,
  value: ReactNode,
  searchableText: string,
  valueClassName?: string,
): PropertyExplorerItem {
  return { id, label, value, searchableText, valueClassName };
}

function attributeProperties(
  prefix: string,
  attributes: readonly TelemetryAttribute[],
): PropertyExplorerItem[] {
  return attributes.map((attribute, index) =>
    textProperty(`${prefix}-${index}-${attribute.key}`, attribute.key, attribute.value));
}

function attributesText(attributes: readonly TelemetryAttribute[]): string {
  return attributes.map((attribute) => `${attribute.key} ${attribute.value}`).join(" ");
}

function nestedAttributes(
  ariaLabel: string,
  attributes: readonly TelemetryAttribute[],
): ReactNode {
  if (attributes.length === 0) {
    return null;
  }

  return (
    <PropertyGrid
      className="span-detail__nested-properties"
      ariaLabel={ariaLabel}
      items={attributes.map((attribute, index) => ({
        id: `${index}-${attribute.key}`,
        label: attribute.key,
        value: attribute.value,
      }))}
    />
  );
}

function eventOffset(span: SpanSummary, event: SpanEventSummary): string {
  try {
    const offset = BigInt(event.timeUnixNano) - BigInt(span.startUnixNano);
    return `${offset >= 0n ? "+" : ""}${formatDurationNanos(offset.toString())}`;
  } catch {
    return formatTimeWithMillis(dateFromUnixNano(event.timeUnixNano));
  }
}

function hasTelemetryId(value: string | null): value is string {
  return value !== null && value.length > 0 && /[^0]/.test(value);
}

export function SpanDetailDrawer({
  span,
  allSpans,
  color,
  onClose,
  onNavigateToSpan,
  onViewLogs,
  onViewJson,
  onViewGenAI,
}: {
  span: SpanSummary;
  allSpans: readonly SpanSummary[];
  color: string;
  onClose: () => void;
  onNavigateToSpan: (traceId: string, spanId: string | null) => void;
  onViewLogs: () => void;
  onViewJson: () => void;
  onViewGenAI?: () => void;
}) {
  const backlinks = useMemo<SpanBacklink[]>(() => allSpans.flatMap((source) =>
    source.links.flatMap((link) =>
      link.traceId === span.traceId && link.spanId === span.spanId ? [{ source, link }] : [])),
  [allSpans, span.spanId, span.traceId]);
  const spansById = useMemo(
    () => new Map(allSpans.map((item) => [`${item.traceId}\u0000${item.spanId}`, item])),
    [allSpans],
  );

  const sections = useMemo(() => {
    const spanProperties: PropertyExplorerItem[] = [
      textProperty("span-id", "SpanId", span.spanId, "cell-mono"),
      textProperty("name", "Name", span.name),
      textProperty("kind", "Kind", span.kind),
      nodeProperty(
        "status",
        "Status",
        <Badge tone={statusTone(span.statusCode)}>{span.statusCode ?? "Unset"}</Badge>,
        span.statusCode ?? "Unset",
      ),
      ...(span.statusMessage
        ? [textProperty("status-message", "StatusMessage", span.statusMessage)]
        : []),
      textProperty("started", "Started", formatTimeWithMillis(dateFromUnixNano(span.startUnixNano))),
      textProperty("duration", "Duration", formatDurationNanos(span.durationNanos)),
      ...attributeProperties("span", span.attributes),
      ...(span.droppedAttributesCount > 0
        ? [textProperty("dropped-attributes", "Dropped attributes", String(span.droppedAttributesCount))]
        : []),
      ...(span.droppedEventsCount > 0
        ? [textProperty("dropped-events", "Dropped events", String(span.droppedEventsCount))]
        : []),
      ...(span.droppedLinksCount > 0
        ? [textProperty("dropped-links", "Dropped links", String(span.droppedLinksCount))]
        : []),
    ];
    const context: PropertyExplorerItem[] = [
      textProperty("scope-name", "Source", span.scopeName),
      ...(span.scopeVersion ? [textProperty("scope-version", "Version", span.scopeVersion)] : []),
      nodeProperty(
        "trace-id",
        "TraceId",
        <TraceLink traceId={span.traceId} spanId={span.spanId} onNavigate={onNavigateToSpan} />,
        span.traceId,
        "cell-mono",
      ),
      ...(hasTelemetryId(span.parentSpanId)
        ? [nodeProperty(
            "parent-id",
            "ParentId",
            <TraceLink
              traceId={span.traceId}
              spanId={span.parentSpanId}
              displayText={span.parentSpanId}
              ariaLabel={`Open parent span ${shortId(span.parentSpanId)}`}
              onNavigate={onNavigateToSpan}
            />,
            span.parentSpanId,
            "cell-mono",
          )]
        : []),
      ...(span.traceState ? [textProperty("trace-state", "TraceState", span.traceState)] : []),
      ...(span.flags > 0 ? [textProperty("flags", "Flags", String(span.flags))] : []),
      ...attributeProperties("scope", span.scopeAttributes),
      ...(span.scopeDroppedAttributesCount > 0
        ? [textProperty(
            "scope-dropped-attributes",
            "Dropped scope attributes",
            String(span.scopeDroppedAttributesCount),
          )]
        : []),
    ];
    const resourceAttributes = span.resourceAttributes.some((attribute) => attribute.key === "service.name")
      ? span.resourceAttributes
      : [
          ...(span.resourceName ? [{ key: "service.name", value: span.resourceName }] : []),
          ...span.resourceAttributes,
        ];
    const resource: PropertyExplorerItem[] = [
      ...attributeProperties("resource", resourceAttributes),
      ...(span.resourceDroppedAttributesCount > 0
        ? [textProperty(
            "resource-dropped-attributes",
            "Dropped resource attributes",
            String(span.resourceDroppedAttributesCount),
          )]
        : []),
    ];
    const events = span.events.map((event, index): PropertyExplorerItem => nodeProperty(
      `event-${index}`,
      eventOffset(span, event),
      <div className="span-detail__record">
        <div className="span-detail__record-heading">
          <strong>{event.name}</strong>
          <span title={formatDateTime(dateFromUnixNano(event.timeUnixNano))}>
            {formatTimeWithMillis(dateFromUnixNano(event.timeUnixNano))}
          </span>
        </div>
        {event.droppedAttributesCount > 0 ? (
          <div className="span-detail__record-meta">
            {event.droppedAttributesCount} dropped attributes
          </div>
        ) : null}
        {nestedAttributes(`${event.name} event attributes`, event.attributes)}
      </div>,
      `${event.name} ${event.timeUnixNano} ${attributesText(event.attributes)}`,
    ));
    const links = span.links.map((link, index): PropertyExplorerItem => {
      const linkedSpan = spansById.get(`${link.traceId}\u0000${link.spanId}`);
      return nodeProperty(
        `link-${index}`,
        linkedSpan?.name ?? `Span ${shortId(link.spanId)}`,
        <div className="span-detail__record">
          <TraceLink
            traceId={link.traceId}
            spanId={link.spanId}
            displayText={link.spanId}
            ariaLabel={`Open linked span ${shortId(link.spanId)}`}
            onNavigate={onNavigateToSpan}
          />
          {link.traceState ? <div className="span-detail__record-meta">TraceState {link.traceState}</div> : null}
          {link.flags > 0 ? <div className="span-detail__record-meta">Flags {link.flags}</div> : null}
          {link.droppedAttributesCount > 0 ? (
            <div className="span-detail__record-meta">{link.droppedAttributesCount} dropped attributes</div>
          ) : null}
          {nestedAttributes(`Link to ${shortId(link.spanId)} attributes`, link.attributes)}
        </div>,
        `${link.traceId} ${link.spanId} ${linkedSpan?.name ?? ""} ${attributesText(link.attributes)}`,
      );
    });
    const backlinkItems = backlinks.map(({ source, link }, index): PropertyExplorerItem => nodeProperty(
      `backlink-${index}`,
      source.name,
      <div className="span-detail__record">
        <TraceLink
          traceId={source.traceId}
          spanId={source.spanId}
          displayText={source.spanId}
          ariaLabel={`Open backlink span ${shortId(source.spanId)}`}
          onNavigate={onNavigateToSpan}
        />
        {nestedAttributes(`Backlink from ${shortId(source.spanId)} attributes`, link.attributes)}
      </div>,
      `${source.traceId} ${source.spanId} ${source.name} ${attributesText(link.attributes)}`,
    ));

    return [
      { id: "span", heading: "Span", ariaLabel: "Span properties", items: spanProperties },
      { id: "context", heading: "Context", ariaLabel: "Context properties", items: context },
      { id: "resource", heading: "Resource", ariaLabel: "Resource properties", items: resource },
      { id: "events", heading: "Events", ariaLabel: "Span events", items: events },
      { id: "links", heading: "Links", ariaLabel: "Span links", items: links },
      { id: "backlinks", heading: "Backlinks", ariaLabel: "Span backlinks", items: backlinkItems },
    ] satisfies PropertyExplorerSection[];
  }, [backlinks, onNavigateToSpan, span, spansById]);

  const defaultOpenItems = [
    "span",
    "context",
    "resource",
    ...(span.events.length > 0 ? ["events"] : []),
    ...(span.links.length > 0 ? ["links"] : []),
    ...(backlinks.length > 0 ? ["backlinks"] : []),
  ];
  const started = dateFromUnixNano(span.startUnixNano);

  return (
    <Drawer
      title={span.name}
      subtitle={span.scopeName}
      leading={<span className="span-detail__swatch" style={{ background: color }} />}
      ariaLabel={span.name}
      closeLabel="Close"
      onClose={onClose}
    >
      <PropertyExplorer
        key={`${span.traceId}-${span.spanId}`}
        className="span-detail"
        ariaLabel="Span details"
        sections={sections}
        defaultOpenItems={defaultOpenItems}
        searchPlaceholder="Filter properties…"
        toolbarStart={(
          <div className="span-detail__metadata">
            <span className="span-detail__meta">
              Resource <strong>{span.resourceName ?? "unknown"}</strong>
            </span>
            <Divider label="Span resource" />
            <span className="span-detail__meta">
              Duration <strong>{formatDurationNanos(span.durationNanos)}</strong>
            </span>
            <Divider label="Span timing" />
            <span className="span-detail__meta" title={formatDateTime(started)}>
              Started <strong>{formatTimeWithMillis(started)}</strong>
            </span>
          </div>
        )}
        toolbarEnd={(
          <SpanActions
            placement="below-start"
            onViewLogs={onViewLogs}
            onViewJson={onViewJson}
            onViewGenAI={onViewGenAI}
          />
        )}
      />
    </Drawer>
  );
}
