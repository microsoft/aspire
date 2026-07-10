export const canvasFeatures = {
  "CANVAS-CATALOG-001": "The canvas catalog lists every registered extension with its title and description.",
  "CANVAS-HOST-001": "Selecting a canvas opens it in the shared hosted panel and preserves its metadata.",
  "CANVAS-SANDBOX-001": "Canvas scripts run in an opaque-origin sandbox without same-origin privileges.",
  "CANVAS-CONFIG-001": "The bridge returns the active Deck configuration to the hosted canvas.",
  "CANVAS-RESOURCES-001": "The bridge returns resource snapshots and streams resource changes.",
  "CANVAS-TELEMETRY-001": "The bridge returns and streams live telemetry counters.",
  "CANVAS-COMMAND-001": "A hosted canvas can execute a resource command through the documented bridge.",
  "CANVAS-ISOLATION-001": "Bridge requests are accepted only from the hosted canvas window.",
  "CANVAS-TOPOLOGY-001": "The topology canvas renders every visible resource and its relationships.",
  "CANVAS-BACK-001": "The user can return from a hosted canvas to the catalog.",
  "CANVAS-RESPONSIVE-001": "The canvas catalog and hosted panel remain contained on mobile.",
} as const;

export type CanvasFeatureId = keyof typeof canvasFeatures;

export function getMissingCanvasFeatures(covered: ReadonlySet<CanvasFeatureId>): CanvasFeatureId[] {
  return (Object.keys(canvasFeatures) as CanvasFeatureId[]).filter((feature) => !covered.has(feature));
}
