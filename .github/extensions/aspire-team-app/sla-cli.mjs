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

// Returns the report as a single JSON string rather than writing it, so the caller controls
// flushing (see the entrypoint) and tests can assert the payload without capturing stdout.
export async function main() {
  const report = await computeSlaReport();
  if (!report || !report.authenticated) {
    // Emit a well-formed, clearly-unauthenticated payload so the workflow can bail quietly
    // instead of tripping on a parse error.
    return JSON.stringify({
      authenticated: false,
      message: report?.message ?? "not authenticated",
      generatedAt: new Date().toISOString(),
      sla: null,
      externalOpenPrs: [],
    });
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
  return JSON.stringify(payload);
}

// process.exit() abandons any not-yet-flushed writes to a piped (i.e. async, non-TTY) stdout,
// which can truncate the JSON mid-document when a consumer reads it through a pipe. So wait for
// the write to drain via its completion callback, then force the exit from inside that callback.
// Forcing the exit (rather than relying on a natural event-loop drain) keeps the CLI from
// hanging if any background handle isn't unref'd. See
// https://nodejs.org/api/process.html#processexitcode — "process.exit() will force the process
// to exit as quickly as possible even if there are still asynchronous operations pending that
// have not yet completed fully, including I/O operations to process.stdout and process.stderr."
function writeThenExit(stream, text, code) {
  stream.write(text, () => process.exit(code));
}

// Only self-execute when invoked directly (`node sla-cli.mjs`). The extension validator
// dynamically imports every .mjs to smoke-test it; without this guard the import would run
// main() and exit the process, aborting validation of the whole extension.
const invokedDirectly = import.meta.url === pathToFileURL(process.argv[1] || "").href;
if (invokedDirectly) {
  main()
    .then((json) => writeThenExit(process.stdout, json + "\n", 0))
    .catch((err) => {
      writeThenExit(process.stderr, "sla-cli failed: " + (err?.stack || err?.message || String(err)) + "\n", 1);
    });
}
