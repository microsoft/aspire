import { expect, test } from "@playwright/test";
import { formatDateTime, formatTime, formatTimeWithOptionalDate } from "../src/lib/format";

// The dashboard formats timestamps with the culture's LongTimePattern and composes date-time as
// `ShortDatePattern + " " + LongTimePattern` (src/Shared/FormatHelpers.cs and
// src/Shared/DateFormatStringsHelpers.cs). These assertions are written against the resolved hour
// cycle rather than a hardcoded locale so they hold wherever the suite runs.
const MORNING = new Date(2026, 6, 25, 3, 9, 4);
const AFTERNOON = new Date(2026, 6, 25, 15, 9, 4);

function resolvedHour12(): boolean {
  return new Intl.DateTimeFormat(undefined, { hour: "numeric" }).resolvedOptions().hour12 === true;
}

test("[HTTP-RESOURCES-001] pads the hour only on 24-hour clocks, matching the dashboard pattern", () => {
  // .NET uses "h:mm:ss tt" for 12-hour cultures (no leading zero) and "HH:mm:ss" for 24-hour ones.
  // Intl does the opposite by default for each, so a regression here shows up as "03:09:04 AM".
  const morning = formatTime(MORNING);
  if (resolvedHour12()) {
    expect(morning).toMatch(/^3:09:04\b/);
  } else {
    expect(morning).toMatch(/^03:09:04\b/);
  }

  expect(formatTime(AFTERNOON)).toMatch(resolvedHour12() ? /^3:09:04\b/ : /^15:09:04\b/);
});

test("[HTTP-RESOURCES-001] joins date and time with a space rather than a locale connector", () => {
  for (const value of [MORNING, AFTERNOON]) {
    const datePart = value.toLocaleDateString(undefined);
    const formatted = formatDateTime(value);

    expect(formatted.startsWith(`${datePart} `)).toBe(true);
    // toLocaleString would emit "7/25/2026, 3:09:04 PM" in en-US; the dashboard never does.
    expect(formatted.startsWith(`${datePart},`)).toBe(false);
    expect(formatted.slice(datePart.length + 1)).toBe(formatTime(value));
  }
});

// Telemetry grids (structured logs, traces) render timestamps with
// FormatHelpers.FormatTimeWithOptionalDate(..., MillisecondsDisplay.Truncated): time alone while the
// value is from today, prefixed with the short date once it is not, and always with 3 fractional
// digits. Deck previously used a time-only formatter, so a log written yesterday was rendered
// identically to one written minutes ago. These vectors are deterministic; the differential suite
// can only observe the difference once the playground has been running across a day boundary.

test("[HTTP-RESOURCES-001] renders today's telemetry timestamps as time only, with milliseconds", () => {
  const today = new Date();
  today.setHours(15, 9, 4, 123);

  const formatted = formatTimeWithOptionalDate(today, 3);

  expect(formatted).toBe(today.toLocaleTimeString(undefined, {
    ...new Intl.DateTimeFormat(undefined, { hour: "numeric" }).resolvedOptions().hour12 === true
      ? { hour: "numeric" }
      : { hour: "2-digit", hourCycle: "h23" },
    minute: "2-digit",
    second: "2-digit",
    fractionalSecondDigits: 3,
  }));
  expect(formatted.startsWith(today.toLocaleDateString(undefined))).toBe(false);
  expect(formatted).toMatch(/[.,]\d{3}\b/);
});

test("[HTTP-RESOURCES-001] prefixes the date once a telemetry timestamp is not from today", () => {
  const yesterday = new Date();
  yesterday.setDate(yesterday.getDate() - 1);
  yesterday.setHours(23, 19, 51, 949);

  const formatted = formatTimeWithOptionalDate(yesterday, 3);

  expect(formatted).toBe(formatDateTime(yesterday, 3));
  expect(formatted.startsWith(`${yesterday.toLocaleDateString(undefined)} `)).toBe(true);
  expect(formatted).toMatch(/[.,]\d{3}\b/);
});

test("[HTTP-RESOURCES-001] omits milliseconds when the caller does not ask for them", () => {
  // MetricTable.razor calls FormatTimeWithOptionalDate with the default MillisecondsDisplay.None,
  // so the metric table must not gain a fractional part that the dashboard does not render.
  const yesterday = new Date();
  yesterday.setDate(yesterday.getDate() - 1);
  yesterday.setHours(23, 19, 51, 949);

  expect(formatTimeWithOptionalDate(yesterday)).toBe(formatDateTime(yesterday));
  expect(formatTimeWithOptionalDate(yesterday)).not.toMatch(/[.,]\d{3}\b/);
});
