import type { CopySelectionOptions, InputActionHandler, InputBinding, TerminalActionName, TerminalGeometry, TerminalInput, TerminalInputContext, TerminalPeer, TerminalSelection, TerminalSizing, TerminalSizingState, TerminalStats, TerminalViewport, TerminalProgress, TerminalShellIntegration, WebTerminalHandle, WebTerminalOptions } from "./types.js";
export { InputRoute, TerminalAction, defaultInputBindings } from "./input-policy.js";
/**
 * First-party HWT1 client. Owns only the element it appends, not the caller's
 * container or the server terminal. The HWT1 wire and this spike API evolve together.
 */
export declare class WebTerminal implements WebTerminalHandle {
    #private;
    readonly element: HTMLDivElement;
    /** Resolves after a connected terminal frame is presented. Supply signal to cancel mounting. */
    static mount(container: HTMLElement, options: WebTerminalOptions): Promise<WebTerminal>;
    private constructor();
    get geometry(): TerminalGeometry;
    get peer(): TerminalPeer;
    get connected(): boolean;
    get readOnly(): boolean;
    /** Current presented workload title; retained on disconnect/dispose. Treat as untrusted text. */
    get title(): string;
    get progress(): TerminalProgress;
    get shellIntegration(): TerminalShellIntegration;
    get stats(): TerminalStats;
    get screenText(): string;
    get sizing(): TerminalSizingState;
    get inputBindings(): InputBinding[];
    get viewport(): TerminalViewport;
    get selection(): TerminalSelection;
    scrollLines(delta: number): void;
    scrollToLive(): void;
    clearSelection(): void;
    /** Re-notifies selection UI hosts after an external styling/policy change. */
    refreshSelectionUI(): void;
    copySelection({ clear }?: CopySelectionOptions): Promise<string>;
    get inputContext(): TerminalInputContext;
    /** Named actions are shared by controls and bindings. Custom callbacks receive context, args, and input. */
    runAction(action: "copySelection", args?: CopySelectionOptions, input?: TerminalInput): Promise<string>;
    runAction(action: "pasteClipboard", args?: undefined, input?: TerminalInput): Promise<string>;
    runAction(action: "copyOrPaste", args?: undefined, input?: TerminalInput): Promise<string | undefined>;
    runAction(action: "clearSelection" | "scrollToLive", args?: undefined, input?: TerminalInput): Promise<void>;
    runAction(action: "scrollLines", args: number, input?: TerminalInput): Promise<void>;
    runAction<Name extends string>(action: Name extends TerminalActionName ? never : Name, args?: unknown, input?: TerminalInput): Promise<unknown>;
    runAction(action: InputActionHandler, args?: unknown, input?: TerminalInput): Promise<unknown>;
    /** Sends an explicit paste through the producer's mode-aware input encoder. */
    paste(text: string): void;
    pasteClipboard(): Promise<string>;
    /** Changes per-view input policy without reconnecting; server authorization remains host-owned. */
    setReadOnly(readOnly: boolean): void;
    focus(): void;
    /** Request HMP1 primary explicitly; peer notifications confirm the result. */
    requestPrimary(): void;
    /** Request a grid; never reflow locally before the authoritative response. */
    resize(columns: number, rows: number): void;
    /** Change the primary's sizing policy; applied grid dimensions still come from the server. */
    setSizing(sizing: TerminalSizing): void;
    resync(): void;
    /** Detach this view. The server-side shared terminal is not terminated. */
    dispose(): void;
}
//# sourceMappingURL=web-terminal.d.ts.map