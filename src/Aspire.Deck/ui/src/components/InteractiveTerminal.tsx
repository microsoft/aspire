import { useEffect, useRef, useState } from "react";
import { Button, NamedIcon, Select } from "../toolkit";

interface TerminalToolbarState {
  status: "connecting" | "primary" | "viewer" | "no-primary";
  connected: boolean;
  isPrimary: boolean;
  canTakeControl: boolean;
  sizeKey: string;
  fontPx: number;
  fontControlsEnabled: boolean;
  sizeSelectEnabled: boolean;
  cols: number;
  rows: number;
}

interface TerminalModule {
  initTerminal(element: HTMLElement, wsUrl: string, callback: TerminalCallback): Promise<number>;
  disposeTerminal(id: number): void;
  reconnectTerminal(id: number, wsUrl: string): void;
  takePrimaryFromHost(id: number): void;
  setFontSizeFromHost(id: number, size: number): void;
  setSizeModeFromHost(id: number, sizeKey: string): void;
}

interface TerminalCallback {
  invokeMethodAsync(method: string, state: TerminalToolbarState): Promise<void>;
}

const DEFAULT_FONT_SIZE = 13;
const SIZE_OPTIONS = [
  { value: "auto", label: "Auto" },
  { value: "80x24", label: "80×24" },
  { value: "80x30", label: "80×30" },
  { value: "100x30", label: "100×30" },
  { value: "132x30", label: "132×30" },
  { value: "132x50", label: "132×50" },
];

const initialState: TerminalToolbarState = {
  status: "connecting",
  connected: false,
  isPrimary: false,
  canTakeControl: false,
  sizeKey: "auto",
  fontPx: DEFAULT_FONT_SIZE,
  fontControlsEnabled: false,
  sizeSelectEnabled: false,
  cols: 0,
  rows: 0,
};

export function InteractiveTerminal({ resourceName, replicaIndex }: { resourceName: string; replicaIndex: number }) {
  const hostRef = useRef<HTMLDivElement | null>(null);
  const moduleRef = useRef<TerminalModule | null>(null);
  const terminalIdRef = useRef(0);
  const [state, setState] = useState(initialState);
  const isMock = new URLSearchParams(window.location.search).get("backend") !== "http";
  const wsUrl = `${window.location.protocol === "https:" ? "wss" : "ws"}://${window.location.host}/api/terminal?resource=${encodeURIComponent(resourceName)}&replica=${replicaIndex}`;

  useEffect(() => {
    if (isMock) {
      setState({ ...initialState, status: "viewer", connected: true, canTakeControl: true, cols: 80, rows: 24 });
      return;
    }

    let disposed = false;
    const callback: TerminalCallback = {
      async invokeMethodAsync(_method, nextState) {
        if (!disposed) setState(nextState);
      },
    };
    const load = async () => {
      // This module is served by the dashboard rather than bundled by Vite.
      const modulePath = "/Components/Controls/TerminalView.razor.js";
      const terminalModule = await import(/* @vite-ignore */ modulePath) as TerminalModule;
      if (disposed || hostRef.current === null) return;
      moduleRef.current = terminalModule;
      terminalIdRef.current = await terminalModule.initTerminal(hostRef.current, wsUrl, callback);
    };
    void load();
    return () => {
      disposed = true;
      if (terminalIdRef.current !== 0) moduleRef.current?.disposeTerminal(terminalIdRef.current);
      terminalIdRef.current = 0;
      moduleRef.current = null;
    };
  }, [isMock, wsUrl]);

  const takeControl = () => {
    if (isMock) {
      setState((current) => ({ ...current, status: "primary", isPrimary: true, canTakeControl: false, fontControlsEnabled: true, sizeSelectEnabled: true }));
    } else {
      moduleRef.current?.takePrimaryFromHost(terminalIdRef.current);
    }
  };
  const releaseControl = () => {
    if (isMock) {
      setState((current) => ({ ...current, status: "viewer", isPrimary: false, canTakeControl: true, fontControlsEnabled: false, sizeSelectEnabled: false }));
    } else {
      moduleRef.current?.reconnectTerminal(terminalIdRef.current, wsUrl);
    }
  };
  const setFontSize = (fontPx: number) => {
    const next = Math.max(4, Math.min(72, fontPx));
    setState((current) => ({ ...current, fontPx: next, sizeKey: "auto" }));
    if (!isMock) moduleRef.current?.setFontSizeFromHost(terminalIdRef.current, next);
  };
  const setSize = (sizeKey: string) => {
    const preset = SIZE_OPTIONS.find((option) => option.value === sizeKey);
    const [cols, rows] = sizeKey === "auto" ? [state.cols, state.rows] : sizeKey.split("x").map(Number);
    setState((current) => ({ ...current, sizeKey: preset?.value ?? "auto", cols: cols ?? current.cols, rows: rows ?? current.rows }));
    if (!isMock) moduleRef.current?.setSizeModeFromHost(terminalIdRef.current, sizeKey);
  };

  return (
    <div className="interactive-terminal" aria-label={`${resourceName} terminal`}>
      <div className="interactive-terminal__toolbar" role="toolbar" aria-label="Terminal controls">
        <Button size="small" variant={state.isPrimary ? "ghost" : "primary"} disabled={!state.isPrimary && !state.canTakeControl} onClick={state.isPrimary ? releaseControl : takeControl}>
          {state.isPrimary ? "Release control" : "Take control"}
        </Button>
        <Button size="small" variant="ghost" title="Decrease font size" disabled={!state.fontControlsEnabled || state.fontPx <= 4} onClick={() => setFontSize(state.fontPx - 1)}>−</Button>
        <span className="interactive-terminal__font" aria-label="Terminal font size">{state.fontPx}px</span>
        <Button size="small" variant="ghost" title="Increase font size" disabled={!state.fontControlsEnabled || state.fontPx >= 72} onClick={() => setFontSize(state.fontPx + 1)}>+</Button>
        <Button size="small" variant="ghost" title="Reset font size" disabled={!state.fontControlsEnabled || state.fontPx === DEFAULT_FONT_SIZE} onClick={() => setFontSize(DEFAULT_FONT_SIZE)}>
          <NamedIcon name="ArrowReset" size={16} />
        </Button>
        <Select ariaLabel="Terminal grid size" options={SIZE_OPTIONS} value={state.sizeKey} disabled={!state.sizeSelectEnabled} onValueChange={setSize} />
        <span className="interactive-terminal__dimensions" aria-label="Terminal dimensions">{state.cols} × {state.rows}</span>
        <span className={`badge ${state.isPrimary ? "success" : ""}`}>{state.status === "primary" ? "In control" : state.status === "connecting" ? "Connecting" : "View only"}</span>
      </div>
      {isMock ? <pre className="interactive-terminal__mock-screen" tabIndex={0}>$ dotnet watch{`\n`}Watching for file changes.</pre> : <div ref={hostRef} className="interactive-terminal__host" />}
    </div>
  );
}
