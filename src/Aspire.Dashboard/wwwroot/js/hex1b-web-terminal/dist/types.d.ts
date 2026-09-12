/** Logical terminal dimensions, confirmed by the producer rather than reflowed locally. */
export interface TerminalGrid {
    columns: number;
    rows: number;
}
export interface TerminalSize {
    width: number;
    height: number;
}
export interface TerminalPoint {
    x: number;
    y: number;
}
export type MouseTrackingMode = 0 | 9 | 1000 | 1002 | 1003;
export interface TerminalGeometry extends TerminalGrid {
    cellWidth: number;
    cellHeight: number;
    mouseTracking: MouseTrackingMode;
}
export interface TerminalPeer {
    id: string | null;
    primaryId: string | null;
    isPrimary: boolean;
}
/** Font size is an integer from 8 to 32; fixed grids allow 20–300 columns and 10–100 rows. */
export type TerminalSizing = {
    mode: "auto";
    fontSize?: number;
} | {
    mode: "fixed";
    fontSize?: number;
    columns: number;
    rows: number;
};
export type TerminalSizingState = {
    mode: "auto";
    fontSize: number;
} | {
    mode: "fixed";
    fontSize: number;
    columns: number;
    rows: number;
};
export interface TerminalFontFace {
    url: string;
    weight?: string;
    style?: string;
}
/** Without faces, a non-generic family must be installed locally in the worker's environment. */
export interface TerminalFont {
    family: string;
    faces?: readonly TerminalFontFace[];
}
export type TerminalBuffer = "main" | "alternate";
export type SelectionMode = "character" | "word" | "line" | "rectangle";
export interface SelectionRange {
    row: number;
    startColumn: number;
    endColumn: number;
}
export type TerminalViewport = ({
    available: true;
    generation: string;
    buffer: TerminalBuffer;
    totalRows: number;
    liveTop: number;
    top: number;
    requestId: number;
    rowIds: readonly string[];
    revision: number;
} | {
    available: false;
    generation?: undefined;
    buffer?: undefined;
    totalRows?: undefined;
    liveTop?: undefined;
    top?: undefined;
    requestId?: undefined;
    rowIds?: readonly string[];
    revision?: undefined;
}) & {
    following: boolean;
    pending: boolean;
    followTail: boolean;
    offset: number;
};
export type TerminalSelection = ({
    status: "valid";
    text: string;
    requestId: number;
    revision: number;
} | {
    status: "none" | "invalidated";
    text: null;
    requestId: number;
    revision: number;
} | {
    status: "pending";
    text: null;
    requestId: number;
    revision?: undefined;
} | {
    status: "unavailable";
    text: null;
    requestId?: undefined;
    revision?: undefined;
}) & {
    mode: SelectionMode;
    ranges: readonly Readonly<SelectionRange>[];
    canExtend?: boolean;
    message: string;
    active: boolean;
    pending: boolean;
    copying: boolean;
    copyError: string;
};
export type TerminalStatusLevel = "info" | "ready" | "error";
/** Native WebSocket close details, not an assertion that the terminal workload completed. */
export interface TerminalCloseDetails {
    /** RFC 6455 status reported by the browser, including 1006 for abnormal loss without a close frame. */
    readonly code: number;
    /** Peer-provided close reason, or "". Treat as untrusted text. */
    readonly reason: string;
    /** Whether the browser observed a clean WebSocket closing handshake, not workload success. */
    readonly wasClean: boolean;
}
export type TerminalRendererKind = "webgpu" | "webgl2";
/** Auto prefers WebGPU and falls back to WebGL2 for capability/device acquisition failures. */
export type TerminalRendererPreference = "auto" | TerminalRendererKind;
/** Metrics are initially empty; individual fields appear as initialization and presentation proceed. */
export interface TerminalStats {
    /** Active backend; absent until renderer initialization completes. */
    renderer?: TerminalRendererKind;
    /** Why auto selected WebGL2 instead of WebGPU; absent for explicit selection or WebGPU. */
    rendererFallbackReason?: string;
    revision?: number;
    fullFrames?: number;
    frames?: number;
    presentations?: number;
    changedCells?: number;
    lastChangedCells?: number;
    discardedFrames?: number;
    imageCount?: number;
    textureBytes?: number;
    atlasGlyphs?: number;
    atlasBytes?: number;
    bytesReceived?: number;
    imageUploadBytes?: number;
    imagePayloadBytes?: number;
    gpu?: "initializing" | "ready" | "error" | "stopped";
    connected?: boolean;
    warnings?: readonly string[];
    fps?: number;
    receivedKBps?: number;
    workloadMBps?: number;
    captureMs?: number;
    rendererCpuMs?: number;
    preparationCpuMs?: number;
    workloadBytes?: number;
    outputBatches?: number;
    serverElapsedMs?: number;
    quads?: number;
    drawCalls?: number;
    columns?: number;
    rows?: number;
    mouseTracking?: MouseTrackingMode;
    peer?: TerminalPeer;
    fontFamily?: string;
    rasterScale?: number;
    backingScale?: number;
    backingWidth?: number;
    backingHeight?: number;
    atlasRebuilds?: number;
    glyphUploadBytes?: number;
    instanceBufferBytes?: number;
}
export interface InputModifiers {
    ctrl: boolean;
    alt: boolean;
    shift: boolean;
    meta: boolean;
}
export type PointerButton = "left" | "middle" | "right";
/** Input intents contain no browser event. Returning a route controls browser cancellation. */
export type TerminalInput = ({
    type: "key";
    key: string;
    code: string;
    repeat: boolean;
} & InputModifiers) | ({
    type: "pointer";
    button: PointerButton;
    point: Readonly<TerminalPoint>;
} & InputModifiers) | ({
    type: "wheel";
    deltaX: number;
    deltaY: number;
    deltaMode: number;
    point: Readonly<TerminalPoint> | null;
} & InputModifiers) | {
    type: "paste" | "text";
    text: string;
};
export type InputRouteValue = "continue" | "consume" | "application" | "browser";
export type TerminalActionName = "copySelection" | "pasteClipboard" | "copyOrPaste" | "clearSelection" | "scrollToLive" | "scrollLines";
export interface CopySelectionOptions {
    clear?: boolean;
}
export interface TerminalInputContext {
    readonly terminal: WebTerminalHandle;
    readonly selection: TerminalSelection;
    readonly viewport: TerminalViewport;
    readonly buffer: TerminalBuffer | null;
    readonly mouseCaptured: boolean;
    readonly historical: boolean;
    readonly readOnly: boolean;
    readonly connected: boolean;
    readonly peer: TerminalPeer;
}
/** Custom actions validate their own arguments and may complete asynchronously. */
export type InputActionHandler = (context: TerminalInputContext, args: unknown, input: Readonly<TerminalInput> | undefined) => unknown;
export type InputDecision = {
    route: InputRouteValue;
    action?: never;
    args?: never;
} | {
    action: string | InputActionHandler;
    args?: unknown;
    route?: never;
};
export type InputInterceptor = (input: Readonly<TerminalInput>, context: TerminalInputContext) => InputRouteValue | InputDecision | undefined;
export type InputBinding = {
    id: string;
    match: (input: Readonly<TerminalInput>, context: TerminalInputContext) => boolean;
    when?: (context: TerminalInputContext, input: Readonly<TerminalInput>) => boolean;
    remove?: false;
} & InputDecision;
export type InputBindingOverride = InputBinding | {
    id: string;
    remove: true;
};
export interface InputPolicyOptions {
    inputBindings?: readonly InputBindingOverride[];
    onInput?: InputInterceptor;
    actions?: Readonly<Record<string, InputActionHandler>>;
}
/** Built-ins have typed arguments/results; custom names and callbacks own their argument validation. */
export interface RunTerminalAction {
    (action: "copySelection", args?: CopySelectionOptions, input?: TerminalInput): Promise<string>;
    (action: "pasteClipboard", args?: undefined, input?: TerminalInput): Promise<string>;
    (action: "copyOrPaste", args?: undefined, input?: TerminalInput): Promise<string | undefined>;
    (action: "clearSelection" | "scrollToLive", args?: undefined, input?: TerminalInput): Promise<void>;
    (action: "scrollLines", args: number, input?: TerminalInput): Promise<void>;
    <Name extends string>(action: Name extends TerminalActionName ? never : Name, args?: unknown, input?: TerminalInput): Promise<unknown>;
    (action: InputActionHandler, args?: unknown, input?: TerminalInput): Promise<unknown>;
}
export interface SelectionRectangle {
    left: number;
    top: number;
    width: number;
    height: number;
}
export interface SelectionUIState {
    readonly selection: Readonly<TerminalSelection>;
    readonly viewport: Readonly<TerminalViewport>;
    readonly geometry: Readonly<TerminalGeometry>;
    readonly canvasSize: Readonly<TerminalSize>;
    readonly connected: boolean;
    readonly readOnly: boolean;
}
/** A frozen snapshot; preventDefault() synchronously to replace the built-in Copy button. */
export interface SelectionUIDetail extends SelectionUIState {
    readonly overlay: HTMLDivElement;
    readonly signal: AbortSignal;
    readonly runAction: RunTerminalAction;
    readonly rects: readonly Readonly<SelectionRectangle>[];
}
export type SelectionUIEvent = CustomEvent<SelectionUIDetail>;
/** Application-reported OSC 9;4 indicator, independent of shell execution. */
export type TerminalProgressState = "none" | "normal" | "error" | "indeterminate" | "warning";
/** Immutable current progress; a hidden or indeterminate indicator has no percentage. */
export interface TerminalProgress {
    readonly state: TerminalProgressState;
    readonly percentage: number | null;
}
/** Last reported OSC 133 phase. Unknown does not mean idle; commandLine is not execution. */
export type TerminalShellIntegrationPhase = "unknown" | "prompt" | "commandLine" | "executing" | "finished";
/** Current shell phase and latest reported completion status, not command history. */
export interface TerminalShellIntegration {
    readonly phase: TerminalShellIntegrationPhase;
    /** Null means no reported status, not success. Preserved across the next prompt/command. */
    readonly lastExitCode: number | null;
}
export interface WebTerminalOptions extends InputPolicyOptions {
    url: string | URL;
    /** Optional module-worker entry, resolved against the page URL. Defaults to the bundled worker. */
    workerUrl?: string | URL;
    signal?: AbortSignal;
    scale?: number | "auto";
    /** Mount-time backend selection. Defaults to auto; explicit modes never fall back. */
    renderer?: TerminalRendererPreference;
    font?: TerminalFont;
    sizing?: TerminalSizing;
    label?: string;
    /** Initial per-view input policy. Change it later with setReadOnly; not a server authorization boundary. */
    readOnly?: boolean;
    onStatus?: (message: string, level: TerminalStatusLevel) => void;
    /**
     * Receives the native WebSocket close details once, including connection failures and closes
     * before the first frame. The view is disconnected before this callback; a pending mount
     * rejects after notification. No callback is synthesized for abort, disposal, initialization
     * failure, or mount timeout, and none runs after disposal. This client never reconnects
     * automatically. Interpret application close codes in the host; even 1000 is not proof of
     * workload completion. Callback exceptions reach the host and are not retried.
     */
    onClose?: (details: TerminalCloseDetails) => void;
    onGeometry?: (geometry: TerminalGeometry) => void;
    onSizingChange?: (sizing: TerminalSizingState) => void;
    onRoleChange?: (peer: TerminalPeer) => void;
    /**
     * Receives the first authoritative presented title (including "") before mount resolves,
     * then distinct presented changes. The title getter is updated first. Titles are untrusted
     * text; render with textContent, not HTML. No notifications after disposal.
     */
    onTitleChange?: (title: string) => void;
    /**
     * Receives the first authoritative presented progress before mount resolves, then distinct
     * presented changes. Both activity getters update before either callback. Intermediate
     * states may coalesce; this is not a callback for every OSC sequence. None hides the indicator.
     * No notifications after disposal; connection loss does not manufacture a progress clear.
     */
    onProgressChange?: (progress: TerminalProgress) => void;
    /**
     * Receives the first authoritative presented shell state before mount resolves, then distinct
     * presented changes. This is not a lossless command-start/finish stream: entire commands may
     * occur between frames. Replays provide current state, never synthetic command executions.
     */
    onShellIntegrationChange?: (shellIntegration: TerminalShellIntegration) => void;
    onStats?: (stats: TerminalStats, text: string | undefined) => void;
    onViewportChange?: (viewport: TerminalViewport) => void;
    onSelectionChange?: (selection: TerminalSelection) => void;
    onInputError?: (error: Error) => void;
    /** Must return undefined synchronously; async handlers cannot claim default UI ownership. */
    onSelectionUI?: (event: SelectionUIEvent) => undefined;
}
/** Owns only the appended element and browser connection, not the server terminal. */
export interface WebTerminalHandle {
    readonly element: HTMLDivElement;
    readonly geometry: TerminalGeometry;
    readonly peer: TerminalPeer;
    readonly connected: boolean;
    /** Whether this view blocks application input, resize, and primary takeover. */
    readonly readOnly: boolean;
    /** Current presented workload title, or "" when unset/cleared. Retained on disconnect/dispose. */
    readonly title: string;
    /** Current presented progress, initially none. Retained on disconnect/dispose; check connected. */
    readonly progress: TerminalProgress;
    /** Current presented shell state, initially unknown. Retained on disconnect/dispose. */
    readonly shellIntegration: TerminalShellIntegration;
    readonly stats: TerminalStats;
    readonly screenText: string;
    readonly sizing: TerminalSizingState;
    readonly inputBindings: InputBinding[];
    readonly inputContext: TerminalInputContext;
    readonly viewport: TerminalViewport;
    readonly selection: TerminalSelection;
    runAction: RunTerminalAction;
    scrollLines(delta: number): void;
    scrollToLive(): void;
    clearSelection(): void;
    refreshSelectionUI(): void;
    copySelection(options?: CopySelectionOptions): Promise<string>;
    paste(text: string): void;
    pasteClipboard(): Promise<string>;
    /**
     * Changes this view's input policy without remounting or changing peer roles.
     * Output, history, selection and copying remain available. Cancels active gestures,
     * pending composition and clipboard paste; already dispatched commands cannot be recalled.
     * Hosts must separately enforce permissions on their per-view Hwt1PresentationAdapter.
     */
    setReadOnly(readOnly: boolean): void;
    focus(): void;
    requestPrimary(): void;
    resize(columns: number, rows: number): void;
    setSizing(sizing: TerminalSizing): void;
    resync(): void;
    dispose(): void;
}
//# sourceMappingURL=types.d.ts.map