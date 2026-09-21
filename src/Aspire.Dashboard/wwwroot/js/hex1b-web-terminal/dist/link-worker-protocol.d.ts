import type { TerminalLinkRule } from "./link-types.js";
export interface LinkScanRequest {
    id: number;
    rule: {
        source: string;
        flags: string;
    } | {
        builtin: NonNullable<TerminalLinkRule["builtin"]>;
    };
    chunks: {
        key: string;
        text: string;
    }[];
}
export interface LinkScanMatch {
    index: number;
    text: string;
    captures: (string | undefined)[];
    groups: Record<string, string | undefined>;
}
export declare function linkMatchTextSize(match: LinkScanMatch): number;
export interface LinkScanResponse {
    id: number;
    results?: {
        key: string;
        matches: LinkScanMatch[];
    }[];
    error?: "limit" | "worker";
    message?: string;
}
//# sourceMappingURL=link-worker-protocol.d.ts.map