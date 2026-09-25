import type { InputActionHandler, SelectionRange } from "./types.js";
export type TerminalLinkTextMode = "physicalRow" | "logicalLine" | "viewport";
export type TerminalLinkKind = "uri" | "path" | "custom";
export type TerminalLinkAction = string | InputActionHandler;
export type TerminalLinkUnderlineStyle = "solid" | "dashed";
export interface TerminalLinkOptions {
    osc8?: false | {
        action: TerminalLinkAction;
    };
    detection?: false | {
        rules: readonly TerminalLinkRule[];
        activation?: "modifierClick" | "click";
        /** When inferred underlines are visible. Defaults to always. */
        decoration?: "always" | "hover" | "none";
        /** Inferred underline appearance. Defaults to solid; authored SGR styling takes precedence. */
        underlineStyle?: TerminalLinkUnderlineStyle;
    };
}
export interface TerminalLinkRuleOptions {
    id: string;
    enabled?: boolean;
    text?: TerminalLinkTextMode;
    action: TerminalLinkAction;
    /** Synchronous, fast host callback. Unlike regex execution, this cannot be preempted. */
    resolve?: (match: TerminalLinkMatch) => TerminalLinkResolution | null;
}
export type TerminalLinkRule = TerminalLinkRuleOptions & ({
    builtin: "url" | "uri" | "absolutePath" | "homePath";
    pattern?: never;
    kind?: never;
} | {
    pattern: RegExp;
    kind: TerminalLinkKind;
    builtin?: never;
});
export interface TerminalLinkTextChunk {
    readonly text: string;
    readonly mode: TerminalLinkTextMode;
    readonly start: "complete" | "clipped" | "unknown";
    readonly end: "complete" | "clipped" | "unknown";
}
export interface TerminalLinkMatch {
    readonly text: string;
    /** UTF-16 offset within chunk.text. */
    readonly index: number;
    readonly captures: readonly (string | undefined)[];
    readonly groups: Readonly<Record<string, string | undefined>>;
    readonly chunk: TerminalLinkTextChunk;
}
export interface TerminalLinkResolution {
    readonly target: string;
    readonly action?: TerminalLinkAction;
    readonly data?: unknown;
}
export interface TerminalLinkActivation {
    readonly source: "detected" | "osc8";
    readonly ruleId: string | null;
    readonly kind: TerminalLinkKind;
    readonly text: string;
    readonly target: string;
    readonly ranges: readonly Readonly<SelectionRange>[];
    readonly revision: number;
    /** Consumer-owned payload; core snapshots are frozen, but this object is not. */
    readonly data?: unknown;
}
export interface TerminalLinkDetectionError {
    readonly code: "timeout" | "limit" | "resolver" | "worker";
    readonly ruleId: string | null;
    readonly revision: number;
    readonly message: string;
}
//# sourceMappingURL=link-types.d.ts.map