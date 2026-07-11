export const stressFeatures = {
  "STRESS-CONFIG-001": "The React shell loads the running Stress AppHost identity and version.",
  "STRESS-RESOURCES-001": "The resource table renders the live Stress resource inventory without mock fallback.",
  "STRESS-VISIBILITY-001": "Hidden resources and parameters stay out of the resource table.",
  "STRESS-DETAILS-001": "A live Stress resource opens its populated details drawer.",
  "STRESS-RESOURCE-ICON-001": "Custom Stress resource icon names render instead of resource-type fallbacks.",
  "STRESS-COMMAND-ICON-001": "Custom Stress command icon names and filled variants render in command surfaces.",
  "STRESS-COMMAND-EXECUTE-001": "A Stress resource command executes through the dashboard HTTP backend.",
  "STRESS-COMMAND-ARGUMENTS-001": "A live Stress command round-trips text, number, boolean, choice, and secret inputs.",
  "STRESS-SECRETS-001": "Sensitive Stress resource properties remain masked by default.",
  "STRESS-PARAMETERS-001": "Live Stress parameters render missing values and keep secret values masked.",
  "STRESS-CONSOLE-001": "The React console renders the live backlog streamed by the Stress dashboard backend.",
  "STRESS-STRUCTURED-LOGS-001": "The React structured-log page replays Stress telemetry and updates after the telemetry service starts again.",
  "STRESS-STRUCTURED-LOG-RESOURCE-001": "The React structured-log resource selector constrains every visible Stress row.",
  "STRESS-STRUCTURED-LOG-PAUSE-001": "The React structured-log page freezes live Stress records and catches up on resume.",
  "STRESS-STRUCTURED-LOG-CLEAR-001": "The React structured-log clear menu removes a selected Stress resource and then all logs.",
  "STRESS-STRUCTURED-LOG-DETAILS-001": "A live Stress log opens complete details and its JSON visualizer.",
  "STRESS-TRACES-001": "The React trace page replays live Stress spans and opens an exact span detail route.",
  "STRESS-NAVIGATION-001": "Every dashboard page remains reachable while connected to the live backend.",
  "STRESS-EMPTY-METRICS-001": "The live dashboard distinguishes settled empty metrics from loading.",
  "STRESS-RESPONSIVE-001": "The live resource workflow remains contained and usable at a mobile viewport.",
  "STRESS-VISUAL-001": "Desktop and mobile evidence is captured for visual review.",
} as const;

export type StressFeatureId = keyof typeof stressFeatures;

export function getMissingStressFeatures(covered: ReadonlySet<StressFeatureId>): StressFeatureId[] {
  return (Object.keys(stressFeatures) as StressFeatureId[]).filter((feature) => !covered.has(feature));
}
