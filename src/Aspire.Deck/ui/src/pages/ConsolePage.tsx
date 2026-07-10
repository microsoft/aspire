import { useEffect, useLayoutEffect, useMemo, useRef, useState } from "react";
import type { ConsoleLogLine } from "../api/types";
import { subscribeConsoleLogs } from "../api/deck";
import { useResources } from "../lib/useDeckEvent";
import {
  ConsoleIcon,
  EmptyState,
  Page,
  PageBody,
  PageHeader,
  PageHeading,
  PageSubtitle,
  PageTitle,
  PageToolbar,
} from "../toolkit";

const MAX_LINES = 5000;
const LINE_HEIGHT = 21;
const OVERSCAN = 12;

interface BufferedLine {
  lineNumber: number;
  text: string;
  isStdErr: boolean;
}

export function ConsolePage() {
  const { resources } = useResources();
  const [selected, setSelected] = useState<string>("");
  const [lines, setLines] = useState<BufferedLine[]>([]);
  const [autoScroll, setAutoScroll] = useState(true);
  const [scrollTop, setScrollTop] = useState(0);
  const [viewportHeight, setViewportHeight] = useState(0);

  const scrollRef = useRef<HTMLDivElement | null>(null);
  const autoScrollRef = useRef(autoScroll);
  autoScrollRef.current = autoScroll;

  // Pick a default resource once the list loads.
  useEffect(() => {
    if (selected === "" && resources.length > 0) {
      const first = resources.find((r) => !r.isHidden) ?? resources[0];
      if (first) {
        setSelected(first.name);
      }
    }
  }, [resources, selected]);

  // Subscribe to console logs for the selected resource; reset on change.
  useEffect(() => {
    if (selected === "") {
      return;
    }
    setLines([]);
    const unsubscribe = subscribeConsoleLogs(selected, (event) => {
      setLines((prev) => {
        const next = prev.concat(
          event.lines.map((l: ConsoleLogLine) => ({
            lineNumber: l.lineNumber,
            text: l.text,
            isStdErr: l.isStdErr,
          })),
        );
        return next.length > MAX_LINES ? next.slice(next.length - MAX_LINES) : next;
      });
    });
    return unsubscribe;
  }, [selected]);

  // Keep the viewport pinned to the bottom when auto-scroll is enabled.
  useLayoutEffect(() => {
    if (autoScrollRef.current && scrollRef.current) {
      scrollRef.current.scrollTop = scrollRef.current.scrollHeight;
    }
  }, [lines]);

  useEffect(() => {
    const el = scrollRef.current;
    if (!el) {
      return;
    }
    setViewportHeight(el.clientHeight);
    const observer = new ResizeObserver(() => setViewportHeight(el.clientHeight));
    observer.observe(el);
    return () => observer.disconnect();
  }, []);

  const onScroll = (): void => {
    const el = scrollRef.current;
    if (!el) {
      return;
    }
    setScrollTop(el.scrollTop);
    const atBottom = el.scrollHeight - el.scrollTop - el.clientHeight < 24;
    setAutoScroll(atBottom);
  };

  const scrollToBottom = (): void => {
    const el = scrollRef.current;
    if (el) {
      el.scrollTop = el.scrollHeight;
      setAutoScroll(true);
    }
  };

  // Manual windowing: only render the lines intersecting the viewport.
  const { startIndex, endIndex } = useMemo(() => {
    const start = Math.max(0, Math.floor(scrollTop / LINE_HEIGHT) - OVERSCAN);
    const visibleCount = Math.ceil((viewportHeight || 600) / LINE_HEIGHT) + OVERSCAN * 2;
    const end = Math.min(lines.length, start + visibleCount);
    return { startIndex: start, endIndex: end };
  }, [scrollTop, viewportHeight, lines.length]);

  const visibleLines = lines.slice(startIndex, endIndex);
  const errorCount = useMemo(() => lines.filter((l) => l.isStdErr).length, [lines]);

  return (
    <Page aria-labelledby="deck-page-console-title">
      <PageHeader>
        <PageHeading>
          <PageTitle id="deck-page-console-title">Console</PageTitle>
          <PageSubtitle>Streaming standard output and error</PageSubtitle>
        </PageHeading>
      </PageHeader>

      <PageToolbar ariaLabel="Console tools">
        <select className="select" value={selected} onChange={(e) => setSelected(e.target.value)}>
          {resources.length === 0 ? <option value="">No resources</option> : null}
          {resources
            .filter((r) => !r.isHidden)
            .map((r) => (
              <option key={r.name} value={r.name}>
                {r.displayName}
              </option>
            ))}
        </select>
        {!autoScroll ? (
          <button className="btn btn--sm" onClick={scrollToBottom}>
            Scroll to bottom
          </button>
        ) : (
          <span className="badge accent">Live · following</span>
        )}
      </PageToolbar>

      <PageBody style={{ display: "flex" }}>
        {selected === "" ? (
          <EmptyState icon={<ConsoleIcon size={26} />} title="No resource selected">
            Pick a resource above to stream its console output.
          </EmptyState>
        ) : (
          <div className="console">
            <div className="console__scroll" ref={scrollRef} onScroll={onScroll}>
              <div style={{ height: lines.length * LINE_HEIGHT, position: "relative" }}>
                <div style={{ position: "absolute", top: startIndex * LINE_HEIGHT, left: 0, right: 0 }}>
                  {visibleLines.map((line, i) => (
                    <div
                      key={startIndex + i}
                      className={`log-line ${line.isStdErr ? "stderr" : ""}`}
                      style={{ height: LINE_HEIGHT }}
                    >
                      <span className="log-line__num">{line.lineNumber}</span>
                      <span className="log-line__text">{line.text}</span>
                    </div>
                  ))}
                </div>
              </div>
            </div>
            <div className="console__footer">
              <span>{lines.length.toLocaleString()} lines</span>
              <span>{errorCount > 0 ? `${errorCount} stderr` : "no errors"}</span>
            </div>
          </div>
        )}
      </PageBody>
    </Page>
  );
}
