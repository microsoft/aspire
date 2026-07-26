import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from "react";
import type { ConsoleLogLine, Resource, ResourceCommand } from "../api/types";
import { subscribeConsoleLogs } from "../api/deck";
import { useResources } from "../lib/useDeckEvent";
import { useTelemetryLimits } from "../lib/useTelemetryLimits";
import { useMeasuredWindow } from "../lib/useMeasuredWindow";
import { useCommandExecution } from "../components/useCommandExecution";
import { InteractiveTerminal } from "../components/InteractiveTerminal";
import { formatConsoleTimestamp, parseConsoleLine } from "../lib/consoleLogs";
import { partitionResourceCommands } from "../lib/resourceCommands";
import {
  Button,
  CommandMenu,
  ConfirmDialog,
  ConsoleIcon,
  EmptyState,
  MoreIcon,
  CommandIcon,
  NamedIcon,
  Page,
  PageActions,
  PageBody,
  PageHeader,
  PageHeading,
  PageSubtitle,
  PageTitle,
  PageToolbar,
  Select,
  Switch,
  type ConfirmRequest,
} from "../toolkit";

const ALL_RESOURCES = "__all-resources__";
const LINE_HEIGHT = 21;
const OVERSCAN = 12;

interface BufferedLine {
  resourceName: string;
  lineNumber: number;
  text: string;
  rawText: string;
  html: string | null;
  timestamp: Date | null;
  isStdErr: boolean;
}

export interface ConsoleRouteState {
  resourceName: string | null;
  showTimestamps: boolean;
  timestampsUtc: boolean;
  wrapLines: boolean;
  paused: boolean;
}

export interface ConsolePageProps {
  routeResourceName?: string | null;
  routeShowTimestamps?: boolean;
  routeTimestampsUtc?: boolean;
  routeWrapLines?: boolean;
  routePaused?: boolean;
  onRouteChange?: (state: ConsoleRouteState) => void;
}

/**
 * Renders one console line, preferring the server-rendered ANSI markup when it is available.
 *
 * The markup comes from the shared `LogParser` running with `encodeForHtml: true`, which
 * HTML-encodes the raw console text *before* wrapping it in `<span class="ansi-*">` elements and
 * turning bare URLs into anchors. Because the encode step runs first, resource output cannot inject
 * markup of its own, which is what makes `dangerouslySetInnerHTML` acceptable here - it is the same
 * trust boundary the Blazor LogViewer relies on when it renders the identical string as a
 * MarkupString.
 *
 * Falling back to `text` keeps the page working against backends that predate the `html` field.
 */
function LogLineText({ line }: { line: BufferedLine }): React.ReactElement {
  if (line.html !== null) {
    return <span className="log-line__text" dangerouslySetInnerHTML={{ __html: line.html }} />;
  }

  return <span className="log-line__text">{line.text}</span>;
}

export function ConsolePage({
  routeResourceName = null,
  routeShowTimestamps = false,
  routeTimestampsUtc = false,
  routeWrapLines = false,
  routePaused = false,
  onRouteChange,
}: ConsolePageProps = {}) {
  const { resources } = useResources();
  // Sized from Dashboard:Frontend:MaxConsoleLogCount so raising the server-side retention actually
  // keeps the extra lines instead of having the client discard them.
  const maxLines = useTelemetryLimits().maxConsoleLogCount;
  const maxLinesRef = useRef(maxLines);
  maxLinesRef.current = maxLines;
  const [selected, setSelected] = useState<string>(routeResourceName ?? ALL_RESOURCES);
  const [lines, setLines] = useState<BufferedLine[]>([]);
  const [paused, setPaused] = useState(routePaused);
  const [pendingCount, setPendingCount] = useState(0);
  const [showTimestamps, setShowTimestamps] = useState(routeShowTimestamps);
  const [timestampsUtc, setTimestampsUtc] = useState(routeTimestampsUtc);
  const [wrapLines, setWrapLines] = useState(routeWrapLines);
  const [confirm, setConfirm] = useState<ConfirmRequest | null>(null);
  const { runCommand, feedbackUi } = useCommandExecution();
  const [autoScroll, setAutoScroll] = useState(true);
  const [scrollTop, setScrollTop] = useState(0);
  const [viewportHeight, setViewportHeight] = useState(0);

  const scrollRef = useRef<HTMLDivElement | null>(null);
  const autoScrollRef = useRef(autoScroll);
  autoScrollRef.current = autoScroll;
  const pausedRef = useRef(paused);
  pausedRef.current = paused;
  const pendingLinesRef = useRef<BufferedLine[]>([]);
  const clearedThroughRef = useRef(new Map<string, number>());
  const visibleResources = useMemo(
    () => resources.filter((resource) => !resource.isHidden),
    [resources],
  );
  const resourceOptions = useMemo(() => [
    { value: ALL_RESOURCES, label: "All resources", group: "All" },
    ...visibleResources.map((resource) => ({
      value: resource.name,
      label: resource.displayName,
      group: resource.resourceType,
    })),
  ], [visibleResources]);
  const selectedResource = useMemo(
    () => selected === ALL_RESOURCES ? null : visibleResources.find((resource) => resource.name === selected) ?? null,
    [selected, visibleResources],
  );

  useEffect(() => {
    setSelected(routeResourceName ?? ALL_RESOURCES);
    setShowTimestamps(routeShowTimestamps);
    setTimestampsUtc(routeTimestampsUtc);
    setWrapLines(routeWrapLines);
    setPaused(routePaused);
    pausedRef.current = routePaused;
  }, [routePaused, routeResourceName, routeShowTimestamps, routeTimestampsUtc, routeWrapLines]);

  // Pick a default resource once the list loads.
  useEffect(() => {
    if (selected === "" && visibleResources.length > 0) {
      setSelected(ALL_RESOURCES);
    } else if (visibleResources.length > 0 && selected !== "" && !resourceOptions.some((option) => option.value === selected)) {
      setSelected(visibleResources.length > 0 ? ALL_RESOURCES : "");
    }
  }, [resourceOptions, selected, visibleResources.length]);

  const appendLines = useCallback((incoming: BufferedLine[]): void => {
    setLines((previous) => {
      const next = previous.concat(incoming);
      const limit = maxLinesRef.current;
      return next.length > limit ? next.slice(next.length - limit) : next;
    });
    // Read through the ref so the callback identity stays stable when the limit arrives from
    // config; re-creating it would tear down and re-establish every live log subscription.
  }, []);

  const subscriptionNames = selectedResource?.hasTerminal
    ? []
    : selected === ALL_RESOURCES
    ? visibleResources.map((resource) => resource.name)
    : selected === "" ? [] : [selected];
  const subscriptionKey = subscriptionNames.join("\0");

  // Subscribe to every selected resource; reset when the selection changes.
  useEffect(() => {
    if (subscriptionNames.length === 0) {
      return;
    }
    setLines([]);
    setScrollTop(0);
    setAutoScroll(true);
    if (scrollRef.current) {
      scrollRef.current.scrollTop = 0;
    }
    pendingLinesRef.current = [];
    setPendingCount(0);
    const unsubscribes = subscriptionNames.map((resourceName) =>
      {
        let active = true;
        const unsubscribe = subscribeConsoleLogs(resourceName, (event) => {
        if (!active) return;
        const clearedThrough = clearedThroughRef.current.get(event.resourceName) ?? 0;
        const incoming = event.lines.filter((line) => line.lineNumber > clearedThrough).map((line: ConsoleLogLine) => {
          const parsed = parseConsoleLine(line.text);
          return {
            resourceName: event.resourceName,
            lineNumber: line.lineNumber,
            text: parsed.text,
            rawText: line.text,
            html: line.html ?? null,
            timestamp: parsed.timestamp,
            isStdErr: line.isStdErr,
          };
        });
        if (pausedRef.current) {
          pendingLinesRef.current.push(...incoming);
          if (pendingLinesRef.current.length > maxLinesRef.current) {
            pendingLinesRef.current = pendingLinesRef.current.slice(-maxLinesRef.current);
          }
          setPendingCount(pendingLinesRef.current.length);
        } else {
          appendLines(incoming);
        }
        });
        return () => {
          active = false;
          unsubscribe();
        };
      },
    );
    return () => {
      for (const unsubscribe of unsubscribes) {
        unsubscribe();
      }
    };
  // The stable key avoids reconnecting every stream when resource metadata changes.
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [appendLines, subscriptionKey]);

  const onPausedChanged = (nextPaused: boolean): void => {
    setPaused(nextPaused);
    pausedRef.current = nextPaused;
    if (!nextPaused && pendingLinesRef.current.length > 0) {
      appendLines(pendingLinesRef.current);
      pendingLinesRef.current = [];
      setPendingCount(0);
    }
    onRouteChange?.({
      resourceName: selected === ALL_RESOURCES ? null : selected,
      showTimestamps,
      timestampsUtc,
      wrapLines,
      paused: nextPaused,
    });
  };

  const updateDisplayRoute = (changes: Partial<ConsoleRouteState>): void => {
    onRouteChange?.({
      resourceName: selected === ALL_RESOURCES ? null : selected,
      showTimestamps,
      timestampsUtc,
      wrapLines,
      paused,
      ...changes,
    });
  };

  const clearLines = (resourceName: string | null): void => {
    const candidates = [...lines, ...pendingLinesRef.current];
    const resourcesToClear = resourceName === null
      ? new Set(candidates.map((line) => line.resourceName))
      : new Set([resourceName]);
    for (const name of resourcesToClear) {
      const lastLineNumber = candidates
        .filter((line) => line.resourceName === name)
        .reduce((maximum, line) => Math.max(maximum, line.lineNumber), clearedThroughRef.current.get(name) ?? 0);
      clearedThroughRef.current.set(name, lastLineNumber);
    }
    setLines((current) => current.filter((line) => !resourcesToClear.has(line.resourceName)));
    pendingLinesRef.current = pendingLinesRef.current.filter((line) => !resourcesToClear.has(line.resourceName));
    setPendingCount(pendingLinesRef.current.length);
  };

  const downloadLines = (): void => {
    const content = lines.map((line) => selected === ALL_RESOURCES
      ? `${line.resourceName}: ${line.rawText}`
      : line.rawText).join("\n") + (lines.length > 0 ? "\n" : "");
    const href = URL.createObjectURL(new Blob([content], { type: "text/plain;charset=utf-8" }));
    const anchor = document.createElement("a");
    anchor.href = href;
    const filePrefix = selected === ALL_RESOURCES ? "AllResources" : selected || "console";
    anchor.download = `${filePrefix}-${new Date().toISOString().replaceAll(":", "").slice(0, 15)}.txt`;
    anchor.click();
    URL.revokeObjectURL(href);
  };

  const requestCommand = (resource: Resource, command: ResourceCommand): void => {
    if (command.confirmationMessage) {
      setConfirm({
        title: command.displayName,
        message: command.confirmationMessage,
        confirmLabel: command.displayName,
        onConfirm: () => void runCommand(resource, command),
      });
    } else {
      void runCommand(resource, command);
    }
  };

  const { highlightedCommands, menuCommands } = partitionResourceCommands(selectedResource?.commands ?? []);

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

  // Stable per-line identity, shared by React's key and the height cache so that trimming the
  // buffer from the front does not misattribute a measured height to a different line.
  const lineKeys = useMemo(
    () => lines.map((line, index) => `${line.resourceName}-${line.lineNumber}-${index}`),
    [lines]
  );

  // Manual windowing: only render the lines intersecting the viewport. Wrapped lines have variable
  // height, so their offsets come from measurements; clipped lines are all exactly LINE_HEIGHT and
  // the measurement path collapses to the same arithmetic as before.
  const { startIndex, endIndex, totalHeight, offsetTop, measureRef } = useMeasuredWindow({
    keys: lineKeys,
    scrollTop,
    viewportHeight,
    estimatedRowHeight: LINE_HEIGHT,
    overscan: OVERSCAN
  });

  const visibleLines = lines.slice(startIndex, endIndex);
  const errorCount = useMemo(() => lines.filter((l) => l.isStdErr).length, [lines]);

  return (
    <Page aria-labelledby="deck-page-console-title">
      <PageHeader>
        <PageHeading>
          <PageTitle id="deck-page-console-title">Console</PageTitle>
          <PageSubtitle>Streaming standard output and error</PageSubtitle>
        </PageHeading>
        <PageActions>
          <CommandMenu
            ariaLabel="Console settings"
            triggerContent={null}
            triggerIcon={<MoreIcon size={18} />}
            placement="below-end"
            entries={[
              {
                id: "download",
                label: "Download logs",
                icon: <NamedIcon name="ArrowDownload" size={16} />,
                disabled: lines.length === 0,
                onSelect: downloadLines,
              },
              { id: "display-divider", kind: "divider" },
              {
                id: "timestamps",
                label: showTimestamps ? "Hide timestamps" : "Show timestamps",
                icon: <NamedIcon name="Clock" size={16} />,
                onSelect: () => {
                  const next = !showTimestamps;
                  setShowTimestamps(next);
                  updateDisplayRoute({ showTimestamps: next });
                },
              },
              {
                id: "utc",
                label: "UTC timestamps",
                disabled: !showTimestamps,
                onSelect: () => {
                  const next = !timestampsUtc;
                  setTimestampsUtc(next);
                  updateDisplayRoute({ timestampsUtc: next });
                },
              },
              {
                id: "wrap",
                label: wrapLines ? "Don't wrap lines" : "Wrap lines",
                icon: <NamedIcon name="TextWrap" size={16} />,
                onSelect: () => {
                  const next = !wrapLines;
                  setWrapLines(next);
                  updateDisplayRoute({ wrapLines: next });
                },
              },
            ]}
          />
        </PageActions>
      </PageHeader>

      <PageToolbar ariaLabel="Console tools">
        <Select
          ariaLabel="Resource"
          options={resourceOptions}
          value={selected}
          placeholder={resourceOptions.length === 0 ? "No resources" : undefined}
          disabled={resourceOptions.length === 0}
          onValueChange={(value) => {
            setSelected(value);
            onRouteChange?.({
              resourceName: value === ALL_RESOURCES ? null : value,
              showTimestamps,
              timestampsUtc,
              wrapLines,
              paused,
            });
          }}
        />
        {!selectedResource?.hasTerminal ? <><Switch
          ariaLabel="Pause incoming data"
          label="Pause"
          checked={paused}
          onCheckedChange={onPausedChanged}
        />
        <CommandMenu
          ariaLabel="Clear console"
          triggerContent="Clear"
          triggerIcon={<NamedIcon name="Delete" size={16} />}
          entries={[
            {
              id: "clear-resource",
              label: selectedResource === null ? "Clear selected resource" : `Clear ${selectedResource.displayName}`,
              disabled: selectedResource === null || (lines.length === 0 && pendingCount === 0),
              onSelect: () => clearLines(selectedResource?.name ?? null),
            },
            {
              id: "clear-all",
              label: "Clear all resources",
              disabled: lines.length === 0 && pendingCount === 0,
              onSelect: () => clearLines(null),
            },
          ]}
        /></> : null}
        {selectedResource ? highlightedCommands.map((command) => (
          <Button
            key={command.name}
            size="small"
            variant="ghost"
            title={command.displayDescription ?? command.displayName}
            disabled={command.state === "disabled"}
            onClick={() => requestCommand(selectedResource, command)}
          >
            <CommandIcon iconName={command.iconName} iconVariant={command.iconVariant} isHighlighted={command.isHighlighted} size={16} />
            {command.displayName}
          </Button>
        )) : null}
        {selectedResource && menuCommands.length > 0 ? (
          <CommandMenu
            ariaLabel="Resource actions"
            triggerContent={null}
            triggerIcon={<MoreIcon size={18} />}
            entries={menuCommands.map((command) => ({
              id: command.name,
              label: command.displayName,
              description: command.displayDescription ?? undefined,
              icon: <CommandIcon iconName={command.iconName} iconVariant={command.iconVariant} isHighlighted={command.isHighlighted} size={16} />,
              disabled: command.state === "disabled",
              onSelect: () => requestCommand(selectedResource, command),
            }))}
          />
        ) : null}
        {!autoScroll ? (
          <button className="btn btn--sm" onClick={scrollToBottom}>
            Scroll to bottom
          </button>
        ) : selectedResource?.hasTerminal ? (
          <InteractiveTerminal resourceName={selectedResource.displayName} replicaIndex={selectedResource.terminalReplicaIndex ?? 0} />
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
          <div className={`console ${wrapLines ? "console--wrap" : ""}`}>
            <div className="console__scroll" ref={scrollRef} onScroll={onScroll}>
              <div style={{ height: totalHeight, position: "relative" }}>
                <div style={{ position: "absolute", top: offsetTop, left: 0, right: 0 }}>
                  {visibleLines.map((line, index) => {
                    const key = lineKeys[startIndex + index] ?? `${line.resourceName}-${line.lineNumber}`;
                    return (
                      <div
                        key={key}
                        ref={wrapLines ? measureRef(key) : undefined}
                        data-resource-name={line.resourceName}
                        className={`log-line ${line.isStdErr ? "stderr" : ""}`}
                        style={wrapLines ? undefined : { height: LINE_HEIGHT }}
                      >
                        <span className="log-line__num">{line.lineNumber}</span>
                        {selected === ALL_RESOURCES ? <span className="log-line__resource">{line.resourceName}</span> : null}
                        {showTimestamps && line.timestamp ? (
                          <time className="log-line__timestamp" dateTime={line.timestamp.toISOString()}>
                            {formatConsoleTimestamp(line.timestamp, timestampsUtc)}
                          </time>
                        ) : null}
                        <LogLineText line={line} />
                      </div>
                    );
                  })}
                </div>
              </div>
            </div>
            <div className="console__footer">
              <span>{lines.length.toLocaleString()} lines</span>
              <span>{errorCount > 0 ? `${errorCount} stderr` : "no errors"}</span>
              {paused ? <span>{pendingCount.toLocaleString()} pending</span> : null}
            </div>
          </div>
        )}
      </PageBody>
      <ConfirmDialog request={confirm} onClose={() => setConfirm(null)} />
      {feedbackUi}
    </Page>
  );
}
