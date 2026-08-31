// Headless SLA report emitter for the hourly Teams notifier workflow.
//
// Runs OUTSIDE the canvas: it computes a fresh dashboard via forceRefresh() (which does
// NOT start the loopback HTTP server) and prints a single JSON document describing the
// aspire-1p review SLA state plus every open external PR. The workflow diffs this against
// its own tracker file to decide what to announce.
//
// Usage:  node sla-cli.mjs
// Output (stdout): one JSON object; see shape below. Non-JSON diagnostics go to stderr.

import { forceRefresh } from "./server.mjs";

function leanCard(c) {
  return {
    repo: c.pr.repository,
    number: c.pr.number,
    title: c.pr.title,
    url: c.pr.url,
    author: c.pr.author,
    state: c.sla?.state ?? null,
    firstQualifiedAt: c.sla?.firstQualifiedAt ?? null,
    warnAt: c.sla?.warnAt ?? null,
    deadlineAt: c.sla?.deadlineAt ?? null,
  };
}

async function main() {
  const { dashboard } = await forceRefresh();
  if (!dashboard || !dashboard.authenticated) {
    // Emit a well-formed, clearly-unauthenticated payload so the workflow can bail quietly
    // instead of tripping on a parse error.
    process.stdout.write(JSON.stringify({
      authenticated: false,
      message: dashboard?.message ?? "not authenticated",
      generatedAt: new Date().toISOString(),
      sla: null,
      externalOpenPrs: [],
    }) + "\n");
    return;
  }

  const sla = dashboard.sla ?? null;
  const payload = {
    authenticated: true,
    viewer: dashboard.viewer,
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
    externalOpenPrs: dashboard.externalOpenPrs ?? [],
  };
  process.stdout.write(JSON.stringify(payload) + "\n");
}

main()
  .then(() => {
    // Background pollers/timers are unref'd, but exit explicitly so the CLI never hangs.
    process.exit(0);
  })
  .catch((err) => {
    process.stderr.write("sla-cli failed: " + (err?.stack || err?.message || String(err)) + "\n");
    process.exit(1);
  });
