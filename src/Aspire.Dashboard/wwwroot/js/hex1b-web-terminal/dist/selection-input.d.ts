import type { MouseTrackingMode, SelectionMode, TerminalPoint, TerminalSelection } from "./types.js";
import type { CellPosition } from "./wire-types.js";
export interface GestureState {
    tracking: MouseTrackingMode;
    historical?: boolean;
    readOnly?: boolean;
    selection?: Pick<TerminalSelection, "status" | "mode" | "canExtend">;
}
export type GestureStart = {
    owner: "app";
} | {
    owner: "local";
    mode: SelectionMode;
    extend: boolean;
};
type PointEvent = Pick<MouseEvent, "clientX" | "clientY" | "shiftKey" | "altKey" | "ctrlKey">;
export declare function cellPoint(event: PointEvent, bounds: Pick<DOMRect, "left" | "top" | "width" | "height">, columns: number, rows: number, clamp?: boolean): CellPosition | null;
export declare class WheelAccumulator {
    x: number;
    y: number;
    reset(): void;
    take(event: Pick<WheelEvent, "deltaMode" | "deltaX" | "deltaY">, bounds: Pick<DOMRect, "height">, rows: number): TerminalPoint;
}
/** Ownership and granularity are latched, independent of subsequent modifiers. */
export declare class SelectionGesture {
    owner: "local" | "app" | null;
    mode: SelectionMode;
    endpoint: TerminalPoint | null;
    begin(event: Pick<MouseEvent, "button" | "shiftKey" | "altKey" | "detail">, point: TerminalPoint, { tracking, historical, readOnly, selection }: GestureState): GestureStart | null;
    move(point: TerminalPoint): TerminalPoint | null;
    scrollPoint(point: TerminalPoint): TerminalPoint | undefined;
    wheelOwner(event: Pick<MouseEvent, "shiftKey">, { tracking, historical, readOnly }: GestureState): "local" | "app";
    end(): void;
}
export {};
//# sourceMappingURL=selection-input.d.ts.map