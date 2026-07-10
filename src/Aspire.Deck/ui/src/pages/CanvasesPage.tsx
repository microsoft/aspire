import { useEffect, useMemo, useState } from "react";
import type { CanvasManifest } from "../api/types";
import { listCanvases } from "../api/deck";
import { attachCanvasBridge } from "../lib/canvasBridge";
import {
  BackIcon,
  CanvasHost,
  CanvasIcon,
  EmptyState,
  Page,
  PageBody,
  PageHeader,
  PageHeading,
  PageSubtitle,
  PageTitle,
} from "../toolkit";

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

  if (open) {
    return (
      <Page aria-labelledby="deck-page-canvas-title">
        <PageHeader>
          <button className="icon-btn" onClick={() => setOpenId(null)} aria-label="Back to canvases">
            <BackIcon size={17} />
          </button>
          <PageHeading>
            <PageTitle id="deck-page-canvas-title">
              {open.icon ? `${open.icon} ` : ""}
              {open.title}
            </PageTitle>
            {open.description ? <PageSubtitle>{open.description}</PageSubtitle> : null}
          </PageHeading>
        </PageHeader>
        <PageBody>
          <CanvasHost
            src={resolveCanvasUrl(open.url)}
            title={open.title}
            connect={attachCanvasBridge}
          />
        </PageBody>
      </Page>
    );
  }

  return (
    <Page aria-labelledby="deck-page-canvases-title">
      <PageHeader>
        <PageHeading>
          <PageTitle id="deck-page-canvases-title">Canvases</PageTitle>
          <PageSubtitle>Custom interactive panels for your app</PageSubtitle>
        </PageHeading>
      </PageHeader>

      <PageBody>
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
      </PageBody>
    </Page>
  );
}
