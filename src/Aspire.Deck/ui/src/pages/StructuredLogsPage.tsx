import { useMemo, useState } from "react";
import type { LogRecordSummary, TelemetrySummary } from "../api/types";
import { useTelemetry } from "../lib/useDeckEvent";
import { dateFromUnixNano, formatTimeWithMillis } from "../lib/format";
import {
  Badge,
  DataTable,
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
  type Column,
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

export function StructuredLogsPage() {
  const telemetry = useTelemetry();
  const [query, setQuery] = useState("");
  const [severity, setSeverity] = useState("All");
  const [resource, setResource] = useState("all");
  const [pausedSnapshot, setPausedSnapshot] = useState<TelemetrySummary | null>(null);

  const displayedTelemetry = pausedSnapshot ?? telemetry;
  const logs = displayedTelemetry?.recentLogs ?? [];
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
          (log.resourceName ?? "").toLowerCase().includes(trimmed)
        );
      }
      return true;
    });
  }, [logs, query, selectedResource, severity]);

  const columns: Column<LogRecordSummary>[] = [
    {
      key: "time",
      header: "Time",
      width: "150px",
      render: (l) => (
        <span className="cell-mono cell-muted cell-time">{formatTimeWithMillis(dateFromUnixNano(l.timeUnixNano))}</span>
      ),
    },
    {
      key: "severity",
      header: "Severity",
      width: "120px",
      render: (l) => <Badge tone={severityTone(l.severity)}>{l.severity ?? "Unknown"}</Badge>,
    },
    {
      key: "resource",
      header: "Resource",
      width: "150px",
      render: (l) => <span className="cell-muted">{l.resourceName ?? "—"}</span>,
    },
    {
      key: "body",
      header: "Message",
      render: (l) => <span className="cell-mono cell-log-message">{l.body}</span>,
    },
  ];

  return (
    <Page aria-labelledby="deck-page-structured-logs-title">
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

      <PageToolbar ariaLabel="Structured log tools">
        <SearchBox value={query} onChange={setQuery} placeholder="Filter messages…" />
        <Select
          ariaLabel="Resource"
          options={resourceOptions}
          value={selectedResource}
          onValueChange={setResource}
        />
        <Select
          ariaLabel="Severity"
          options={SEVERITY_OPTIONS}
          value={severity}
          onValueChange={setSeverity}
        />
        <div className="page__header-spacer" />
        <Switch
          label="Pause incoming data"
          checked={pausedSnapshot !== null}
          disabled={telemetry === null}
          onCheckedChange={(checked) => setPausedSnapshot(checked ? telemetry : null)}
        />
      </PageToolbar>

      <PageBody>
        <DataTable
          columns={columns}
          rows={filtered}
          rowKey={(l) => `${l.resourceName ?? ""}-${l.timeUnixNano}-${l.spanId ?? ""}-${l.severityNumber}-${l.body}`}
          emptyMessage={telemetry ? "No logs match your filter." : "Waiting for telemetry…"}
        />
      </PageBody>
    </Page>
  );
}
