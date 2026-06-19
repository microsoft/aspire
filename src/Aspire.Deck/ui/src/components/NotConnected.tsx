import type { DeckConfig } from "../api/types";

// Shown when Deck has no AppHost to talk to: either no resource-service URL was
// configured (standalone launch with no AppHost) or the configured endpoint is
// unreachable. Without this, the app shell renders with an empty table and a
// perpetual "Connecting…" message, which reads as a blank/broken window.
export function NotConnected({
  config,
  state,
}: {
  config: DeckConfig | null;
  state: "disconnected" | "error";
}) {
  const hasResourceService = Boolean(config?.resourceServiceUrl);

  const title = state === "error" || hasResourceService
    ? "Can't reach the AppHost"
    : "No AppHost connected";

  return (
    <div className="splash">
      <div className="splash__card">
        <div className="splash__glyph" aria-hidden="true">
          <RadarGlyph />
        </div>
        <h1 className="splash__title">{title}</h1>
        <p className="splash__lead">
          {hasResourceService
            ? "Aspire Deck is configured to connect to a resource service, but no response is coming back. Make sure the AppHost is running and the endpoint below is reachable."
            : "Aspire Deck isn't connected to a running AppHost yet, so there are no resources or telemetry to show. Start your app with Deck attached, or point Deck at a running resource service."}
        </p>

        <div className="splash__hint">
          <div className="splash__hint-title">Start your app with Deck</div>
          <code className="splash__code">aspire run --deck</code>
          <div className="splash__hint-sub">
            Or launch Deck against an already-running AppHost with{" "}
            <code>--resource-service-url</code>.
          </div>
        </div>

        <dl className="splash__endpoints">
          <Endpoint label="Resource service" value={config?.resourceServiceUrl} fallback="not configured" />
          <Endpoint label="OTLP gRPC" value={config?.otlpGrpcUrl} fallback="—" />
          <Endpoint label="OTLP HTTP" value={config?.otlpHttpUrl} fallback="—" />
        </dl>
      </div>
    </div>
  );
}

function Endpoint({ label, value, fallback }: { label: string; value: string | null | undefined; fallback: string }) {
  return (
    <div className="splash__endpoint">
      <dt>{label}</dt>
      <dd className={value ? "" : "splash__endpoint--muted"}>{value ?? fallback}</dd>
    </div>
  );
}

function RadarGlyph() {
  return (
    <svg width="56" height="56" viewBox="0 0 56 56" fill="none" xmlns="http://www.w3.org/2000/svg">
      <circle cx="28" cy="28" r="24" stroke="currentColor" strokeOpacity="0.25" strokeWidth="1.5" />
      <circle cx="28" cy="28" r="15" stroke="currentColor" strokeOpacity="0.35" strokeWidth="1.5" />
      <circle cx="28" cy="28" r="6" stroke="currentColor" strokeOpacity="0.5" strokeWidth="1.5" />
      <path d="M28 28 L28 4" stroke="currentColor" strokeWidth="2" strokeLinecap="round" className="splash__sweep" />
      <circle cx="28" cy="28" r="2.5" fill="currentColor" />
    </svg>
  );
}
