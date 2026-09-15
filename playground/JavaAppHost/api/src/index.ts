/**
 * Import the OpenTelemetry instrumentation setup first, before any other modules.
 * This ensures all subsequent imports are automatically instrumented for
 * distributed tracing, metrics, and logging in the Aspire dashboard.
 */
import "./instrumentation.ts";
import express from "express";
import { existsSync } from "fs";
import { join } from "path";

const app = express();
const port = process.env.PORT || 5000;

// Injected by the AppHost's withReference(forecast). Service discovery names the variable after the
// resource and endpoint, so the API never has to know which port Aspire handed the Java service.
const forecastServiceUrl = process.env.services__forecast__http__0;

/** Proxies the 5-day forecast produced by the Java service. */
app.get("/api/weatherforecast", async (_req, res) => {
  if (!forecastServiceUrl) {
    res.status(503).json({ error: "The forecast service is not configured. Run this API through the AppHost." });
    return;
  }

  try {
    const response = await fetch(new URL("/forecast", forecastServiceUrl));

    if (!response.ok) {
      res.status(502).json({ error: `Forecast service returned ${response.status}` });
      return;
    }

    res.json(await response.json());
  } catch (error) {
    res.status(502).json({ error: `Forecast service is unreachable: ${error}` });
  }
});

app.get("/health", (_req, res) => {
  res.send("Healthy");
});

// Serve static files from the "static" directory if it exists (used in publish/deploy mode
// when the frontend's build output is bundled into this container via publishWithContainerFiles)
const staticDir = join(import.meta.dirname, "..", "static");
if (existsSync(staticDir)) {
  app.use(express.static(staticDir));
}

app.listen(port, () => {
  console.log(`API server listening on port ${port}`);
});
