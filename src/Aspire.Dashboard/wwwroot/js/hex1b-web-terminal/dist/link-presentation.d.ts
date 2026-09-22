import type { LinkDetectionSnapshot } from "./link-detection.js";
import type { SelectionRange, TerminalLinkUnderlineStyle } from "./types.js";
import type { FrameMetadata, TerminalCell } from "./wire-types.js";
type Cells = readonly (TerminalCell | undefined)[];
type Decorations = {
    revision: number;
    generation: number;
    serial: number;
    mask: Uint8Array;
};
/** Tracks only local presentation state; never modifies authoritative cells or runs matching. */
export declare class LinkPresentation {
    enabled: boolean;
    generation: number;
    private presented?;
    private sent?;
    private forceSnapshot;
    private decorations?;
    private pending?;
    private lastSerial;
    configure(enabled: boolean, generation: number): boolean;
    private clear;
    prepare(cells: Cells, metadata: FrameMetadata): void;
    present(cells: Cells, metadata: FrameMetadata): {
        linkGeneration?: number;
        linkSnapshot?: LinkDetectionSnapshot;
    };
    snapshot(): LinkDetectionSnapshot | undefined;
    accept(revision: number, generation: number, serial: number, ranges: readonly SelectionRange[], busy: boolean, underlineStyle?: TerminalLinkUnderlineStyle): boolean;
    submission(): {
        mask?: Uint8Array;
        acknowledgement?: Decorations;
    };
    acknowledge(submitted: Decorations | undefined, busy: boolean): {
        revision: number;
        generation: number;
        serial: number;
    } | undefined;
}
export {};
//# sourceMappingURL=link-presentation.d.ts.map