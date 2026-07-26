import { expect, test } from "@playwright/test";
import {
  dashboardParityFeatures,
  getReactParityGaps,
  getUncoveredLegacyFeatures,
  type DashboardArea,
  type ReactParityStatus,
} from "./dashboard-parity-features";
import { parseCitations, resolveCitation } from "./citation-resolver";

const areas: readonly DashboardArea[] = [
  "shell",
  "resources",
  "parameters",
  "commands",
  "console",
  "structured-logs",
  "traces",
  "metrics",
];

test("dashboard migration parity ledger is internally consistent", async ({}) => {
  const ids = dashboardParityFeatures.map((feature) => feature.id);
  expect(new Set(ids).size, "Feature IDs must be unique.").toBe(ids.length);
  expect(ids, "The ledger must remain extensive enough to represent the legacy dashboard.").toHaveLength(157);

  for (const area of areas) {
    expect(
      dashboardParityFeatures.some((feature) => feature.area === area),
      `The '${area}' area must contain features.`,
    ).toBe(true);
  }

  for (const feature of dashboardParityFeatures) {
    expect(feature.description.trim(), `${feature.id} must have a description.`).not.toBe("");
    expect(feature.legacyRoute.startsWith("/"), `${feature.id} must have a legacy route.`).toBe(true);
    if (feature.reactStatus === "covered" || feature.reactStatus === "partial") {
      expect(feature.currentCoverage, `${feature.id} must cite its current React coverage.`).not.toBeNull();
    }
  }
});

test("every parity ledger coverage citation resolves to a real test", async ({}, testInfo) => {
  const failures: string[] = [];
  const kinds = new Map<string, number>();

  for (const feature of dashboardParityFeatures) {
    const citations = parseCitations(feature.currentCoverage);

    if (feature.reactStatus === "covered" || feature.reactStatus === "partial") {
      // A claim of coverage with nothing behind it is exactly the failure mode this test exists for.
      if (citations.length === 0) {
        failures.push(`${feature.id}: marked '${feature.reactStatus}' but cites no coverage.`);
        continue;
      }
    }

    let hasTestCitation = false;
    for (const citation of citations) {
      const resolution = resolveCitation(citation);
      kinds.set(resolution.kind ?? "unknown", (kinds.get(resolution.kind ?? "unknown") ?? 0) + 1);

      if (!resolution.resolved) {
        failures.push(`${feature.id}: citation '${citation}' ${resolution.detail}.`);
        continue;
      }

      // A commit SHA records a deliberate upstream removal; it is provenance, not coverage.
      if (resolution.kind !== "commit") {
        hasTestCitation = true;
      }
    }

    if ((feature.reactStatus === "covered" || feature.reactStatus === "partial") && !hasTestCitation) {
      failures.push(
        `${feature.id}: marked '${feature.reactStatus}' but every citation is a commit reference rather than a test.`,
      );
    }
  }

  await testInfo.attach("citation-kinds.txt", {
    body: Buffer.from([...kinds].map(([kind, count]) => `${kind}: ${count}`).join("\n")),
    contentType: "text/plain",
  });

  expect(failures, `Unresolvable parity ledger citations:\n${failures.join("\n")}`).toEqual([]);
});

test("dashboard migration parity ledger is complete and reviewable", async ({}, testInfo) => {

  const report = buildReport();
  await testInfo.attach("dashboard-parity-ledger.md", {
    body: Buffer.from(report),
    contentType: "text/markdown",
  });
  expect(report).toMatchSnapshot("dashboard-parity-ledger.md");
});

function buildReport(): string {
  const statusCounts = countBy(dashboardParityFeatures.map((feature) => feature.reactStatus));
  const lines = [
    "# Dashboard migration parity ledger",
    "",
    `- Total legacy features: ${dashboardParityFeatures.length}`,
    `- React covered: ${statusCounts.covered ?? 0}`,
    `- React partial: ${statusCounts.partial ?? 0}`,
    `- React missing: ${statusCounts.missing ?? 0}`,
    `- Legacy black-box scenarios pending: ${getUncoveredLegacyFeatures().length}`,
    `- React parity gaps: ${getReactParityGaps().length}`,
    "",
    "| ID | Area | Legacy route | Legacy test | React | Current coverage | Behavior |",
    "| --- | --- | --- | --- | --- | --- | --- |",
  ];

  for (const feature of dashboardParityFeatures) {
    lines.push(
      `| ${feature.id} | ${feature.area} | \`${feature.legacyRoute}\` | ${formatLegacyCoverage(feature.legacyScenario)} | ${feature.reactStatus} | ${feature.currentCoverage ?? "-"} | ${feature.description} |`,
    );
  }

  lines.push("");
  return lines.join("\n");
}

function formatLegacyCoverage(coverage: (typeof dashboardParityFeatures)[number]["legacyScenario"]): string {
  if (coverage === "not-applicable") {
    return "N/A (React enhancement)";
  }
  if (coverage === "removed") {
    return "Removed upstream";
  }

  return coverage ?? "PENDING";
}

function countBy(values: readonly ReactParityStatus[]): Partial<Record<ReactParityStatus, number>> {
  const counts: Partial<Record<ReactParityStatus, number>> = {};
  for (const value of values) {
    counts[value] = (counts[value] ?? 0) + 1;
  }
  return counts;
}
