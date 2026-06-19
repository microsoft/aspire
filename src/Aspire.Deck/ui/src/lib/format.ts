// Formatting helpers for durations, timestamps and byte sizes.

const NANOS_PER_MS = 1_000_000;

export function formatDurationNanos(durationNanos: string): string {
  let nanos: number;
  try {
    nanos = Number(BigInt(durationNanos));
  } catch {
    nanos = Number(durationNanos);
  }
  if (!Number.isFinite(nanos)) {
    return "—";
  }
  const ms = nanos / NANOS_PER_MS;
  return formatMilliseconds(ms);
}

export function formatMilliseconds(ms: number): string {
  if (ms < 1) {
    return `${(ms * 1000).toFixed(0)}µs`;
  }
  if (ms < 1000) {
    return `${ms.toFixed(ms < 10 ? 1 : 0)}ms`;
  }
  const seconds = ms / 1000;
  if (seconds < 60) {
    return `${seconds.toFixed(2)}s`;
  }
  const minutes = Math.floor(seconds / 60);
  const remSeconds = Math.round(seconds % 60);
  return `${minutes}m ${remSeconds}s`;
}

// Converts a unix nanosecond string (e.g. OTLP timeUnixNano) to a Date.
export function dateFromUnixNano(unixNano: string): Date {
  try {
    const ms = BigInt(unixNano) / 1_000_000n;
    return new Date(Number(ms));
  } catch {
    return new Date(Number(unixNano) / NANOS_PER_MS);
  }
}

export function formatTime(value: Date | string | null): string {
  if (value === null) {
    return "—";
  }
  const date = typeof value === "string" ? new Date(value) : value;
  if (Number.isNaN(date.getTime())) {
    return "—";
  }
  return date.toLocaleTimeString(undefined, {
    hour: "2-digit",
    minute: "2-digit",
    second: "2-digit",
  });
}

export function formatTimeWithMillis(value: Date | string | null): string {
  if (value === null) {
    return "—";
  }
  const date = typeof value === "string" ? new Date(value) : value;
  if (Number.isNaN(date.getTime())) {
    return "—";
  }
  const base = date.toLocaleTimeString(undefined, {
    hour: "2-digit",
    minute: "2-digit",
    second: "2-digit",
  });
  const millis = date.getMilliseconds().toString().padStart(3, "0");
  return `${base}.${millis}`;
}

export function formatRelativeTime(value: string | null): string {
  if (value === null) {
    return "—";
  }
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) {
    return "—";
  }
  const deltaMs = Date.now() - date.getTime();
  const seconds = Math.floor(deltaMs / 1000);
  if (seconds < 5) {
    return "just now";
  }
  if (seconds < 60) {
    return `${seconds}s ago`;
  }
  const minutes = Math.floor(seconds / 60);
  if (minutes < 60) {
    return `${minutes}m ago`;
  }
  const hours = Math.floor(minutes / 60);
  if (hours < 24) {
    return `${hours}h ago`;
  }
  const days = Math.floor(hours / 24);
  return `${days}d ago`;
}

export function formatBytes(bytes: number): string {
  if (!Number.isFinite(bytes)) {
    return "—";
  }
  if (bytes < 1024) {
    return `${bytes} B`;
  }
  const units = ["KB", "MB", "GB", "TB"];
  let value = bytes / 1024;
  let unitIndex = 0;
  while (value >= 1024 && unitIndex < units.length - 1) {
    value /= 1024;
    unitIndex++;
  }
  return `${value.toFixed(value < 10 ? 2 : 1)} ${units[unitIndex]}`;
}

// Formats a metric value, choosing a sensible representation by unit.
export function formatMetricValue(value: number | null, unit: string | null): string {
  if (value === null || !Number.isFinite(value)) {
    return "—";
  }
  switch (unit) {
    case "By":
      return formatBytes(value);
    case "ms":
      return formatMilliseconds(value);
    case "1":
      return `${(value * 100).toFixed(1)}%`;
    default: {
      // Counts and other plain values: show integers without trailing zeros (e.g.
      // "66", not "66.00") and keep a few decimals only for genuinely fractional
      // values. Thousands separators match the dashboard's culture formatting.
      const formatted = value.toLocaleString(undefined, { maximumFractionDigits: 3 });
      const u = displayUnit(unit);
      return u ? `${formatted} ${u}` : formatted;
    }
  }
}

// Normalizes an OTLP/UCUM unit for display, mirroring the Aspire dashboard's
// OtlpUnits.GetUnit: strip UCUM "annotation" units (curly braces, e.g. "{request}"
// — a "count of foo" is unitless), convert rate units ("foo/bar" -> "foo per bar"),
// and expand abbreviations to full words ("ms" -> "milliseconds"). Returns null when
// nothing dimensional remains (so the value is shown as a plain count).
// See src/Aspire.Dashboard/Otlp/Model/OtlpUnits.cs.
export function displayUnit(unit: string | null): string | null {
  if (!unit) {
    return null;
  }
  // UCUM allows annotations anywhere, e.g. "{packet}/s" -> "/s".
  const stripped = unit.replace(/\{[^}]*\}/g, "");
  if (stripped.length === 0) {
    return null;
  }
  // Rate units: "foo/bar" -> "foo per bar".
  const slash = stripped.indexOf("/");
  if (slash > 0 && slash < stripped.length - 1) {
    return `${mapUnit(stripped.slice(0, slash))} per ${mapPerUnit(stripped.slice(slash + 1))}`;
  }
  const mapped = mapUnit(stripped);
  return mapped.length > 0 ? mapped : null;
}

const UNIT_MAP: Record<string, string> = {
  d: "days", h: "hours", min: "minutes", s: "seconds", ms: "milliseconds", us: "microseconds", ns: "nanoseconds",
  By: "bytes", KiBy: "kibibytes", MiBy: "mebibytes", GiBy: "gibibytes", TiBy: "tibibytes",
  KBy: "kilobytes", MBy: "megabytes", GBy: "gigabytes", TBy: "terabytes",
  B: "bytes", KB: "kilobytes", MB: "megabytes", GB: "gigabytes", TB: "terabytes",
  m: "meters", V: "volts", A: "amperes", J: "joules", W: "watts", g: "grams",
  Cel: "celsius", Hz: "hertz", "1": "", "%": "percent", $: "dollars",
};

const PER_UNIT_MAP: Record<string, string> = {
  s: "second", m: "minute", h: "hour", d: "day", w: "week", mo: "month", y: "year",
};

function mapUnit(unit: string): string {
  return UNIT_MAP[unit] ?? unit;
}

function mapPerUnit(perUnit: string): string {
  return PER_UNIT_MAP[perUnit] ?? perUnit;
}

export function shortId(id: string | null, length = 8): string {
  if (id === null || id.length === 0) {
    return "—";
  }
  return id.length <= length ? id : id.slice(0, length);
}
