// Renders a colored status dot derived from the resource `stateStyle` plus an
// optional health label. The contract defines stateStyle as one of
// "success" | "info" | "warning" | "error" | null.

const STYLE_CLASSES = new Set(["success", "info", "warning", "error"]);

export function StateDot({
  stateStyle,
  state,
  health,
}: {
  stateStyle: string | null;
  state: string | null;
  health?: string | null;
}) {
  const dotClass = stateStyle && STYLE_CLASSES.has(stateStyle) ? stateStyle : "neutral";
  return (
    <span className="state">
      <span className={`state__dot ${dotClass}`} title={state ?? "Unknown"} />
      <span>{state ?? "Unknown"}</span>
      {health ? <span className="state__health">· {health}</span> : null}
    </span>
  );
}
