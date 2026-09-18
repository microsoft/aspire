import type { InputActionHandler, TerminalInput, TerminalInputContext } from "./types.js";
import type { TerminalLinkAction, TerminalLinkActivation, TerminalLinkOptions } from "./link-types.js";
export declare const LINK_LIMITS: Readonly<{
    rules: 32;
    pattern: 8192;
    chunk: 65536;
    totalText: 262144;
    cells: 262144;
    matches: 2048;
    resultText: 262144;
    cacheEntries: 2048;
    cacheText: 1048576;
    timeoutMs: 250;
}>;
export declare function validateLinkAction(action: unknown, actions: ReadonlySet<string>): asserts action is TerminalLinkAction;
export declare function normalizeLinks(options: false | TerminalLinkOptions | undefined, actions: ReadonlySet<string>): false | TerminalLinkOptions;
export declare function isLinkActivation(input: unknown): input is TerminalLinkActivation;
export declare function linkAction(handler: (context: TerminalInputContext, link: TerminalLinkActivation, input: Readonly<TerminalInput> | undefined) => unknown): InputActionHandler;
//# sourceMappingURL=link-options.d.ts.map