import type { ConnectionState, ConnectionTarget } from "../api/types";

const LABELS: Record<ConnectionTarget, string> = {
  resourceService: "Resources",
  otlpGrpc: "OTLP gRPC",
  otlpHttp: "OTLP HTTP",
};

const STATE_LABELS: Record<ConnectionState, string> = {
  connecting: "Connecting…",
  connected: "Connected",
  disconnected: "Disconnected",
  error: "Error",
};

export function ConnectionPill({
  target,
  state,
}: {
  target: ConnectionTarget;
  state: ConnectionState;
}) {
  return (
    <span className="pill" title={`${LABELS[target]}: ${STATE_LABELS[state]}`}>
      <span className={`pill__dot ${state}`} />
      {LABELS[target]}
    </span>
  );
}
