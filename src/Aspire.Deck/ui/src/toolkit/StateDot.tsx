export type StateTone = "success" | "info" | "warning" | "error" | "neutral";

export function getStateTone(state: string | null, stateStyle: string | null, health: string | null): StateTone {
  const normalizedState = (state ?? "").trim();

  if (normalizedState === "Finished" || normalizedState === "Exited") {
    return "info";
  }
  if (normalizedState === "FailedToStart") {
    return "error";
  }
  if (["Starting", "Building", "Waiting", "Stopping", "NotStarted", "Unknown", ""].includes(normalizedState)) {
    return "info";
  }
  if (normalizedState === "RuntimeUnhealthy") {
    return "warning";
  }
  if (stateStyle === "success" || stateStyle === "info" || stateStyle === "warning" || stateStyle === "error") {
    return stateStyle;
  }

  const normalizedHealth = (health ?? "").toLowerCase();
  if (normalizedHealth === "unhealthy" || normalizedHealth === "degraded") {
    return "warning";
  }

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
  const tone = getStateTone(state, stateStyle, health ?? null);
  return (
    <span className="state">
      <span className={`state__dot ${tone}`} title={state ?? "Unknown"} />
      <span>{state ?? "Unknown"}</span>
      {health ? <span className="state__health">· {health}</span> : null}
    </span>
  );
}
