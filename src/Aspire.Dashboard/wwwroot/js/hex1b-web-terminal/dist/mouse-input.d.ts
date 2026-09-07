import type { GestureState } from "./selection-input.js";
import type { InputDecision, MouseTrackingMode, SelectionMode, TerminalInput, TerminalPoint } from "./types.js";
import type { MouseCommand } from "./wire-types.js";
interface MouseInspection {
    state?: () => Omit<GestureState, "tracking">;
    begin?: (point: TerminalPoint, selection: {
        mode: SelectionMode;
        extend: boolean;
    }) => void;
    extend?: (point: TerminalPoint) => void;
    scroll?: (delta: number, endpoint?: TerminalPoint) => void;
    end?: (cancelled: boolean) => void;
    resolve?: (input: TerminalInput) => InputDecision;
    execute?: (decision: Extract<InputDecision, {
        action: unknown;
    }>, input: TerminalInput) => void;
}
export interface MouseCapture {
    update(columns: number, rows: number, tracking: MouseTrackingMode): void;
    cancel(): void;
    dispose(): void;
}
/** Capture input intent only; the server chooses and encodes the mouse protocol. */
export declare function captureMouse(canvas: HTMLCanvasElement, send: (command: MouseCommand) => void, focus: () => void, inspection?: MouseInspection): MouseCapture;
export {};
//# sourceMappingURL=mouse-input.d.ts.map