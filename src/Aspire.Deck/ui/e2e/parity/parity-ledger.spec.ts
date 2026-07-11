import { expect, test } from "@playwright/test";
import {
  dashboardParityFeatures,
  getReactParityGaps,
  getUncoveredLegacyFeatures,
  type DashboardArea,
  type ReactParityStatus,
} from "./dashboard-parity-features";

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

test("dashboard migration parity ledger is complete and reviewable", async ({}, testInfo) => {
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
      `| ${feature.id} | ${feature.area} | \`${feature.legacyRoute}\` | ${feature.legacyScenario ?? "PENDING"} | ${feature.reactStatus} | ${feature.currentCoverage ?? "-"} | ${feature.description} |`,
    );
  }

  lines.push("");
  return lines.join("\n");
}

function countBy(values: readonly ReactParityStatus[]): Partial<Record<ReactParityStatus, number>> {
  const counts: Partial<Record<ReactParityStatus, number>> = {};
  for (const value of values) {
    counts[value] = (counts[value] ?? 0) + 1;
  }
  return counts;
}
