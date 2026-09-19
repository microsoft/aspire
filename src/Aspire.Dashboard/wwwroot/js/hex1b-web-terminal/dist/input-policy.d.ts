export declare const InputRoute: Readonly<{
    Continue: "continue";
    Consume: "consume";
    Application: "application";
    Browser: "browser";
}>;
export declare const TerminalAction: Readonly<{
    CopySelection: "copySelection";
    PasteClipboard: "pasteClipboard";
    CopyOrPaste: "copyOrPaste";
    ClearSelection: "clearSelection";
    ScrollToLive: "scrollToLive";
    ScrollLines: "scrollLines";
}>;
/** Fresh, inspectable defaults. Overrides are per mounted view, never global. */
export declare function defaultInputBindings(): InputBinding[];
/** Resolves intent only. DOM cancellation and action execution belong to the mounted view. */
export declare class InputPolicy {
    #private;
    constructor({ inputBindings, onInput, actions }?: InputPolicyOptions);
    get bindings(): ({
        id: string;
        match: (input: Readonly<TerminalInput>, context: TerminalInputContext) => boolean;
        when?: (context: TerminalInputContext, input: Readonly<TerminalInput>) => boolean;
        remove?: false;
        route: InputRouteValue;
        action?: never;
        args?: never;
    } | {
        id: string;
        match: (input: Readonly<TerminalInput>, context: TerminalInputContext) => boolean;
        when?: (context: TerminalInputContext, input: Readonly<TerminalInput>) => boolean;
        remove?: false;
        action: string | InputActionHandler;
        args?: unknown;
        route?: never;
    })[];
    resolve(input: Readonly<TerminalInput>, context: TerminalInputContext): InputDecision;
}
export declare function inputModifiers(event: Pick<KeyboardEvent, "ctrlKey" | "altKey" | "shiftKey" | "metaKey">): InputModifiers;
import type { InputActionHandler, InputBinding, InputDecision, InputModifiers, InputPolicyOptions, InputRouteValue, TerminalInput, TerminalInputContext } from "./types.js";
//# sourceMappingURL=input-policy.d.ts.map