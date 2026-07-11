export const stressFeatures = {
  "STRESS-CONFIG-001": "The React shell loads the running Stress AppHost identity and version.",
  "STRESS-RESOURCES-001": "The resource table renders the live Stress resource inventory without mock fallback.",
  "STRESS-VISIBILITY-001": "Hidden resources and parameters stay out of the resource table.",
  "STRESS-DETAILS-001": "A live Stress resource opens its populated details drawer.",
  "STRESS-SECRETS-001": "Sensitive Stress resource properties remain masked by default.",
  "STRESS-PARAMETERS-001": "Live Stress parameters render missing values and keep secret values masked.",
  "STRESS-NAVIGATION-001": "Every dashboard page remains reachable while connected to the live backend.",
  "STRESS-EMPTY-TELEMETRY-001": "The live dashboard distinguishes settled empty telemetry from loading.",
  "STRESS-RESPONSIVE-001": "The live resource workflow remains contained and usable at a mobile viewport.",
  "STRESS-VISUAL-001": "Desktop and mobile evidence is captured for visual review.",
} as const;

export type StressFeatureId = keyof typeof stressFeatures;

export function getMissingStressFeatures(covered: ReadonlySet<StressFeatureId>): StressFeatureId[] {
  return (Object.keys(stressFeatures) as StressFeatureId[]).filter((feature) => !covered.has(feature));
}
