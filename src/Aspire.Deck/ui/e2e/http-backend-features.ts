export const httpBackendFeatures = {
  "HTTP-CONFIG-001": "HTTP mode loads the application identity and version from the dashboard backend.",
  "HTTP-RESOURCES-001": "HTTP mode renders the resource snapshot returned by the dashboard backend.",
  "HTTP-MOCK-ISOLATION-001": "Explicit HTTP mode never falls back to the standalone mock backend.",
  "HTTP-FAILURE-001": "HTTP mode reports an unavailable dashboard backend without unhandled browser errors.",
  "HTTP-RECOVERY-001": "HTTP mode recovers application identity and resources when the dashboard backend returns.",
  "HTTP-COMMAND-001": "HTTP mode executes a live resource command through the dashboard backend.",
  "HTTP-EMPTY-TELEMETRY-001": "HTTP mode distinguishes a settled empty telemetry snapshot from loading.",
} as const;

export type HttpBackendFeatureId = keyof typeof httpBackendFeatures;

export function getMissingHttpBackendFeatures(
  covered: ReadonlySet<HttpBackendFeatureId>,
): HttpBackendFeatureId[] {
  return (Object.keys(httpBackendFeatures) as HttpBackendFeatureId[]).filter(
    (feature) => !covered.has(feature),
  );
}
