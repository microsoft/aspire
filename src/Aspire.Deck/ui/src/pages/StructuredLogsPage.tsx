import { useMemo, useState } from "react";
import type { LogRecordSummary } from "../api/types";
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
  type Column,
} from "../toolkit";

const SEVERITIES = ["All", "Trace", "Debug", "Information", "Warning", "Error", "Critical"];

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

  const logs = telemetry?.recentLogs ?? [];

  const filtered = useMemo(() => {
    const trimmed = query.trim().toLowerCase();
    return logs.filter((log) => {
      if (severity !== "All" && log.severity !== severity) {
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
  }, [logs, query, severity]);

  const columns: Column<LogRecordSummary>[] = [
    {
      key: "time",
      header: "Time",
      width: "120px",
      render: (l) => (
        <span className="cell-mono cell-muted">{formatTimeWithMillis(dateFromUnixNano(l.timeUnixNano))}</span>
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
      render: (l) => <span className="cell-mono">{l.body}</span>,
    },
  ];

  return (
    <Page aria-labelledby="deck-page-structured-logs-title">
      <PageHeader>
        <PageHeading>
          <PageTitle id="deck-page-structured-logs-title">Structured Logs</PageTitle>
          <PageSubtitle>
            {telemetry ? `${telemetry.logCount.toLocaleString()} total · showing ${filtered.length}` : "Loading…"}
          </PageSubtitle>
        </PageHeading>
      </PageHeader>

      <PageToolbar ariaLabel="Structured log tools">
        <SearchBox value={query} onChange={setQuery} placeholder="Filter messages…" />
        <select className="select" value={severity} onChange={(e) => setSeverity(e.target.value)}>
          {SEVERITIES.map((s) => (
            <option key={s} value={s}>
              {s}
            </option>
          ))}
        </select>
      </PageToolbar>

      <PageBody>
        <DataTable
          columns={columns}
          rows={filtered}
          rowKey={(l) => `${l.timeUnixNano}-${l.spanId ?? ""}-${l.body}`}
          emptyMessage={telemetry ? "No logs match your filter." : "Waiting for telemetry…"}
        />
      </PageBody>
    </Page>
  );
}
