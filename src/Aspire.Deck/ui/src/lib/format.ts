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
      const rounded = Math.abs(value) >= 100 ? value.toFixed(0) : value.toFixed(2);
      return unit ? `${rounded} ${unit}` : rounded;
    }
  }
}

export function shortId(id: string | null, length = 8): string {
  if (id === null || id.length === 0) {
    return "—";
  }
  return id.length <= length ? id : id.slice(0, length);
}
