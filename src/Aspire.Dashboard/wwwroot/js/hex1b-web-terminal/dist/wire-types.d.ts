import type { InputModifiers, PointerButton, SelectionMode, SelectionRange, TerminalBuffer, TerminalFont, TerminalGeometry, TerminalPeer, TerminalSize, TerminalStats, TerminalRendererPreference, TerminalStatusLevel, TerminalProgress, TerminalShellIntegration } from "./types.js";
export type SelectionText = {
    status: "valid";
    text: string;
} | {
    status: "none" | "invalidated";
    text: null;
};
export type HistorySelection = SelectionText & {
    requestId: number;
    mode: SelectionMode;
    ranges: SelectionRange[];
};
export interface HistoryMetadata {
    generation: string;
    buffer: TerminalBuffer;
    totalRows: number;
    liveTop: number;
    top: number;
    following: boolean;
    requestId: number;
    rowIds: string[];
    selection: HistorySelection;
    copy: (SelectionText & {
        requestId: number;
    }) | null;
}
export interface TerminalCell {
    index: number;
    foreground: number;
    background: number;
    underlineColor: number;
    attributes: number;
    width: number;
    underlineStyle: number;
    text: string;
}
export interface HyperlinkRange {
    row: number;
    startColumn: number;
    endColumn: number;
    uri: string;
}
export interface ImageMetadata {
    key: string;
    width: number;
    height: number;
    byteLength: number;
    format: "rgba" | "png";
}
export interface FrameImage extends ImageMetadata {
    bytes: Uint8Array<ArrayBuffer>;
}
export interface ImagePlacement {
    key: string;
    kind: "kgp" | "sixel";
    x: number;
    y: number;
    width: number;
    height: number;
    sourceX: number;
    sourceY: number;
    sourceWidth: number;
    sourceHeight: number;
    clipX: number;
    clipY: number;
    clipWidth: number;
    clipHeight: number;
    z: number;
}
export interface FrameMetadata extends TerminalGeometry {
    version: 1;
    full: boolean;
    revision: number;
    baseRevision: number;
    peer: TerminalPeer;
    history: HistoryMetadata | null;
    title: string;
    progress: TerminalProgress;
    shellIntegration: TerminalShellIntegration;
    defaultBackground?: number;
    defaultForeground?: number;
    cursor: {
        visible: boolean;
        x: number;
        y: number;
        shape: number;
    };
    images: ImageMetadata[];
    retainedImages: string[];
    placements: ImagePlacement[];
    warnings: string[];
    hyperlinks: HyperlinkRange[];
    stats: {
        workloadBytes: number;
        outputBatches: number;
        captureMs: number;
        elapsedMs: number;
    };
}
export interface TerminalFrame {
    metadata: FrameMetadata;
    cells: TerminalCell[];
    images: FrameImage[];
}
export type CellPosition = {
    x: number;
    y: number;
} & Partial<Pick<InputModifiers, "ctrl" | "alt" | "shift">>;
export type MouseButton = PointerButton | "none" | "wheelUp" | "wheelDown" | "wheelLeft" | "wheelRight";
export type MouseCommand = {
    type: "mouse";
    action: "down" | "up" | "move" | "wheel";
    button: MouseButton;
    count?: number;
} & CellPosition;
export type InputCommand = {
    type: "input" | "paste";
    text: string;
} | {
    type: "key";
    key: string;
    ctrl: boolean;
    alt: boolean;
    shift: boolean;
} | MouseCommand;
export type TerminalCommand = InputCommand | {
    type: "viewport";
    requestId: number;
    delta?: number;
    live?: boolean;
    extend?: {
        row: number;
        column: number;
    };
} | {
    type: "selection";
    action: "clear";
    requestId: number;
} | {
    type: "selection";
    action: "start" | "extend";
    mode: SelectionMode;
    requestId: number;
    generation: string;
    rowId: string;
    column: number;
} | {
    type: "copy";
    requestId: number;
    selectionRequestId: number;
    generation: string;
} | {
    type: "resize" | "requestPrimary";
    columns: number;
    rows: number;
} | {
    type: "resync";
} | {
    type: "ack";
    revision: number;
};
export type WorkerInputMessage = {
    type: "init";
    canvas: OffscreenCanvas;
    url: string;
    scale: number;
    font: TerminalFont;
    renderer: TerminalRendererPreference;
} | ({
    type: "viewport";
} & TerminalSize) | {
    type: "command";
    command: TerminalCommand;
} | {
    type: "stop";
};
export interface WorkerStats extends TerminalStats {
    revision: number;
    fullFrames: number;
    frames: number;
    presentations: number;
    changedCells: number;
    lastChangedCells: number;
    discardedFrames: number;
    imageCount: number;
    textureBytes: number;
    atlasGlyphs: number;
    atlasBytes: number;
    bytesReceived: number;
    imageUploadBytes: number;
    imagePayloadBytes: number;
    gpu: "initializing" | "ready" | "error" | "stopped";
    connected: boolean;
    warnings: string[];
    fps: number;
    receivedKBps: number;
    workloadMBps: number;
    captureMs: number;
    rendererCpuMs: number;
    preparationCpuMs: number;
    workloadBytes: number;
    outputBatches: number;
    serverElapsedMs: number;
    history?: HistoryMetadata | null;
}
export type WorkerOutputMessage = {
    type: "connected" | "disconnected";
} | {
    type: "status";
    message: string;
    level: TerminalStatusLevel;
} | ({
    type: "geometry";
    peer: TerminalPeer;
    history: HistoryMetadata | null;
    revision: number;
    title: string;
    progress: TerminalProgress;
    shellIntegration: TerminalShellIntegration;
    text: string;
    hyperlinks: HyperlinkRange[];
} & TerminalGeometry) | {
    type: "history";
    history: HistoryMetadata | null;
    revision: number;
    text: string;
} | {
    type: "stats";
    stats: WorkerStats;
    text?: string;
};
//# sourceMappingURL=wire-types.d.ts.map