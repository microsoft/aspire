// Headless SLA report emitter for the hourly Teams notifier workflow.
//
// Runs OUTSIDE the canvas: it computes a fresh review-mode SLA snapshot via computeSlaReport()
// (which does NOT start the loopback HTTP server, and does NOT depend on / mutate whatever mode
// the user last viewed in the canvas) and prints a single JSON document describing the review
// SLA state plus every open external PR. The workflow diffs this against its own tracker file to
// decide what to announce.
//
// Usage:  node sla-cli.mjs
// Output (stdout): one JSON object; see shape below. Non-JSON diagnostics go to stderr.

import { pathToFileURL } from "node:url";
import { computeSlaReport } from "./server.mjs";

function leanCard(c) {
  return {
    repo: c.pr.repository,
    number: c.pr.number,
    // The CLI output is consumed by a deterministic workflow (not injected into an agent's
    // reasoning), so it may carry display text like the PR title. The agent-facing sla_report
    // action deliberately omits free-text fields — see extension.mjs.
    title: c.pr.title,
    url: c.pr.url,
    author: c.pr.author,
    state: c.sla?.state ?? null,
    firstQualifiedAt: c.sla?.firstQualifiedAt ?? null,
    warnAt: c.sla?.warnAt ?? null,
    deadlineAt: c.sla?.deadlineAt ?? null,
  };
}

export async function main() {
  const report = await computeSlaReport();
  if (!report || !report.authenticated) {
    // Emit a well-formed, clearly-unauthenticated payload so the workflow can bail quietly
    // instead of tripping on a parse error.
    process.stdout.write(JSON.stringify({
      authenticated: false,
      message: report?.message ?? "not authenticated",
      generatedAt: new Date().toISOString(),
      sla: null,
      externalOpenPrs: [],
    }) + "\n");
    return;
  }

  const sla = report.sla ?? null;
  const payload = {
    authenticated: true,
    viewer: report.viewer,
    generatedAt: new Date().toISOString(),
    sla: sla
      ? {
          repos: sla.repos,
          budgetHours: sla.budgetHours,
          warnHours: sla.warnHours,
          tz: sla.tz,
          total: sla.total,
          okCount: sla.okCount,
          breached: (sla.breached ?? []).map(leanCard),
          approaching: (sla.approaching ?? []).map(leanCard),
        }
      : null,
    externalOpenPrs: report.externalOpenPrs ?? [],
  };
  process.stdout.write(JSON.stringify(payload) + "\n");
}

// Only self-execute when invoked directly (`node sla-cli.mjs`). The extension validator
// dynamically imports every .mjs to smoke-test it; without this guard the import would run
// main() and call process.exit(), aborting validation of the whole extension.
const invokedDirectly = import.meta.url === pathToFileURL(process.argv[1] || "").href;
if (invokedDirectly) {
  main()
    .then(() => {
      // Background pollers/timers are unref'd, but exit explicitly so the CLI never hangs.
      process.exit(0);
    })
    .catch((err) => {
      process.stderr.write("sla-cli failed: " + (err?.stack || err?.message || String(err)) + "\n");
      process.exit(1);
    });
}
