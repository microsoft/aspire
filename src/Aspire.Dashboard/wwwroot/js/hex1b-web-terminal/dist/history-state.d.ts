/** Commands contain producer identities; text and ranges are never inferred from painted cells. */
export declare class HistoryState {
    #private;
    constructor(send: (command: TerminalCommand) => void, change?: () => void);
    get viewport(): HistoryViewport;
    get selection(): HistorySelectionState;
    accept(history: HistoryMetadata | null, revision: number): boolean;
    begin(point: TerminalPoint, { mode, extend }: {
        mode: SelectionMode;
        extend?: boolean;
    }): void;
    extend(point: TerminalPoint): void;
    scroll(delta: number, endpoint?: TerminalPoint): void;
    live(): void;
    clear(): void;
    endGesture(cancelled?: boolean): void;
    cancelCopy(error: unknown): void;
    copy(): Promise<string>;
    disconnect(): void;
}
import type { SelectionMode, TerminalPoint, TerminalSelection, TerminalViewport } from "./types.js";
import type { HistoryMetadata, TerminalCommand } from "./wire-types.js";
type OmitEach<T, K extends PropertyKey> = T extends unknown ? Omit<T, K> : never;
type HistoryViewport = OmitEach<TerminalViewport, "followTail" | "offset">;
type HistorySelectionState = OmitEach<TerminalSelection, "active" | "pending" | "copying" | "copyError">;
export {};
//# sourceMappingURL=history-state.d.ts.map