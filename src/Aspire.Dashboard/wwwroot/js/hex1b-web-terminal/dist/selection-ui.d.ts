import type { RunTerminalAction, SelectionRange, SelectionUIState, TerminalGeometry, TerminalSize, WebTerminalOptions } from "./types.js";
export declare function sameSelectionUIState(a: SelectionUIState | undefined, b: SelectionUIState): boolean;
export declare function selectionRectangles(ranges: readonly SelectionRange[], geometry: TerminalGeometry, canvasSize: TerminalSize): Readonly<{
    left: number;
    top: number;
    width: number;
    height: number;
}>[];
/** Owns UI notification/default rendering, not terminal selection or clipboard state. */
export declare class SelectionUI {
    #private;
    constructor({ element, overlay, button, signal, getState, runAction, onSelectionUI, reportError }: {
        element: HTMLDivElement;
        overlay: HTMLDivElement;
        button: HTMLButtonElement;
        signal: AbortSignal;
        getState: () => SelectionUIState;
        runAction: RunTerminalAction;
        onSelectionUI?: WebTerminalOptions["onSelectionUI"];
        reportError: (error: unknown) => void;
    });
    refresh(force?: boolean): void;
}
//# sourceMappingURL=selection-ui.d.ts.map