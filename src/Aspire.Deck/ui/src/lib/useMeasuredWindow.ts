import { useCallback, useEffect, useMemo, useRef, useState } from "react";

/**
 * Windowing for lists whose rows do not share a fixed height.
 *
 * The console renders up to `Dashboard:Frontend:MaxConsoleLogCount` lines (10,000 by default).
 * Fixed-height windowing is enough while lines are clipped to one row, but in wrap mode a single
 * line can occupy any number of rows, so offsets have to come from real measurements. Rows report
 * their height through a `ResizeObserver` -- not a one-shot measurement -- because wrapping changes
 * whenever the container is resized, not only when the content changes.
 *
 * Heights are keyed by a caller-supplied stable key rather than by index: the console trims its
 * buffer from the front, which shifts every index and would otherwise attribute one line's height
 * to another.
 */
export interface MeasuredWindow {
  /** First index to render, including overscan. */
  startIndex: number;
  /** Exclusive end index to render, including overscan. */
  endIndex: number;
  /** Height of the full list, used to size the scroll spacer. */
  totalHeight: number;
  /** Offset of `startIndex`, used to position the rendered window. */
  offsetTop: number;
  /** Ref callback to attach to each rendered row. */
  measureRef: (key: string) => (element: HTMLElement | null) => void;
}

export interface MeasuredWindowOptions {
  /** Stable keys, one per row, in list order. */
  keys: readonly string[];
  scrollTop: number;
  viewportHeight: number;
  /** Height assumed for rows that have not been measured yet. */
  estimatedRowHeight: number;
  /** Extra rows rendered above and below the viewport. */
  overscan: number;
}

export function useMeasuredWindow({
  keys,
  scrollTop,
  viewportHeight,
  estimatedRowHeight,
  overscan
}: MeasuredWindowOptions): MeasuredWindow {
  const heights = useRef(new Map<string, number>());
  const observers = useRef(new Map<string, ResizeObserver>());
  // Bumping a counter is what makes a measurement change recompute the layout; the height map is a
  // ref so that observer callbacks do not have to close over the latest state.
  const [revision, setRevision] = useState(0);

  // Drop measurements for rows that have left the buffer, otherwise a long-running console session
  // accumulates an entry for every line it has ever seen.
  useEffect(() => {
    if (heights.current.size <= keys.length * 2) {
      return;
    }

    const live = new Set(keys);
    for (const key of [...heights.current.keys()]) {
      if (!live.has(key)) {
        heights.current.delete(key);
      }
    }
  }, [keys]);

  useEffect(() => {
    const active = observers.current;
    return () => {
      for (const observer of active.values()) {
        observer.disconnect();
      }
      active.clear();
    };
  }, []);

  const measureRef = useCallback(
    (key: string) =>
      (element: HTMLElement | null): void => {
        const existing = observers.current.get(key);
        if (existing) {
          existing.disconnect();
          observers.current.delete(key);
        }

        if (element === null) {
          return;
        }

        const record = (height: number): void => {
          // Sub-pixel jitter from fractional line heights would otherwise cause a render loop.
          if (height > 0 && Math.abs((heights.current.get(key) ?? 0) - height) > 0.5) {
            heights.current.set(key, height);
            setRevision((value) => value + 1);
          }
        };

        record(element.getBoundingClientRect().height);

        const observer = new ResizeObserver((entries) => {
          for (const entry of entries) {
            record(entry.contentRect.height || entry.target.getBoundingClientRect().height);
          }
        });
        observer.observe(element);
        observers.current.set(key, observer);
      },
    []
  );

  return useMemo(() => {
    // Prefix sums so a scroll position can be resolved to an index without walking every row twice.
    const offsets = new Array<number>(keys.length + 1);
    offsets[0] = 0;
    for (let index = 0; index < keys.length; index++) {
      const key = keys[index] ?? "";
      offsets[index + 1] = (offsets[index] ?? 0) + (heights.current.get(key) ?? estimatedRowHeight);
    }

    const totalHeight = offsets[keys.length] ?? 0;

    // Binary search for the first row whose bottom edge is past the top of the viewport.
    let low = 0;
    let high = keys.length;
    while (low < high) {
      const mid = (low + high) >>> 1;
      if ((offsets[mid + 1] ?? 0) <= scrollTop) {
        low = mid + 1;
      } else {
        high = mid;
      }
    }

    const startIndex = Math.max(0, low - overscan);

    const viewportBottom = scrollTop + (viewportHeight || 600);
    let endIndex = low;
    while (endIndex < keys.length && (offsets[endIndex] ?? 0) < viewportBottom) {
      endIndex++;
    }
    endIndex = Math.min(keys.length, endIndex + overscan);

    return {
      startIndex,
      endIndex,
      totalHeight,
      offsetTop: offsets[startIndex] ?? 0,
      measureRef
    };
    // `revision` is intentionally part of the dependency list: it is the signal that the height map
    // behind the ref has changed.
  }, [keys, scrollTop, viewportHeight, estimatedRowHeight, overscan, revision, measureRef]);
}
