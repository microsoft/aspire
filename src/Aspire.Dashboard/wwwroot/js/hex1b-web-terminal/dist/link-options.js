export const LINK_LIMITS = Object.freeze({
    rules: 32, pattern: 8192, chunk: 65536, totalText: 262144, cells: 262144,
    matches: 2048, resultText: 262144, cacheEntries: 2048, cacheText: 1048576, timeoutMs: 250,
});
export function validateLinkAction(action, actions) {
    if (typeof action !== "function" && !(typeof action === "string" && actions.has(action))) {
        throw new TypeError("Link action must be a registered custom action or callback");
    }
}
function object(value) {
    return value !== null && typeof value === "object" && !Array.isArray(value);
}
export function normalizeLinks(options, actions) {
    if (options === false)
        return false;
    if (options === undefined)
        return {};
    if (!object(options))
        throw new TypeError("Invalid links configuration");
    const normalized = {};
    if (options.osc8 === false)
        normalized.osc8 = false;
    else if (options.osc8 !== undefined) {
        if (!object(options.osc8))
            throw new TypeError("Invalid OSC 8 configuration");
        validateLinkAction(options.osc8.action, actions);
        normalized.osc8 = { action: options.osc8.action };
    }
    const detection = options.detection;
    if (detection === false)
        normalized.detection = false;
    else if (detection !== undefined) {
        if (!object(detection) || !Array.isArray(detection.rules))
            throw new TypeError("Invalid detection configuration");
        if (detection.rules.length > LINK_LIMITS.rules)
            throw new TypeError("Too many link rules");
        if (detection.activation !== undefined && !["modifierClick", "click"].includes(detection.activation)) {
            throw new TypeError("Invalid link activation");
        }
        if (detection.decoration !== undefined && !["always", "hover", "none"].includes(detection.decoration)) {
            throw new TypeError("Invalid link decoration");
        }
        if (detection.underlineStyle !== undefined && !["solid", "dashed"].includes(detection.underlineStyle)) {
            throw new TypeError("Invalid link underline style");
        }
        const ids = new Set();
        const rules = detection.rules.map((rule) => {
            if (!object(rule) || typeof rule.id !== "string" || !rule.id.length || rule.id.length > 256 || ids.has(rule.id)) {
                throw new TypeError("Link rule IDs must be nonempty and unique");
            }
            ids.add(rule.id);
            if (rule.enabled !== undefined && typeof rule.enabled !== "boolean")
                throw new TypeError("Invalid enabled flag");
            if (rule.text !== undefined && !["physicalRow", "logicalLine", "viewport"].includes(rule.text)) {
                throw new TypeError("Invalid link text mode");
            }
            if (rule.resolve !== undefined && typeof rule.resolve !== "function")
                throw new TypeError("Invalid resolver");
            validateLinkAction(rule.action, actions);
            const common = { id: rule.id, action: rule.action, enabled: rule.enabled ?? true,
                text: rule.text ?? "logicalLine", resolve: rule.resolve };
            if (rule.builtin !== undefined) {
                if (!["url", "uri", "absolutePath", "homePath"].includes(rule.builtin) ||
                    rule.pattern !== undefined || rule.kind !== undefined)
                    throw new TypeError("Invalid builtin rule");
                return { ...common, builtin: rule.builtin };
            }
            if (!(rule.pattern instanceof RegExp) || !["uri", "path", "custom"].includes(rule.kind)) {
                throw new TypeError("Custom rules require a RegExp and kind");
            }
            if (rule.pattern.source.length > LINK_LIMITS.pattern || /[^dgimsuv]/u.test(rule.pattern.flags)) {
                throw new TypeError("Unsupported regex flags or oversized pattern (sticky is not supported)");
            }
            return { ...common, kind: rule.kind, pattern: new RegExp(rule.pattern.source, rule.pattern.flags) };
        });
        normalized.detection = { rules, activation: detection.activation ?? "modifierClick",
            decoration: detection.decoration ?? "always", underlineStyle: detection.underlineStyle ?? "solid" };
    }
    return normalized;
}
export function isLinkActivation(input) {
    if (!object(input))
        return false;
    const value = input;
    if (!["detected", "osc8"].includes(value.source) ||
        !["uri", "path", "custom"].includes(value.kind) ||
        typeof value.text !== "string" || typeof value.target !== "string" ||
        !Number.isSafeInteger(value.revision) || value.revision < 0 ||
        !Array.isArray(value.ranges) || value.ranges.length === 0)
        return false;
    if (value.source === "osc8" ? value.ruleId !== null || value.kind !== "uri"
        : typeof value.ruleId !== "string" || value.ruleId.length === 0)
        return false;
    return value.ranges.every(range => object(range) &&
        Number.isSafeInteger(range.row) && range.row >= 0 &&
        Number.isSafeInteger(range.startColumn) && range.startColumn >= 0 &&
        Number.isSafeInteger(range.endColumn) && range.endColumn > range.startColumn);
}
export function linkAction(handler) {
    if (typeof handler !== "function")
        throw new TypeError("Expected a link action callback");
    return (context, args, input) => {
        if (!isLinkActivation(args))
            throw new TypeError("Expected a terminal link activation");
        return handler(context, args, input);
    };
}
//# sourceMappingURL=link-options.js.map