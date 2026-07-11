import { useMemo, useState } from "react";
import { clearStructuredLogs } from "../api/deck";
import type { LogRecordSummary, TelemetrySummary } from "../api/types";
import { StructuredLogActions, formatStructuredLogJson } from "../components/StructuredLogActions";
import { StructuredLogDetailsDrawer } from "../components/StructuredLogDetailsDrawer";
import { TraceLink } from "../components/TraceLink";
import { useTelemetry } from "../lib/useDeckEvent";
import { dateFromUnixNano, formatTimeWithMillis, shortId } from "../lib/format";
import {
  Badge,
  CommandMenu,
  DataTable,
  NamedIcon,
  Page,
  PageBody,
  PageHeader,
  PageHeading,
  PageSubtitle,
  PageTitle,
  PageToolbar,
  SearchBox,
  Select,
  Switch,
  TextViewerDialog,
  type Column,
  type TextViewerRequest,
} from "../toolkit";

const SEVERITIES = ["All", "Trace", "Debug", "Information", "Warning", "Error", "Critical"];
const SEVERITY_OPTIONS = SEVERITIES.map((value) => ({ value, label: value }));
const SEVERITY_MINIMUMS: Record<string, number> = {
  Trace: 1,
  Debug: 5,
  Information: 9,
  Warning: 13,
  Error: 17,
  Critical: 21,
};

function severityTone(severity: string | null): "neutral" | "info" | "warning" | "error" {
  switch (severity) {
    case "Error":
    case "Critical":
      return "error";
    case "Warning":
      return "warning";
    case "Information":
      return "info";
    default:
      return "neutral";
  }
}

function logKey(log: LogRecordSummary): string {
  return `${log.resourceName ?? ""}-${log.timeUnixNano}-${log.spanId ?? ""}-${log.severityNumber}-${log.body}`;
}

export function StructuredLogsPage({
  onNavigateToTrace,
}: {
  onNavigateToTrace: (traceId: string, spanId: string | null) => void;
}) {
  const telemetry = useTelemetry();
  const [query, setQuery] = useState("");
  const [severity, setSeverity] = useState("All");
  const [resource, setResource] = useState("all");
  const [pausedSnapshot, setPausedSnapshot] = useState<TelemetrySummary | null>(null);
  const [clearing, setClearing] = useState(false);
  const [clearStatus, setClearStatus] = useState<{ message: string; error: boolean } | null>(null);
  const [selectedLog, setSelectedLog] = useState<LogRecordSummary | null>(null);
  const [textViewer, setTextViewer] = useState<TextViewerRequest | null>(null);

  const displayedTelemetry = pausedSnapshot ?? telemetry;
  const logs = displayedTelemetry?.recentLogs ?? [];
  const availableTraceIds = useMemo(
    () => new Set((displayedTelemetry?.recentSpans ?? []).map((span) => span.traceId)),
    [displayedTelemetry?.recentSpans],
  );
  const resourceOptions = useMemo(() => [
    { value: "all", label: "All resources" },
    ...[...new Set(logs.flatMap((log) => log.resourceName === null ? [] : [log.resourceName]))]
      .sort((left, right) => left.localeCompare(right))
      .map((value) => ({ value, label: value })),
  ], [logs]);
  const selectedResource = resource === "all" || resourceOptions.some((option) => option.value === resource)
    ? resource
    : "all";

  const filtered = useMemo(() => {
    const trimmed = query.trim().toLowerCase();
    const minimumSeverity = SEVERITY_MINIMUMS[severity];
    return logs.filter((log) => {
      if (selectedResource !== "all" && log.resourceName !== selectedResource) {
        return false;
      }
      if (minimumSeverity !== undefined && log.severityNumber < minimumSeverity) {
        return false;
      }
      if (trimmed) {
        return (
          log.body.toLowerCase().includes(trimmed) ||
          (log.resourceName ?? "").toLowerCase().includes(trimmed) ||
          (log.eventName ?? "").toLowerCase().includes(trimmed) ||
          log.scopeName.toLowerCase().includes(trimmed) ||
          [...log.attributes, ...log.scopeAttributes, ...log.resourceAttributes].some((attribute) =>
            attribute.key.toLowerCase().includes(trimmed) || attribute.value.toLowerCase().includes(trimmed))
        );
      }
      return true;
    });
  }, [logs, query, selectedResource, severity]);

  const columns: Column<LogRecordSummary>[] = [
    {
      key: "resource",
      header: "Resource",
      width: "200px",
      render: (log) => <span className="cell-muted">{log.resourceName ?? "—"}</span>,
    },
    {
      key: "severity",
      header: "Level",
      width: "120px",
      render: (log) => <Badge tone={severityTone(log.severity)}>{log.severity ?? "Unknown"}</Badge>,
    },
    {
      key: "time",
      header: "Timestamp",
      width: "140px",
      render: (log) => (
        <span className="cell-mono cell-muted cell-time">{formatTimeWithMillis(dateFromUnixNano(log.timeUnixNano))}</span>
      ),
    },
    {
      key: "body",
      header: "Message",
      render: (log) => <span className="cell-mono cell-log-message">{log.body}</span>,
    },
    {
      key: "trace",
      header: "Trace",
      width: "90px",
      render: (log) => log.traceId && availableTraceIds.has(log.traceId)
        ? (
            <TraceLink
              traceId={log.traceId}
              spanId={log.spanId}
              shortened
              onNavigate={onNavigateToTrace}
            />
          )
        : (
            <span className="cell-mono cell-muted cell-trace" title={log.traceId ?? undefined}>
              {shortId(log.traceId)}
            </span>
          ),
    },
    {
      key: "actions",
      header: "Actions",
      width: "56px",
      minWidth: "56px",
      render: (log) => (
        <div
          className="structured-log-actions"
          onClick={(event) => event.stopPropagation()}
          onKeyDown={(event) => event.stopPropagation()}
        >
          <StructuredLogActions
            onViewDetails={() => setSelectedLog(log)}
            onViewMessage={() => setTextViewer({
              title: "Structured log message",
              value: log.body,
              format: "text",
            })}
            onViewJson={() => setTextViewer({
              title: `${log.eventName ?? "structured-log"}.json`,
              value: formatStructuredLogJson(log),
              format: "json",
            })}
          />
        </div>
      ),
    },
  ];

  const clearLogs = async (resourceName: string | null): Promise<void> => {
    setClearing(true);
    setClearStatus(null);
    try {
      await clearStructuredLogs(resourceName);
      setPausedSnapshot(null);
      setResource("all");
      setClearStatus({
        message: resourceName === null
          ? "Cleared all structured logs."
          : `Cleared structured logs for ${resourceName}.`,
        error: false,
      });
    } catch (error) {
      setClearStatus({ message: `Could not clear structured logs: ${String(error)}`, error: true });
    } finally {
      setClearing(false);
    }
  };

  return (
    <Page className="structured-logs" aria-labelledby="deck-page-structured-logs-title">
      <PageHeader>
        <PageHeading>
          <PageTitle id="deck-page-structured-logs-title">Structured Logs</PageTitle>
          <PageSubtitle>
            {displayedTelemetry
              ? `${displayedTelemetry.logCount.toLocaleString()} total · showing ${filtered.length}${pausedSnapshot === null ? "" : " · paused"}`
              : "Loading…"}
          </PageSubtitle>
        </PageHeading>
      </PageHeader>

      <PageToolbar ariaLabel="Structured log tools" className="structured-logs__toolbar">
        <SearchBox value={query} onChange={setQuery} placeholder="Filter messages…" />
        <Select
          ariaLabel="Resource"
          className="structured-logs__resource-select"
          fieldClassName="structured-logs__resource-field"
          options={resourceOptions}
          value={selectedResource}
          onValueChange={setResource}
        />
        <Select
          ariaLabel="Severity"
          className="structured-logs__severity-select"
          fieldClassName="structured-logs__severity-field"
          options={SEVERITY_OPTIONS}
          value={severity}
          onValueChange={setSeverity}
        />
        <div className="page__header-spacer" />
        <Switch
          className="structured-logs__pause"
          label="Pause incoming data"
          checked={pausedSnapshot !== null}
          disabled={telemetry === null}
          onCheckedChange={(checked) => setPausedSnapshot(checked ? telemetry : null)}
        />
        <CommandMenu
          ariaLabel="Clear structured logs"
          triggerContent="Clear"
          triggerIcon={<NamedIcon name="Delete" size={16} />}
          placement="below-end"
          entries={[
            {
              id: "clear-all",
              label: "Clear all resources",
              icon: <NamedIcon name="BoxMultiple" size={16} />,
              tone: "danger",
              disabled: clearing || displayedTelemetry === null || displayedTelemetry.logCount === 0,
              onSelect: () => void clearLogs(null),
            },
            {
              id: "clear-resource",
              label: selectedResource === "all" ? "Clear selected resource" : `Clear ${selectedResource}`,
              icon: <NamedIcon name="CheckmarkCircle" size={16} />,
              tone: "danger",
              disabled: clearing || selectedResource === "all",
              onSelect: () => void clearLogs(selectedResource),
            },
          ]}
        />
      </PageToolbar>

      <PageBody>
        <DataTable
          columns={columns}
          rows={filtered}
          rowKey={logKey}
          onRowClick={setSelectedLog}
          isSelected={(log) => selectedLog !== null && logKey(log) === logKey(selectedLog)}
          emptyMessage={telemetry === null
            ? "Waiting for telemetry…"
            : logs.length === 0
              ? "No structured logs."
              : "No logs match your filter."}
        />
      </PageBody>

      {clearStatus ? (
        <div className="toast" role="status" aria-live="polite">
          <span className={`state__dot ${clearStatus.error ? "error" : "success"}`} />
          {clearStatus.message}
        </div>
      ) : null}

      {selectedLog ? (
        <StructuredLogDetailsDrawer
          log={selectedLog}
          onClose={() => setSelectedLog(null)}
          onViewMessage={() => setTextViewer({
            title: "Structured log message",
            value: selectedLog.body,
            format: "text",
          })}
          onViewJson={() => setTextViewer({
            title: `${selectedLog.eventName ?? "structured-log"}.json`,
            value: formatStructuredLogJson(selectedLog),
            format: "json",
          })}
          onNavigateToTrace={onNavigateToTrace}
          canNavigateToTrace={selectedLog.traceId !== null && availableTraceIds.has(selectedLog.traceId)}
        />
      ) : null}

      <TextViewerDialog request={textViewer} onClose={() => setTextViewer(null)} />
    </Page>
  );
}
