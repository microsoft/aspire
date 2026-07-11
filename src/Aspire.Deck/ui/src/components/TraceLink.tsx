import type { MouseEvent } from "react";
import { shortId } from "../lib/format";
import { dashboardRouteHref } from "../lib/routes";

export function TraceLink({
  traceId,
  spanId,
  shortened = false,
  onNavigate,
}: {
  traceId: string;
  spanId: string | null;
  shortened?: boolean;
  onNavigate: (traceId: string, spanId: string | null) => void;
}) {
  const displayId = shortened ? shortId(traceId) : traceId;
  const onClick = (event: MouseEvent<HTMLAnchorElement>): void => {
    event.stopPropagation();
    if (event.button !== 0 || event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) {
      return;
    }

    event.preventDefault();
    onNavigate(traceId, spanId);
  };

  return (
    <a
      className="telemetry-link cell-mono"
      href={dashboardRouteHref({ page: "traces", traceId, spanId: spanId ?? undefined })}
      aria-label={`Open trace ${shortId(traceId)}`}
      title={traceId}
      onClick={onClick}
    >
      {displayId}
    </a>
  );
}
