import type { HyperlinkRange, TerminalCell } from "./wire-types.js";
import type { TerminalLinkAction, TerminalLinkActivation, TerminalLinkDetectionError, TerminalLinkOptions } from "./link-types.js";
export interface LinkDetectionSnapshot {
    revision: number;
    columns: number;
    rows: number;
    cells: readonly (TerminalCell | undefined)[];
    hyperlinks: readonly HyperlinkRange[];
}
export interface DetectedLink {
    id: string;
    action: TerminalLinkAction;
    activation: TerminalLinkActivation;
}
export declare class LinkDetection {
    #private;
    constructor(options: {
        workerUrl?: string | URL;
        actions: ReadonlySet<string>;
        onChange: (revision: number, links: readonly DetectedLink[]) => void;
        onError: (error: TerminalLinkDetectionError) => void;
    });
    configure(detection: TerminalLinkOptions["detection"]): void;
    update(snapshot: LinkDetectionSnapshot): void;
    advance(revision: number): void;
    clear(): void;
    dispose(): void;
}
//# sourceMappingURL=link-detection.d.ts.map