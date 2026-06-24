// Renders a colored status dot for a resource, plus an optional health label.
//
// The color mirrors the Aspire dashboard's ResourceStateViewModel.GetStateIcon
// cascade (src/Aspire.Dashboard/Model/ResourceStateViewModel.cs) so Deck matches
// the dashboard. The AppHost leaves `stateStyle` empty for most states (e.g. a
// plain "Running" resource), so coloring purely from `stateStyle` would render
// Running as neutral/grey. Instead we classify by the known state text first and
// only fall back to `stateStyle`, then health, then a green "running & healthy"
// default — matching the dashboard's priority order.

type DotClass = "success" | "info" | "warning" | "error" | "neutral";

// State text values come from KnownResourceState
// (src/Aspire.Dashboard/Model/KnownResourceState.cs).
function stateDotClass(state: string | null, stateStyle: string | null, health: string | null): DotClass {
  const s = (state ?? "").trim();

  // Stopped states. (The dashboard shows non-zero exit codes as error, but the
  // resource snapshot Deck consumes doesn't surface the exit code, so completed
  // processes use info like the dashboard's normal case.)
  if (s === "Finished" || s === "Exited") {
    return "info";
  }
  if (s === "FailedToStart") {
    return "error";
  }

  // Transitory, not-started, and unknown states.
  if (s === "Starting" || s === "Building" || s === "Waiting" || s === "Stopping") {
    return "info";
  }
  if (s === "NotStarted" || s === "Unknown") {
    return "info";
  }

  // Runtime reported as unhealthy.
  if (s === "RuntimeUnhealthy") {
    return "warning";
  }

  // No state yet.
  if (s === "") {
    return "info";
  }

  // Explicit style from the AppHost (e.g. a parameter's ValueMissing -> warning).
  if (stateStyle === "success" || stateStyle === "info" || stateStyle === "warning" || stateStyle === "error") {
    return stateStyle;
  }

  // A running resource whose health checks are failing.
  const h = (health ?? "").toLowerCase();
  if (h === "unhealthy" || h === "degraded") {
    return "warning";
  }

  // Default: running (and healthy, or no health checks) -> green.
  return "success";
}

export function StateDot({
  stateStyle,
  state,
  health,
}: {
  stateStyle: string | null;
  state: string | null;
  health?: string | null;
}) {
  const dotClass = stateDotClass(state, stateStyle, health ?? null);
  return (
    <span className="state">
      <span className={`state__dot ${dotClass}`} title={state ?? "Unknown"} />
      <span>{state ?? "Unknown"}</span>
      {health ? <span className="state__health">· {health}</span> : null}
    </span>
  );
}
