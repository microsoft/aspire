// Formatting helpers for durations, timestamps and byte sizes.
import { timeFormatOptions } from "./timeFormat";

const NANOS_PER_MS = 1_000_000;

// A .NET tick is 100ns. Durations arrive from OTLP in nanoseconds, but the dashboard's formatting
// rules are expressed in ticks, so the port below converts once and then works in ticks.
const NANOS_PER_TICK = 100;
const TICKS_PER_MICROSECOND = 10;
const TICKS_PER_MILLISECOND = 10_000;
const TICKS_PER_SECOND = 10_000_000;
const TICKS_PER_MINUTE = 600_000_000;
const TICKS_PER_HOUR = 36_000_000_000;
const TICKS_PER_DAY = 864_000_000_000;

interface UnitStep {
  readonly unit: string;
  readonly ticks: number;
  readonly threshold: number;
  readonly isDecimal: boolean;
}

/**
 * Ported from `DurationFormatter` in src/Shared/DurationFormatter.cs so spans, traces and metrics
 * read identically to the dashboard. The thresholds are deliberately not "1 of the next unit":
 * milliseconds take over at 0.01ms and seconds at 0.1s, so a 812µs span reads "0.81ms" rather than
 * "812µs". Note the microsecond unit uses U+03BC GREEK SMALL LETTER MU, matching the dashboard.
 */
const UNIT_STEPS: readonly UnitStep[] = [
  { unit: "d", ticks: TICKS_PER_DAY, threshold: TICKS_PER_DAY, isDecimal: false },
  { unit: "h", ticks: TICKS_PER_HOUR, threshold: TICKS_PER_HOUR, isDecimal: false },
  { unit: "m", ticks: TICKS_PER_MINUTE, threshold: TICKS_PER_MINUTE, isDecimal: false },
  { unit: "s", ticks: TICKS_PER_SECOND, threshold: TICKS_PER_SECOND / 10, isDecimal: true },
  { unit: "ms", ticks: TICKS_PER_MILLISECOND, threshold: TICKS_PER_MILLISECOND / 100, isDecimal: true },
  { unit: "\u03bcs", ticks: TICKS_PER_MICROSECOND, threshold: TICKS_PER_MICROSECOND, isDecimal: true },
];

function resolveUnits(ticks: number): [UnitStep, UnitStep] {
  for (let i = 0; i < UNIT_STEPS.length; i++) {
    const step = UNIT_STEPS[i]!;
    const keepSearching = i < UNIT_STEPS.length - 1 && step.threshold > ticks;
    if (!keepSearching) {
      return [step, i < UNIT_STEPS.length - 1 ? UNIT_STEPS[i + 1]! : step];
    }
  }

  const last = UNIT_STEPS[UNIT_STEPS.length - 1]!;
  return [last, last];
}

// .NET's "0.##" rounds half away from zero. Durations are non-negative by the time they get here
// (the sign is handled by the caller), so Math.round -- which rounds half toward +infinity -- is
// equivalent.
function formatOptionalDecimals(value: number): string {
  const rounded = Math.round(value * 100) / 100;
  return rounded.toFixed(2).replace(/\.?0+$/, "");
}

function formatTicks(ticks: number): string {
  const [primary, secondary] = resolveUnits(ticks);

  if (primary.isDecimal) {
    return `${formatOptionalDecimals(ticks / primary.ticks)}${primary.unit}`;
  }

  // Whole units are shown as at most two components ("1h 2m"), with the smaller one rounded and
  // omitted entirely when it rounds to zero.
  const ofPrevious = primary.ticks / secondary.ticks;
  const primaryValue = Math.floor(ticks / primary.ticks);
  const secondaryValue = Math.round((ticks / secondary.ticks) % ofPrevious);

  return secondaryValue === 0
    ? `${primaryValue}${primary.unit}`
    : `${primaryValue}${primary.unit} ${secondaryValue}${secondary.unit}`;
}

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

  // Span offsets can be negative (a child that started before its parent's recorded start), so
  // format the magnitude and re-apply the sign rather than feeding a negative into the unit ladder.
  const negative = nanos < 0;
  // TimeSpan.FromTicks takes a whole number of ticks, so sub-tick precision is truncated, not
  // rounded -- matching that here keeps values on the exact same side of a unit threshold.
  const ticks = Math.trunc(Math.abs(nanos) / NANOS_PER_TICK);
  const formatted = formatTicks(ticks);

  return negative ? `-${formatted}` : formatted;
}

export function formatMilliseconds(ms: number): string {
  if (!Number.isFinite(ms)) {
    return "—";
  }

  // Deliberately NOT the DurationFormatter ladder used by formatDurationNanos. That ladder is tuned
  // for trace waterfalls, where switching to seconds at 0.1s keeps sibling spans on a common unit.
  // Metric values are read on their own, so a p99 latency reads better as "320ms" than "0.32s".
  // The dashboard sidesteps this by formatting every metric as a bare F3 number (MetricTable.razor.cs),
  // which loses the unit entirely; keeping a unit here is a deliberate Deck improvement.
  const negative = ms < 0;
  const magnitude = Math.abs(ms);
  let formatted: string;
  if (magnitude < 1) {
    formatted = `${(magnitude * 1000).toFixed(0)}\u03bcs`;
  } else if (magnitude < 1000) {
    formatted = `${magnitude.toFixed(magnitude < 10 ? 1 : 0)}ms`;
  } else {
    const seconds = magnitude / 1000;
    if (seconds < 60) {
      formatted = `${seconds.toFixed(2)}s`;
    } else {
      const minutes = Math.floor(seconds / 60);
      const remSeconds = Math.round(seconds % 60);
      formatted = `${minutes}m ${remSeconds}s`;
    }
  }

  return negative ? `-${formatted}` : formatted;
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

/**
 * Chooses the hour representation the dashboard would use. .NET formats times with the culture's
 * LongTimePattern, which is "h:mm:ss tt" for 12-hour cultures (no leading zero) but "HH:mm:ss" for
 * 24-hour ones (zero padded). Intl does not mirror that automatically -- "2-digit" pads 12-hour
 * clocks and "numeric" leaves 24-hour clocks unpadded -- so resolve the hour cycle first and pick
 * the option that reproduces the .NET pattern.
 */
function hourOption(options: Pick<Intl.DateTimeFormatOptions, "hour12">): "numeric" | "2-digit" {
  return new Intl.DateTimeFormat(undefined, { ...options, hour: "numeric" }).resolvedOptions().hour12
    ? "numeric"
    : "2-digit";
}

function timeOptions(fractionalSecondDigits?: 3): Intl.DateTimeFormatOptions {
  const options = timeFormatOptions();
  return {
    ...options,
    hour: hourOption(options),
    minute: "2-digit",
    second: "2-digit",
    ...(fractionalSecondDigits === undefined ? {} : { fractionalSecondDigits }),
  };
}

export function formatTime(value: Date | string | null): string {
  if (value === null) {
    return "—";
  }
  const date = typeof value === "string" ? new Date(value) : value;
  if (Number.isNaN(date.getTime())) {
    return "—";
  }
  return date.toLocaleTimeString(undefined, timeOptions());
}

export function formatTimeWithMillis(value: Date | string | null): string {
  if (value === null) {
    return "—";
  }
  const date = typeof value === "string" ? new Date(value) : value;
  if (Number.isNaN(date.getTime())) {
    return "—";
  }
  return date.toLocaleTimeString(undefined, timeOptions(3));
}

/**
 * Mirrors `FormatHelpers.FormatDateTime` in src/Shared/FormatHelpers.cs. The dashboard builds this
 * pattern as `ShortDatePattern + " " + LongTimePattern` (DateFormatStringsHelpers.cs), joining with
 * a literal space. `toLocaleString` instead uses the locale's own date/time connector, which adds a
 * comma in en-US ("7/25/2026, 3:09:04 PM"), so compose the two halves explicitly.
 */
export function formatDateTime(value: Date): string {
  return `${value.toLocaleDateString(undefined)} ${value.toLocaleTimeString(undefined, timeOptions())}`;
}

/**
 * Mirrors `FormatHelpers.FormatTimeWithOptionalDate` in src/Shared/FormatHelpers.cs: render the
 * time alone while the timestamp is from today, and prefix the date once it is not, so a value
 * that scrolls past midnight stays unambiguous. Seconds are included but milliseconds are not,
 * because the resource service reports start times at second precision.
 */
export function formatTimeWithOptionalDate(value: Date | string | null): string {
  if (value === null) {
    return "—";
  }
  const date = typeof value === "string" ? new Date(value) : value;
  if (Number.isNaN(date.getTime())) {
    return "—";
  }

  const now = new Date();
  const isToday = date.getFullYear() === now.getFullYear()
    && date.getMonth() === now.getMonth()
    && date.getDate() === now.getDate();

  return isToday ? formatTime(date) : formatDateTime(date);
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

/**
 * Mirrors how the Blazor dashboard renders the resource state column
 * (`ResourceStateViewModel.GetStateText`), which treats a null *or empty* state as "Unknown" and
 * otherwise runs the raw value through Humanizer's `string.Humanize()`. Resource states arrive
 * from DCP as PascalCase tokens ("NotStarted", "FailedToStart", "RuntimeUnhealthy"), so rendering
 * them unmodified leaks an internal identifier into the UI.
 *
 * Humanizer splits on case boundaries and lower-cases every word after the first, while keeping
 * runs of capitals intact, e.g. "NotStarted" -> "Not started" and "HTTPRequest" -> "HTTP request".
 */
export function humanizeResourceState(state: string | null | undefined): string {
  if (state === null || state === undefined || state.trim() === "") {
    return "Unknown";
  }

  // A value that already contains whitespace has been humanized upstream; re-splitting it would
  // lower-case words that were deliberately capitalised.
  if (/\s/.test(state)) {
    return state;
  }

  return state
    .replace(/([a-z0-9])([A-Z])/g, "$1 $2")
    .replace(/([A-Z]+)([A-Z][a-z])/g, "$1 $2")
    .split(" ")
    .map((word, index) => (index === 0 || /^[A-Z]{2,}$/.test(word) ? word : word.toLowerCase()))
    .join(" ");
}
