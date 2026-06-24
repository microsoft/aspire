import { useEffect, useMemo, useRef, useState } from "react";
import type { CanvasManifest } from "../api/types";
import { listCanvases } from "../api/deck";
import { attachCanvasBridge } from "../lib/canvasBridge";
import { EmptyState } from "../components/EmptyState";
import { BackIcon, CanvasIcon } from "../components/Icons";

// Resolves a canvas url against the app base so it loads under both the native
// `canvas://` scheme (Tauri) and http (dev/preview). Absolute-scheme and
// protocol-relative urls are passed through unchanged.
function resolveCanvasUrl(url: string): string {
  if (/^[a-z]+:/i.test(url) || url.startsWith("//")) {
    return url;
  }
  const base = import.meta.env.BASE_URL ?? "/";
  return `${base}${url}`.replace(/([^:])\/\//g, "$1/");
}

export function CanvasesPage() {
  const [canvases, setCanvases] = useState<CanvasManifest[]>([]);
  const [loading, setLoading] = useState(true);
  const [openId, setOpenId] = useState<string | null>(null);
  const iframeRef = useRef<HTMLIFrameElement | null>(null);

  useEffect(() => {
    let cancelled = false;
    void listCanvases().then((result) => {
      if (!cancelled) {
        setCanvases(result);
        setLoading(false);
      }
    });
    return () => {
      cancelled = true;
    };
  }, []);

  const open = useMemo(() => canvases.find((c) => c.id === openId) ?? null, [canvases, openId]);

  // Bridge the host's data layer to the sandboxed canvas iframe while one is open.
  useEffect(() => {
    const iframe = iframeRef.current;
    if (!open || iframe === null) {
      return;
    }
    return attachCanvasBridge(iframe);
  }, [open]);

  if (open) {
    return (
      <div className="page">
        <div className="page__header">
          <button className="icon-btn" onClick={() => setOpenId(null)} aria-label="Back to canvases">
            <BackIcon size={17} />
          </button>
          <div>
            <div className="page__title">
              {open.icon ? `${open.icon} ` : ""}
              {open.title}
            </div>
            {open.description ? <div className="page__subtitle">{open.description}</div> : null}
          </div>
        </div>
        <div className="page__body">
          <div className="canvas-viewer">
            <iframe
              ref={iframeRef}
              className="canvas-viewer__frame"
              src={resolveCanvasUrl(open.url)}
              title={open.title}
              sandbox="allow-scripts allow-same-origin"
            />
          </div>
        </div>
      </div>
    );
  }

  return (
    <div className="page">
      <div className="page__header">
        <div>
          <div className="page__title">Canvases</div>
          <div className="page__subtitle">Custom interactive panels for your app</div>
        </div>
      </div>

      <div className="page__body">
        {loading ? (
          <div className="center-fill">
            <div className="spinner" />
          </div>
        ) : canvases.length === 0 ? (
          <EmptyState icon={<CanvasIcon size={26} />} title="No canvases yet">
            Canvases are sandboxed HTML panels that render custom dashboards alongside your app —
            think live order maps, build pipelines, or domain-specific visualizations. An agent
            skill (<code>deck-canvas</code>) can author one for you; manifests then appear here
            automatically.
          </EmptyState>
        ) : (
          <div className="canvas-grid">
            {canvases.map((canvas) => (
              <button key={canvas.id} className="canvas-card" onClick={() => setOpenId(canvas.id)}>
                <div className="canvas-card__icon">{canvas.icon ?? "🧩"}</div>
                <div className="canvas-card__title">{canvas.title}</div>
                {canvas.description ? <div className="canvas-card__desc">{canvas.description}</div> : null}
              </button>
            ))}
          </div>
        )}
      </div>
    </div>
  );
}
