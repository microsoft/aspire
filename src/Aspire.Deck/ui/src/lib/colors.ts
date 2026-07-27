// Deterministic per-resource colors for trace waterfall bars.
//
// Mirrors the dashboard's ColorGenerator (src/Shared/ColorGenerator.cs): colors are
// assigned by walking the resource names in sorted order and cycling through a fixed
// palette, so a given resource keeps a stable, distinct color across renders. The
// palette itself is defined as CSS variables in theme.css (light + dark variants).

const TRACE_COLOR_COUNT = 14;

const UNKNOWN_COLOR = "var(--neutral)";

// Builds a name -> CSS color map for the supplied resource names. Assignment is
// stable for a given set: names are sorted first so adjacent resources get
// well-separated palette entries regardless of the order spans arrived in.
export function buildResourceColorMap(names: Iterable<string | null | undefined>): Map<string, string> {
  const unique = [...new Set([...names].filter((n): n is string => !!n))].sort((a, b) =>
    a.localeCompare(b),
  );
  const map = new Map<string, string>();
  unique.forEach((name, index) => {
    map.set(name, `var(--trace-color-${(index % TRACE_COLOR_COUNT) + 1})`);
  });
  return map;
}

export function colorFor(map: Map<string, string>, name: string | null | undefined): string {
  return (name && map.get(name)) || UNKNOWN_COLOR;
}
