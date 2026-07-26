import { expect, test } from "@playwright/test";
import { formatDurationNanos } from "../src/lib/format";

// These expectations were produced by running the dashboard's own `DurationFormatter.FormatDuration`
// (src/Shared/DurationFormatter.cs) over each input under the invariant culture, so this suite
// pins the TypeScript port to the exact strings the Blazor dashboard renders. Regenerate by
// feeding the same nanosecond inputs through TimeSpan.FromTicks(nanos / 100).
//
// The interesting rows are the threshold boundaries: milliseconds take over at 0.01ms (10,000ns)
// and seconds at 0.1s (100,000,000ns), which is why 812,000ns is "0.81ms" and not "812μs".
const GOLDEN: ReadonlyArray<readonly [string, string]> = [
  ["0", "0\u03bcs"],
  ["1", "0\u03bcs"],
  ["100", "0.1\u03bcs"],
  ["500", "0.5\u03bcs"],
  ["999", "0.9\u03bcs"],
  ["1000", "1\u03bcs"],
  ["5000", "5\u03bcs"],
  ["9999", "9.9\u03bcs"],
  ["10000", "0.01ms"],
  ["12345", "0.01ms"],
  ["99999", "0.1ms"],
  ["100000", "0.1ms"],
  ["500000", "0.5ms"],
  ["812000", "0.81ms"],
  ["999999", "1ms"],
  ["1000000", "1ms"],
  ["2357000", "2.36ms"],
  ["5000000", "5ms"],
  ["9999999", "10ms"],
  ["10000000", "10ms"],
  ["14370000", "14.37ms"],
  ["99000000", "99ms"],
  ["100000000", "0.1s"],
  ["320000000", "0.32s"],
  ["999999999", "1s"],
  ["1000000000", "1s"],
  ["1500000000", "1.5s"],
  ["2357000000", "2.36s"],
  ["59000000000", "59s"],
  ["60000000000", "1m"],
  ["90000000000", "1m 30s"],
  ["150555000000", "2m 31s"],
  ["3600000000000", "1h"],
  ["3723000000000", "1h 2m"],
  ["86400000000000", "1d"],
  ["900000000000000", "10d 10h"],
];

test("[HTTP-TRACES-001] formats span durations exactly like the dashboard", () => {
  const actual = GOLDEN.map(([nanos]) => [nanos, formatDurationNanos(nanos)] as const);
  expect(actual).toEqual(GOLDEN.map(([nanos, expected]) => [nanos, expected] as const));
});

test("[HTTP-TRACES-001] formats negative span offsets by magnitude", () => {
  // A child span can report a start before its parent's recorded start, so the trace detail view
  // renders a negative offset. The sign is applied to the formatted magnitude rather than being
  // pushed through the unit ladder, which would otherwise pin every negative value to microseconds.
  expect(formatDurationNanos("-812000")).toBe("-0.81ms");
  expect(formatDurationNanos("-90000000000")).toBe("-1m 30s");
});

test("[HTTP-TRACES-001] reports unparsable durations as an em dash", () => {
  expect(formatDurationNanos("not-a-number")).toBe("—");
});
