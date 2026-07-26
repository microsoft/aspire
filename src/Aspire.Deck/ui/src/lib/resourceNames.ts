/**
 * Resolves the resource a URL refers to.
 *
 * Deep links carry a *logical* resource name (`empty-0000`), but a running resource is keyed by its
 * DCP instance name, which appends a per-run suffix (`empty-0000-dgwtqzkh`). Matching only on the
 * instance name means every shared or bookmarked link silently resolves to nothing, because the
 * suffix is regenerated on each AppHost run.
 *
 * This mirrors `ResourceViewModel.TryGetResourceByName` in the dashboard
 * (src/Aspire.Dashboard/Model/ResourceViewModel.cs): try the instance name first, then fall back to
 * the display name, and only accept the fallback when exactly one resource carries that display
 * name. The uniqueness requirement matters for replicas - several instances of a replicated
 * resource share a display name, and silently picking one of them would show the wrong replica's
 * data rather than making the ambiguity visible.
 *
 * Comparisons are case-insensitive to match `StringComparers.ResourceName`
 * (`StringComparer.OrdinalIgnoreCase`) in src/Shared/StringComparers.cs.
 */
export interface NamedResource {
  name: string;
  displayName: string;
}

function equalsResourceName(left: string, right: string): boolean {
  // `toLowerCase` performs the locale-independent Unicode default case conversion, unlike
  // `toLocaleLowerCase`. That keeps this equivalent to `OrdinalIgnoreCase` rather than picking up
  // locale-specific rules such as the Turkish dotless i.
  return left.toLowerCase() === right.toLowerCase();
}

export function resolveResourceByName<T extends NamedResource>(
  resources: readonly T[],
  name: string | null | undefined,
): T | null {
  if (!name) {
    return null;
  }

  const byInstanceName = resources.find((resource) => equalsResourceName(resource.name, name));
  if (byInstanceName) {
    return byInstanceName;
  }

  const byDisplayName = resources.filter((resource) => equalsResourceName(resource.displayName, name));
  return byDisplayName.length === 1 ? byDisplayName[0]! : null;
}
