import type { LogRecordSummary, TelemetryAttribute } from "../api/types";
import { CommandMenu, MoreIcon, NamedIcon } from "../toolkit";

function attributesToObject(attributes: readonly TelemetryAttribute[]): Record<string, string> {
  return Object.fromEntries(attributes.map((attribute) => [attribute.key, attribute.value]));
}

export function formatStructuredLogJson(log: LogRecordSummary): string {
  return JSON.stringify({
    timeUnixNano: log.timeUnixNano,
    observedTimeUnixNano: log.observedTimeUnixNano,
    severityNumber: log.severityNumber,
    severityText: log.severity,
    body: log.body,
    attributes: attributesToObject(log.attributes),
    droppedAttributesCount: log.droppedAttributesCount,
    flags: log.flags,
    traceId: log.traceId,
    spanId: log.spanId,
    parentId: log.parentId,
    eventName: log.eventName,
    originalFormat: log.originalFormat,
    scope: {
      name: log.scopeName,
      version: log.scopeVersion,
      attributes: attributesToObject(log.scopeAttributes),
      droppedAttributesCount: log.scopeDroppedAttributesCount,
    },
    resource: {
      name: log.resourceName,
      attributes: attributesToObject(log.resourceAttributes),
      droppedAttributesCount: log.resourceDroppedAttributesCount,
    },
  }, null, 2);
}

export function StructuredLogActions({
  onViewDetails,
  onViewMessage,
  onViewJson,
  placement = "below-end",
}: {
  onViewDetails?: () => void;
  onViewMessage: () => void;
  onViewJson: () => void;
  placement?: "below-start" | "below-end" | "above-start" | "above-end";
}) {
  return (
    <CommandMenu
      ariaLabel="Log actions"
      triggerContent={null}
      triggerIcon={<MoreIcon size={16} />}
      triggerSize="small"
      placement={placement}
      entries={[
        ...(onViewDetails
          ? [{
              id: "view-details",
              label: "View details",
              icon: <NamedIcon name="Info" size={16} />,
              onSelect: onViewDetails,
            }]
          : []),
        {
          id: "view-message",
          label: "Open message in text visualizer",
          icon: <NamedIcon name="Open" size={16} />,
          onSelect: onViewMessage,
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
