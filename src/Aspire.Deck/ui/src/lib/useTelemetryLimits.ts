import { useEffect, useState } from "react";
import { getConfig } from "../api/deck";
import { DEFAULT_TELEMETRY_LIMITS, type DeckTelemetryLimits } from "../api/types";

/**
 * Exposes the retention ceilings the dashboard is actually configured with.
 *
 * Client-side buffers must be sized from configuration rather than from a constant: the Blazor
 * dashboard keeps `Dashboard:Frontend:MaxConsoleLogCount` console lines and
 * `Dashboard:TelemetryLimits:*` telemetry records, so a hardcoded client cap silently truncates
 * data the operator deliberately configured the server to retain.
 *
 * Returns the server-side defaults until the config request resolves, and keeps them if it fails,
 * so a slow or unavailable config endpoint degrades to "correct for default configuration" rather
 * than to an empty view.
 */
export function useTelemetryLimits(): DeckTelemetryLimits {
  const [limits, setLimits] = useState<DeckTelemetryLimits>(DEFAULT_TELEMETRY_LIMITS);

  useEffect(() => {
    let active = true;

    void getConfig()
      .then((config) => {
        if (active && config.telemetryLimits) {
          setLimits(config.telemetryLimits);
        }
      })
      .catch(() => {
        // Keep the defaults; the caller has a usable value either way.
      });

    return () => {
      active = false;
    };
  }, []);

  return limits;
}
