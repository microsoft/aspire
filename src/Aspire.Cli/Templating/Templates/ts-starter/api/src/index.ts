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

/** Returns a random 5-day weather forecast as JSON. */
app.get("/api/weatherforecast", (_req, res) => {
  const summaries = ["Freezing", "Bracing", "Chilly", "Cool", "Mild", "Warm", "Balmy", "Hot", "Sweltering", "Scorching"];
  const forecasts = Array.from({ length: 5 }, (_, i) => {
    const temperatureC = Math.floor(Math.random() * 75) - 20;
    return {
      date: new Date(Date.now() + (i + 1) * 86400000).toISOString(),
      temperatureC,
      temperatureF: 32 + Math.trunc(temperatureC / 0.5556),
      summary: summaries[Math.floor(Math.random() * summaries.length)],
    };
  });
  res.json(forecasts);
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
